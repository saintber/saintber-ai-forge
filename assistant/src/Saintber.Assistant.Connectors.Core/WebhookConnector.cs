using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Saintber.Assistant.Abstractions;
namespace Saintber.Assistant.Connectors.Core;

public sealed class WebhookConnector : IConnector, IWebhookReceiver
{
    private readonly IWebhookInbound _inbound;
    private readonly IMessagingPlatform _platform;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly EventRegistry _registry;
    private readonly Channel<Work> _channel;
    private readonly int _workerCount;
    private readonly TimeSpan _maxQueueAge;
    private readonly TimeSpan _activityTimeout;
    private readonly WebhookConnectorOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<ExternalKey, List<ActivityEntry>> _activities = new();
    private static readonly AsyncLocal<WorkLease?> CurrentLease = new();
    private readonly HashSet<WorkLease> _leases = [];
    private readonly HashSet<DeliveryEntry> _deliveries = [];
    private readonly HashSet<ActivityCallEntry> _activityCalls = [];
    private readonly HashSet<Task> _cleanupTasks = [];
    private readonly CancellationTokenSource _stopInterrupt = new();
    private readonly List<CancellationTokenRegistration> _stopRegistrations = [];
    private Task? _stopTask;
    private bool _deliveryClosed;
    private bool _degraded;
    private int _overrunWorkers;
    private Task[] _workers = [];
    private IInboundMessageHandler? _handler;
    private State _state = State.Stopped;
    private bool _started;
    public string ConnectorType { get; }
    public string InstanceId { get; }
    public TimeSpan StopBudget => _options.StopGrace + _options.JoinTimeout;
    public int PendingCount => _channel.Reader.Count;
    public WebhookConnector(string type, string instanceId, IWebhookInbound inbound, IMessagingPlatform platform,
        TimeProvider clock, ILogger logger, int workers, int pending, TimeSpan maxQueueAge, TimeSpan activityTimeout)
        : this(type, instanceId, inbound, platform, clock, logger, new WebhookConnectorOptions { MaxConcurrency=workers, MaxPending=pending, MaxQueueAge=maxQueueAge, ActivityTimeout=activityTimeout }) { }
    public WebhookConnector(string type, string instanceId, IWebhookInbound inbound, IMessagingPlatform platform,
        TimeProvider clock, ILogger logger, WebhookConnectorOptions options)
    {
        if (options.MaxConcurrency < 1) throw new ArgumentException("Work:MaxConcurrency");
        ConnectorType = type; InstanceId = instanceId; _inbound = inbound; _platform = platform;
        _clock = clock; _logger = logger; _options = options; _workerCount = options.MaxConcurrency; _maxQueueAge = options.MaxQueueAge; _activityTimeout = options.ActivityTimeout;
        _registry = new(clock, options.DedupTtl, options.DedupMaxEntries, logger);
        _channel = Channel.CreateBounded<Work>(new BoundedChannelOptions(options.MaxPending) { FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    }
    public Task StartAsync(IInboundMessageHandler handler, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_registry.Gate)
        {
            if (_started || _stopTask is not null) throw new InvalidOperationException("Connector cannot be started again");
            _started = true; _handler = handler; _state = State.Running;
            _workers = Enumerable.Range(0, _workerCount).Select(id => Task.Run(() => WorkerAsync(id))).ToArray();
        }
        return Task.CompletedTask;
    }
    public Task<WebhookResult> ReceiveAsync(WebhookRequest request, CancellationToken cancellationToken)
    {
        lock (_registry.Gate) { if (_state != State.Running) return Task.FromResult(new WebhookResult(503)); }
        try
        {
            _inbound.Verify(request);
            var events = _inbound.Parse(request);
            var dropped = 0;
            lock (_registry.Gate)
            {
                if (_state != State.Running) return Task.FromResult(new WebhookResult(503));
                foreach (var item in events)
                {
                    if (_degraded)
                    {
                        dropped++;
                        _logger.LogWarning("Degraded admission discarded event {EventId}", item.EventId);
                        continue;
                    }
                    _registry.TryAdmit(item.EventId, item.Envelope.Message, item.Envelope.Chat, item.ReplyToken, item.EventTime, () =>
                    {
                        if (_channel.Writer.TryWrite(new Work(item, _clock.GetUtcNow()))) return true;
                        dropped++; return false;
                    });
                }
            }
            if (dropped > 0) _logger.LogWarning("Overload discarded {Count} events", dropped);
            return Task.FromResult(new WebhookResult(200));
        }
        catch (WebhookVerificationException) { return Task.FromResult(new WebhookResult(401)); }
        catch (ConnectorPayloadException) { return Task.FromResult(new WebhookResult(400)); }
    }
    private async Task WorkerAsync(int workerId)
    {
        _logger.LogInformation("Worker {WorkerId} idle", workerId);
        try
        {
            await foreach (var work in _channel.Reader.ReadAllAsync(_lifetime.Token))
            {
                CancellationTokenSource budget;
                CancellationTokenSource linked;
                WorkLease lease;
                lock (_registry.Gate)
                {
                    if (_state != State.Running) { _logger.LogWarning("Pending work discarded during stop for {EventId}", work.Item.EventId); continue; }
                    if (_clock.GetUtcNow() - work.EnqueuedAt > _maxQueueAge) { _logger.LogWarning("Pending work discarded for queue age for {EventId}", work.Item.EventId); continue; }
                    budget = new CancellationTokenSource(_options.EventTimeout, _clock);
                    linked = CancellationTokenSource.CreateLinkedTokenSource(budget.Token, _lifetime.Token);
                    lease = new WorkLease(this, linked, budget, work.Item.EventId);
                    _leases.Add(lease);
                    _logger.LogInformation("Worker {WorkerId} running event {EventId}", workerId, work.Item.EventId);
                }
                using var budgetLifetime = budget;
                using var linkedLifetime = linked;
                using var cancellation = linked.Token.Register(() => RevokeLease(lease));
                var overrun = false;
                try
                {
                    if (_platform.Capabilities.Activity) await StartActivityAsync(work.Item.Envelope.Message, work.Item.Envelope.Chat, linked.Token, lease);
                    linked.Token.ThrowIfCancellationRequested();
                    var handler = HandleWithLeaseAsync(work.Item.Envelope, linked.Token, lease);
                    try { await handler.WaitAsync(linked.Token); }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested && !handler.IsCompleted)
                    {
                        try { await handler.WaitAsync(_options.OverrunGrace, _clock, _lifetime.Token); }
                        catch (TimeoutException)
                        {
                            lock (_registry.Gate)
                            {
                                // A returned handler must not be classified as overrun just because its continuation was delayed.
                                if (!handler.IsCompleted)
                                {
                                    overrun = true;
                                    lease.Revoked = true;
                                    _overrunWorkers++;
                                    _logger.LogError("Worker {WorkerId} overrun for event {EventId}", workerId, work.Item.EventId);
                                    if (_state == State.Running && _overrunWorkers == _workerCount)
                                    {
                                        _degraded = true;
                                        _logger.LogWarning("Degraded entered after event {EventId}", work.Item.EventId);
                                    }
                                }
                            }
                            // Keep this worker occupied. Replacement workers would exceed the concurrency limit.
                            await handler;
                        }
                        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { await handler; }
                    }
                }
                catch (Exception) { _logger.LogError("Event work failed for {EventId}", work.Item.EventId); }
                finally
                {
                    lock (_registry.Gate)
                    {
                        lease.Revoked = true;
                        _leases.Remove(lease);
                        if (overrun)
                        {
                            _overrunWorkers--;
                            if (_state == State.Running && _degraded)
                            {
                                _degraded = false;
                                _logger.LogInformation("Degraded exited after event {EventId}", work.Item.EventId);
                            }
                        }
                        _logger.LogInformation("Worker {WorkerId} idle after event {EventId}", workerId, work.Item.EventId);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task HandleWithLeaseAsync(InboundEnvelope envelope, CancellationToken cancellationToken, WorkLease lease)
    {
        var previous = CurrentLease.Value;
        CurrentLease.Value = lease;
        try
        {
            Task call;
            lock (_registry.Gate)
            {
                if (!CanDeliver(lease)) throw new OperationCanceledException(cancellationToken);
                call = _handler!.HandleAsync(envelope, cancellationToken);
            }
            await call;
        }
        finally
        {
            RevokeLease(lease);
            CurrentLease.Value = previous;
        }
    }
    private void RevokeLease(WorkLease lease)
    {
        lock (_registry.Gate) lease.Revoked = true;
    }
    private bool CanDeliver(WorkLease? lease)
    {
        if (_deliveryClosed || _state == State.Stopped) return false;
        if (lease is not null && ReferenceEquals(lease.Owner, this))
            return !lease.Revoked && !lease.Token.IsCancellationRequested;
        return _state == State.Running;
    }
    private void CloseDeliveries()
    {
        CancellationTokenSource[] sources;
        lock (_registry.Gate)
        {
            _deliveryClosed = true;
            foreach (var lease in _leases) lease.Revoked = true;
            sources = new[] { _lifetime }.Concat(_leases.SelectMany(x => new[] { x.Cancellation, x.Budget }))
                .Concat(_deliveries.SelectMany(x => new[] { x.Cancellation, x.Timeout })).Distinct().ToArray();
        }
        // CancelAsync sets the token first and runs arbitrary callbacks without blocking the stopping thread.
        foreach (var source in sources) CancelAndObserve(source);
    }
    private void CancelAndObserve(CancellationTokenSource source)
    {
        Task cancellation;
        try { cancellation = source.CancelAsync(); }
        catch (ObjectDisposedException) { return; }
        catch (Exception) { _logger.LogWarning("Work cancellation failed"); return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_registry.Gate) _cleanupTasks.Add(completion.Task);
        _ = CompleteCancellationAsync(cancellation, completion);
    }
    private async Task CompleteCancellationAsync(Task cancellation, TaskCompletionSource completion)
    {
        try { await cancellation; }
        catch (Exception) { _logger.LogWarning("Work cancellation callback failed"); }
        finally
        {
            lock (_registry.Gate)
            {
                completion.TrySetResult();
                _cleanupTasks.Remove(completion.Task);
            }
        }
    }
    private async Task StartActivityAsync(ExternalKey message, ExternalKey chat, CancellationToken cancellationToken, WorkLease lease)
    {
        using var timeout = new CancellationTokenSource(_activityTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        ActivityCallEntry? entry = null;
        IAsyncDisposable? resource;
        try
        {
            lock (_registry.Gate)
            {
                if (!CanDeliver(lease) || linked.IsCancellationRequested) return;
                entry = new ActivityCallEntry(_platform.StartActivityAsync(chat, linked.Token));
                _activityCalls.Add(entry);
            }
            resource = await entry.Call.WaitAsync(linked.Token);
        }
        catch (Exception)
        {
            _logger.LogWarning("Activity call failed or timed out for {MessageKey}", message.CanonicalValue);
            if (entry is not null) _ = ObserveLateActivityAsync(message, entry);
            return;
        }
        try
        {
            if (resource is null) return;
            var activity = new ActivityEntry(resource, message);
            Task? disposal = null;
            lock (_registry.Gate)
            {
                if (_state == State.Running && !_deliveryClosed && !linked.IsCancellationRequested)
                {
                    if (!_activities.TryGetValue(message, out var entries)) _activities.Add(message, entries = []);
                    entries.Add(activity);
                    activity.Timer = _clock.CreateTimer(_ => { _ = ExpireActivityAsync(message, activity); }, null, _options.ActivityMaxDuration, Timeout.InfiniteTimeSpan);
                }
                else disposal = DisposeActivityAsync(activity);
            }
            if (disposal is not null) await disposal;
        }
        finally { SettleActivityCall(entry); }
    }
    private async Task ExpireActivityAsync(ExternalKey message, ActivityEntry entry)
    {
        Task cleanup;
        lock (_registry.Gate)
        {
            if (_activities.TryGetValue(message, out var entries))
            {
                entries.Remove(entry);
                if (entries.Count == 0) _activities.Remove(message);
            }
            cleanup = DisposeActivityAsync(entry);
        }
        await cleanup;
    }
    private Task DisposeActivityAsync(ActivityEntry entry)
    {
        lock (_registry.Gate)
        {
            if (entry.DisposeTask is not null) return entry.DisposeTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            entry.DisposeTask = completion.Task;
            entry.Timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _cleanupTasks.Add(completion.Task);
            _ = Task.Run(() => CompleteActivityDisposalAsync(entry, completion));
            return completion.Task;
        }
    }
    private async Task CompleteActivityDisposalAsync(ActivityEntry entry, TaskCompletionSource completion)
    {
        try
        {
            try { if (entry.Timer is not null) await entry.Timer.DisposeAsync(); }
            catch (Exception) { _logger.LogWarning("Activity timer cleanup failed for {MessageKey}", entry.Message.CanonicalValue); }
            try { await entry.Resource.DisposeAsync(); }
            catch (Exception) { _logger.LogWarning("Activity cleanup failed for {MessageKey}", entry.Message.CanonicalValue); }
        }
        finally
        {
            lock (_registry.Gate)
            {
                completion.TrySetResult();
                _cleanupTasks.Remove(completion.Task);
            }
        }
    }
    private Task EndActivitiesAsync(ExternalKey message)
    {
        lock (_registry.Gate)
        {
            _activities.Remove(message, out var entries);
            return entries is null ? Task.CompletedTask : Task.WhenAll(entries.Select(DisposeActivityAsync));
        }
    }
    private async Task ObserveLateActivityAsync(ExternalKey message, ActivityCallEntry entry)
    {
        try
        {
            var result = await entry.Call;
            if (result is not null) await DisposeActivityAsync(new ActivityEntry(result, message));
        }
        catch (Exception) { _logger.LogWarning("Late activity cleanup failed for {MessageKey}", message.CanonicalValue); }
        finally { SettleActivityCall(entry); }
    }
    private void SettleActivityCall(ActivityCallEntry entry)
    {
        lock (_registry.Gate)
        {
            entry.Settled.TrySetResult();
            _activityCalls.Remove(entry);
        }
    }
    public async Task<DeliveryAcceptance> DeliverAsync(OutboundEnvelope envelope, CancellationToken cancellationToken)
    {
        var lease = CurrentLease.Value;
        CancellationTokenSource? timeout = null;
        CancellationTokenSource? linked = null;
        DeliveryEntry? delivery = null;
        var accepted = false;
        try
        {
            lock (_registry.Gate)
            {
                if (!CanDeliver(lease)) return DeliveryAcceptance.Unavailable;
                accepted = true;
                timeout = new CancellationTokenSource(_options.SendTimeout, _clock);
                linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken, _lifetime.Token,
                    lease is not null && ReferenceEquals(lease.Owner, this) ? lease.Token : CancellationToken.None);
                var reply = envelope.InReplyTo is null ? null : _registry.FindReply(envelope.InReplyTo);
                if (reply is not null && !reply.Chat.Equals(envelope.Chat)) _logger.LogWarning("Reply chat mismatch; using envelope destination");
                string? replyToken = null;
                if (reply is not null && reply.Chat.Equals(envelope.Chat) && !reply.Used && reply.Token is not null
                    && _platform.Capabilities.Reply && _clock.GetUtcNow() - reply.ValidFrom < _platform.Capabilities.ReplyValidity)
                { reply.Used = true; replyToken = reply.Token; }
                // Platform invocation and in-flight registration are ordered with stop and lease checks by this gate.
                var call = replyToken is not null
                    ? _platform.ReplyAsync(replyToken, envelope.Content, linked.Token)
                    : _platform.Capabilities.Push
                        ? _platform.PushAsync(envelope.Chat, envelope.Content, linked.Token)
                        : Task.CompletedTask;
                if (replyToken is null && !_platform.Capabilities.Push) _logger.LogWarning("Message undeliverable");
                delivery = new DeliveryEntry(call, linked, timeout);
                _deliveries.Add(delivery);
            }
            _ = ObserveDeliveryAsync(delivery, envelope.InReplyTo?.CanonicalValue ?? envelope.Chat.CanonicalValue);
            await delivery.Call.WaitAsync(delivery.Token);
        }
        catch (Exception)
        {
            if (delivery is null) _logger.LogError("Delivery failed for {MessageKey}", envelope.InReplyTo?.CanonicalValue ?? envelope.Chat.CanonicalValue);
            else if (!delivery.Call.IsCompleted)
            {
                delivery.WaitAbandoned = true;
                _logger.LogWarning("Delivery wait cancelled for {MessageKey}", envelope.InReplyTo?.CanonicalValue ?? envelope.Chat.CanonicalValue);
            }
        }
        finally
        {
            if (delivery is null) { linked?.Dispose(); timeout?.Dispose(); }
            if (accepted && envelope.InReplyTo is not null) await EndActivitiesAsync(envelope.InReplyTo);
        }
        return DeliveryAcceptance.Accepted;
    }
    private async Task ObserveDeliveryAsync(DeliveryEntry entry, string messageKey)
    {
        try { await entry.Call; }
        catch (Exception)
        {
            _logger.LogError(entry.WaitAbandoned ? "Late delivery failed for {MessageKey}" : "Delivery failed for {MessageKey}", messageKey);
        }
        finally
        {
            entry.Cancellation.Dispose();
            entry.Timeout.Dispose();
            lock (_registry.Gate)
            {
                entry.Settled.TrySetResult();
                _deliveries.Remove(entry);
            }
        }
    }
    public Task StopAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource? completion = null;
        Task stop;
        lock (_registry.Gate)
        {
            if (_stopTask is null)
            {
                _state = State.Stopping;
                _channel.Writer.TryComplete();
                while (_channel.Reader.TryRead(out var pending))
                    _logger.LogWarning("Pending work discarded during stop for {EventId}", pending.Item.EventId);
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopTask = completion.Task;
            }
            if (cancellationToken.CanBeCanceled && !_stopTask.IsCompleted)
                _stopRegistrations.Add(cancellationToken.Register(() => _stopInterrupt.Cancel()));
            stop = _stopTask;
        }
        if (completion is not null) _ = StopCoreAsync(completion);
        return stop;
    }
    private async Task StopCoreAsync(TaskCompletionSource completion)
    {
        var interrupted = false;
        Task registryCleanup = Task.CompletedTask;
        try
        {
            try { await WaitForGraceAsync().WaitAsync(_options.StopGrace, _clock, _stopInterrupt.Token); }
            catch (TimeoutException) { _logger.LogInformation("Stop grace expired"); }
            catch (OperationCanceledException) { interrupted = true; }
            CloseDeliveries();
            lock (_registry.Gate)
            {
                registryCleanup = _registry.DisposeAsync().AsTask();
                foreach (var activity in _activities.Values.SelectMany(x => x)) _ = DisposeActivityAsync(activity);
                _activities.Clear();
            }
            if (interrupted || _stopInterrupt.IsCancellationRequested) interrupted = true;
            else
            {
                try { await WaitForJoinAsync(registryCleanup).WaitAsync(_options.JoinTimeout, _clock, _stopInterrupt.Token); }
                catch (TimeoutException) { _logger.LogWarning("Worker, delivery or cleanup join abandoned"); }
                catch (OperationCanceledException) { interrupted = true; }
            }
        }
        catch (Exception) { _logger.LogWarning("Stop cleanup failed"); }
        finally
        {
            CloseDeliveries();
            if (interrupted || _stopInterrupt.IsCancellationRequested) _logger.LogWarning("被 Host 逾時中斷");
            _ = ObserveRegistryCleanupAsync(registryCleanup);
            lock (_registry.Gate)
            {
                _state = State.Stopped;
                _degraded = false;
                foreach (var registration in _stopRegistrations) registration.Unregister();
                _stopRegistrations.Clear();
                completion.TrySetResult();
            }
        }
    }
    private async Task WaitForGraceAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (_registry.Gate) tasks = _workers.Concat(_deliveries.Select(x => x.Settled.Task)).ToArray();
            await Task.WhenAll(tasks);
            lock (_registry.Gate) if (_workers.All(x => x.IsCompleted) && _deliveries.Count == 0) return;
        }
    }
    private async Task WaitForJoinAsync(Task registryCleanup)
    {
        while (true)
        {
            Task[] tasks;
            lock (_registry.Gate)
            {
                tasks = _workers.Concat(_deliveries.Select(x => x.Settled.Task))
                    .Concat(_activityCalls.Select(x => x.Settled.Task)).Concat(_cleanupTasks).Append(registryCleanup).ToArray();
            }
            await Task.WhenAll(tasks);
            lock (_registry.Gate)
                if (_workers.All(x => x.IsCompleted) && _deliveries.Count == 0 && _activityCalls.Count == 0 && _cleanupTasks.Count == 0)
                    return;
        }
    }
    private async Task ObserveRegistryCleanupAsync(Task cleanup)
    {
        try { await cleanup; }
        catch (Exception) { _logger.LogWarning("Registry cleanup failed"); }
    }
    private sealed class DeliveryEntry(Task call, CancellationTokenSource cancellation, CancellationTokenSource timeout)
    {
        internal readonly Task Call = call;
        internal readonly CancellationTokenSource Cancellation = cancellation;
        internal readonly CancellationTokenSource Timeout = timeout;
        internal readonly CancellationToken Token = cancellation.Token;
        internal readonly TaskCompletionSource Settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal volatile bool WaitAbandoned;
    }
    private sealed class ActivityCallEntry(Task<IAsyncDisposable?> call)
    {
        internal readonly Task<IAsyncDisposable?> Call = call;
        internal readonly TaskCompletionSource Settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class WorkLease(WebhookConnector owner, CancellationTokenSource cancellation, CancellationTokenSource budget, string eventId)
    {
        internal readonly WebhookConnector Owner = owner;
        internal readonly CancellationTokenSource Cancellation = cancellation;
        internal readonly CancellationTokenSource Budget = budget;
        internal readonly CancellationToken Token = cancellation.Token;
        internal readonly string EventId = eventId;
        internal bool Revoked;
    }
    private sealed class ActivityEntry(IAsyncDisposable resource, ExternalKey message)
    {
        internal readonly IAsyncDisposable Resource = resource;
        internal readonly ExternalKey Message = message;
        internal ITimer? Timer;
        internal Task? DisposeTask;
    }
    private enum State { Running, Stopping, Stopped }
    private sealed record Work(PlatformInboundEvent Item, DateTimeOffset EnqueuedAt);
}
