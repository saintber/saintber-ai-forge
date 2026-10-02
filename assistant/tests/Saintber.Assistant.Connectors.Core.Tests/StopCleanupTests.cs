using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Saintber.Assistant.Abstractions;

namespace Saintber.Assistant.Connectors.Core.Tests;

public class StopCleanupTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static OutboundEnvelope Outbound(InboundEnvelope envelope, bool reply = false) =>
        new("fake", "main", envelope.Chat, reply ? envelope.Message : null, new("hello"));

    [Fact]
    public async Task Stop_drops_three_pending_with_ids_keeps_registration_and_waits_for_existing_delivery()
    {
        var f = new Fixture(workers: 2); var entered = new ConcurrentBag<string>(); var release = Signal();
        var delivered = new ConcurrentBag<DeliveryAcceptance>();
        await f.Start(async (envelope, _) => { entered.Add(envelope.Message.CanonicalValue); await release.Task; delivered.Add(await f.Connector.DeliverAsync(Outbound(envelope, true), default)); });
        await f.Admit("busy1"); await PipelineTests.Until(() => entered.Count == 1);
        await f.Admit("busy2"); await PipelineTests.Until(() => entered.Count == 2);
        foreach (var id in new[] { "pending1", "pending2", "pending3" }) await f.Admit(id);
        var stop = f.Connector.StopAsync(default); var repeated = f.Connector.StopAsync(default);
        try
        {
            Assert.False(stop.IsCompleted); Assert.False(repeated.IsCompleted);
            Assert.Equal(0, f.Connector.PendingCount); Assert.Equal(5, f.Registry.Count);
            foreach (var id in new[] { "pending1", "pending2", "pending3" })
                Assert.Contains(f.Logger.Entries, x => x.Message.Contains("discarded") && x.Message.Contains(id));
            Assert.Equal(503, (await f.Admit("rejected")).StatusCode); Assert.Equal(5, f.Registry.Count);
            release.SetResult(); await Task.WhenAll(stop, repeated).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, f.Handler!.Calls); Assert.Equal(2, f.Platform.ReplyCalls);
            Assert.All(delivered, result => Assert.Equal(DeliveryAcceptance.Accepted, result));
        }
        finally { release.TrySetResult(); await f.Stop(); }
    }

    [Fact]
    public async Task Grace_cancellation_joins_two_hundred_millisecond_cooperative_cleanup()
    {
        var f = new Fixture(); var entered = Signal(); var cleaning = Signal(); var finished = Signal();
        await f.Start(async (_, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException)
            {
                var delay = Task.Delay(TimeSpan.FromMilliseconds(200), f.Clock);
                cleaning.SetResult(); await delay; finished.SetResult();
            }
        });
        await f.Admit("E1"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = f.Connector.StopAsync(default);
        f.Clock.Advance(10); await cleaning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stop.IsCompleted);
        f.Clock.Advance(.2); await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(finished.Task.IsCompleted);
        Assert.Equal(503, (await f.Admit("later")).StatusCode);
    }

    [Fact]
    public async Task Noncooperative_handler_is_abandoned_at_join_and_late_send_is_unavailable()
    {
        var f = new Fixture(); var entered = Signal(); var release = Signal(); var cancelled = Signal();
        var result = new TaskCompletionSource<DeliveryAcceptance>(TaskCreationOptions.RunContinuationsAsynchronously);
        await f.Start(async (envelope, token) =>
        {
            token.Register(() => cancelled.TrySetResult()); entered.SetResult(); await release.Task;
            result.SetResult(await f.Connector.DeliverAsync(Outbound(envelope, true), CancellationToken.None));
        });
        await f.Admit("E1"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = f.Connector.StopAsync(default);
        try
        {
            f.Clock.Advance(10); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Clock.WaitTimers(TimeSpan.FromSeconds(5), 1); f.Clock.Advance(5);
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(f.Logger.Entries, x => x.Message.Contains("abandoned"));
            var callbacks = f.Clock.Callbacks; f.Clock.Advance(1200);
            Assert.Equal(callbacks, f.Clock.Callbacks);
            release.SetResult();
            Assert.Equal(DeliveryAcceptance.Unavailable, await result.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, f.Platform.PushCalls + f.Platform.ReplyCalls);
        }
        finally { release.TrySetResult(); await f.Stop(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Independent_send_is_cancelled_and_joined_until_original_task_settles(bool lateFault)
    {
        var f = new Fixture(options: new WebhookConnectorOptions { MaxConcurrency = 1, SendTimeout = TimeSpan.FromSeconds(1000) });
        var call = Signal(); var entered = Signal(); var cancelled = Signal();
        f.Platform.Push = (_, _, token) => { token.Register(() => cancelled.TrySetResult()); entered.SetResult(); return call.Task; };
        await f.Start((_, _) => Task.CompletedTask);
        var delivery = f.Connector.DeliverAsync(Outbound(PipelineTests.Event(f.Clock.Fake, "M").Envelope), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = f.Connector.StopAsync(default);
        try
        {
            Assert.False(stop.IsCompleted);
            f.Clock.Advance(10); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(DeliveryAcceptance.Accepted, await delivery.WaitAsync(TimeSpan.FromSeconds(5)));
            await f.Clock.WaitTimers(TimeSpan.FromSeconds(5), 1);
            Assert.False(stop.IsCompleted);
            if (lateFault)
            {
                f.Clock.Advance(5); await stop.WaitAsync(TimeSpan.FromSeconds(5));
                var callbacks = f.Clock.Callbacks; f.Clock.Advance(2000);
                Assert.Equal(callbacks, f.Clock.Callbacks);
                call.SetException(new Exception("TOKEN-SECRET BODY-SECRET"));
                await PipelineTests.Until(() => f.Logger.Entries.Any(x => x.Message.Contains("Late delivery")));
                Assert.DoesNotContain(f.Logger.Entries, x => x.Message.Contains("SECRET"));
            }
            else
            {
                call.SetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, f.TrackedCount("_deliveries"));
            }
        }
        finally { call.TrySetResult(); await f.Stop(); }
    }

    [Fact]
    public async Task Host_cancellation_at_two_seconds_returns_without_joining_noncooperative_work_or_send()
    {
        var f = new Fixture(); var release = Signal(); var entered = Signal(); var workCancelled = Signal(); var sendCancelled = Signal(); var call = Signal();
        f.Platform.Push = (_, _, token) => { token.Register(() => sendCancelled.TrySetResult()); return call.Task; };
        await f.Start(async (_, token) => { token.Register(() => workCancelled.TrySetResult()); entered.SetResult(); await release.Task; });
        await f.Admit("E1"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var delivery = f.Connector.DeliverAsync(Outbound(PipelineTests.Event(f.Clock.Fake, "M").Envelope), default);
        using var host = new CancellationTokenSource(); var stop = f.Connector.StopAsync(host.Token);
        try
        {
            f.Clock.Advance(2); host.Cancel(); await stop.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(workCancelled.Task, sendCancelled.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(DeliveryAcceptance.Accepted, await delivery.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(503, (await f.Admit("later")).StatusCode);
            Assert.Contains(f.Logger.Entries, x => x.Message.Contains("被 Host 逾時中斷"));
            Assert.Equal(DeliveryAcceptance.Unavailable, await f.Connector.DeliverAsync(Outbound(PipelineTests.Event(f.Clock.Fake, "later").Envelope), default));
            Assert.Equal(1, f.Platform.PushCalls);
        }
        finally { call.TrySetResult(); release.TrySetResult(); await f.Stop(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Activity_cleanup_is_bounded_and_host_abort_does_not_wait_for_it(bool hostAbort)
    {
        var f = new Fixture(); var cleanupEntered = Signal(); var cleanupRelease = Signal(); var handled = Signal();
        var activity = new ControlledActivity(async () => { cleanupEntered.SetResult(); await cleanupRelease.Task; });
        f.Platform.Capabilities = new(true, true, true, TimeSpan.FromSeconds(50));
        f.Platform.Activity = _ => Task.FromResult<IAsyncDisposable?>(activity);
        await f.Start((_, _) => { handled.SetResult(); return Task.CompletedTask; });
        await f.Admit("E1"); await handled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var host = new CancellationTokenSource(); var stop = f.Connector.StopAsync(host.Token);
        try
        {
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stop.IsCompleted);
            if (hostAbort) host.Cancel();
            else { await f.Clock.WaitTimers(TimeSpan.FromSeconds(5), 1); f.Clock.Advance(5); }
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, activity.Calls);
            Assert.Equal(503, (await f.Admit("later")).StatusCode);
        }
        finally { cleanupRelease.TrySetResult(); await f.Stop(); }
    }

    [Fact]
    public async Task Cleanup_removed_by_reply_remains_joinable_and_dispose_task_is_shared()
    {
        var f = new Fixture(); var handled = Signal(); var entered = Signal(); var release = Signal();
        var activity = new ControlledActivity(async () => { entered.SetResult(); await release.Task; });
        f.Platform.Capabilities = new(true, true, true, TimeSpan.FromSeconds(50));
        f.Platform.Activity = _ => Task.FromResult<IAsyncDisposable?>(activity);
        await f.Start((_, _) => { handled.SetResult(); return Task.CompletedTask; });
        await f.Admit("E1"); await handled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var delivery = f.Connector.DeliverAsync(Outbound(PipelineTests.Event(f.Clock.Fake, "E1").Envelope, true), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = f.Connector.StopAsync(default);
        try
        {
            await f.Clock.WaitTimers(TimeSpan.FromSeconds(5), 1);
            Assert.False(stop.IsCompleted); Assert.Equal(1, activity.Calls);
            release.SetResult(); await Task.WhenAll(stop, delivery).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, activity.Calls);
        }
        finally { release.TrySetResult(); await f.Stop(); }
    }

    [Fact]
    public async Task Late_activity_after_stopped_is_disposed_once_without_handler_or_activity_registration()
    {
        var f = new Fixture(); var started = Signal(); var cancelled = Signal();
        var call = new TaskCompletionSource<IAsyncDisposable?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Platform.Capabilities = new(true, true, true, TimeSpan.FromSeconds(50));
        f.Platform.Activity = token => { token.Register(() => cancelled.TrySetResult()); started.SetResult(); return call.Task; };
        await f.Start((_, _) => Task.CompletedTask);
        await f.Admit("E1"); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = f.Connector.StopAsync(default);
        try
        {
            f.Clock.Advance(10); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Clock.WaitTimers(TimeSpan.FromSeconds(5), 1); f.Clock.Advance(5);
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            var activity = new CountedActivity(f.Clock);
            call.SetResult(activity); await PipelineTests.Until(() => activity.Disposals == 1);
            Assert.Equal(0, f.Handler!.Calls); Assert.Equal(0, f.TrackedCount("_activities"));
            f.Clock.Advance(180); Assert.Equal(0, activity.Ticks); Assert.Equal(1, activity.Disposals);
            Assert.Equal(1, f.Platform.ActivityCalls);
        }
        finally { call.TrySetResult(null); await f.Stop(); }
    }

    [Fact]
    public async Task Already_completed_activity_cannot_start_handler_after_host_abort()
    {
        var f = new Fixture(); var started = Signal();
        var call = new TaskCompletionSource<IAsyncDisposable?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activity = new CountedActivity();
        f.Platform.Capabilities = new(true, true, true, TimeSpan.FromSeconds(50));
        f.Platform.Activity = _ => { started.SetResult(); return call.Task; };
        await f.Start((_, _) => Task.CompletedTask); await f.Admit("E1");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task stop;
        lock (f.Gate)
        {
            call.SetResult(activity);
            stop = f.Connector.StopAsync(new CancellationToken(true));
        }
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await PipelineTests.Until(() => activity.Disposals == 1);
        Assert.Equal(0, f.Handler!.Calls); Assert.Equal(0, f.TrackedCount("_activities"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admission_and_stop_are_fully_ordered_by_same_gate(bool stopWins)
    {
        var f = new Fixture(); var entered = Signal(); var release = Signal();
        await f.Start(async (_, _) => { entered.SetResult(); await release.Task; });
        await f.Admit("busy"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<WebhookResult> admission; Task stop;
        var parsed = new ManualResetEventSlim(); var unblock = new ManualResetEventSlim();
        try
        {
            if (stopWins)
            {
                f.Inbound.AfterParse = () => { parsed.Set(); unblock.Wait(); };
                admission = Task.Run(() => f.Admit("race"));
                Assert.True(parsed.Wait(TimeSpan.FromSeconds(5)));
                stop = f.Connector.StopAsync(default); unblock.Set();
            }
            else
            {
                lock (f.Gate)
                {
                    admission = f.Admit("race");
                    stop = f.Connector.StopAsync(default);
                }
            }
            Assert.Equal(stopWins ? 503 : 200, (await admission).StatusCode);
            Assert.Equal(stopWins ? 1 : 2, f.Registry.Count);
            Assert.Equal(0, f.Connector.PendingCount);
            release.SetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, f.Handler!.Calls);
        }
        finally { unblock.Set(); release.TrySetResult(); await f.Stop(); parsed.Dispose(); unblock.Dispose(); }
    }

    [Fact]
    public async Task Two_activities_are_disposed_once_and_clean_stop_leaves_no_timer_callbacks()
    {
        var f = new Fixture(workers: 2); var resources = new ConcurrentBag<CountedActivity>(); var handled = new ConcurrentBag<string>();
        f.Platform.Capabilities = new(true, true, true, TimeSpan.FromSeconds(50));
        f.Platform.Activity = _ => { var activity = new CountedActivity(); resources.Add(activity); return Task.FromResult<IAsyncDisposable?>(activity); };
        await f.Start((envelope, _) => { handled.Add(envelope.Message.CanonicalValue); return Task.CompletedTask; });
        await f.Admit("E1"); await f.Admit("E2"); await PipelineTests.Until(() => handled.Count == 2);
        await f.Stop(); Assert.Equal(2, resources.Count); Assert.All(resources, activity => Assert.Equal(1, activity.Disposals));
        var callbacks = f.Clock.Callbacks;
        f.Clock.Advance(1200);
        Assert.Equal(callbacks, f.Clock.Callbacks); Assert.Equal(2, f.Platform.ActivityCalls);
        Assert.Equal(503, (await f.Admit("late")).StatusCode); Assert.Equal(2, f.Handler!.Calls);
    }

    [Fact]
    public async Task Throwing_cancellation_callback_is_observed_without_breaking_stop_or_logging_values()
    {
        var f = new Fixture(); var entered = Signal(); var release = Signal();
        await f.Start(async (_, token) => { token.Register(() => throw new Exception("TOKEN-SECRET")); entered.SetResult(); await release.Task; });
        await f.Admit("E1"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var host = new CancellationTokenSource(); var stop = f.Connector.StopAsync(host.Token);
        try
        {
            host.Cancel(); await stop.WaitAsync(TimeSpan.FromSeconds(5));
            await PipelineTests.Until(() => f.Logger.Entries.Any(x => x.Message.Contains("cancellation", StringComparison.OrdinalIgnoreCase)));
            Assert.DoesNotContain(f.Logger.Entries, x => x.Message.Contains("SECRET"));
        }
        finally { release.TrySetResult(); await f.Stop(); }
    }

    [Fact]
    public async Task Timer_disposal_failure_does_not_skip_activity_resource_disposal()
    {
        var f = new Fixture(wrapTimer: (timer, due) => due == TimeSpan.FromMinutes(2) ? new FaultingTimer(timer) : timer);
        var activity = new CountedActivity(); var handled = Signal();
        f.Platform.Capabilities = new(true, true, true, TimeSpan.FromSeconds(50));
        f.Platform.Activity = _ => Task.FromResult<IAsyncDisposable?>(activity);
        await f.Start((_, _) => { handled.SetResult(); return Task.CompletedTask; });
        await f.Admit("E1"); await handled.Task.WaitAsync(TimeSpan.FromSeconds(5)); await f.Stop();
        Assert.Equal(1, activity.Disposals);
        Assert.DoesNotContain(f.Logger.Entries, entry => entry.Message.Contains("SECRET"));
    }

    [Fact]
    public async Task Stop_before_start_cannot_start_or_restart_workers()
    {
        var f = new Fixture(); await f.Stop(); var timers = f.Clock.Timers;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Start((_, _) => Task.CompletedTask));
        Assert.Equal(timers, f.Clock.Timers); Assert.Equal(503, (await f.Admit("E1")).StatusCode);
    }

    [Fact]
    public async Task Host_cancellation_revokes_lease_before_the_cancellation_callback_returns()
    {
        var f = new Fixture(); var entered = Signal(); var cancel = Signal();
        using var host = new CancellationTokenSource();
        var outcome = new TaskCompletionSource<DeliveryAcceptance>(TaskCreationOptions.RunContinuationsAsynchronously);
        await f.Start(async (envelope, _) =>
        {
            entered.SetResult(); await cancel.Task;
            Task<DeliveryAcceptance> delivery;
            lock (f.Gate)
            {
                host.Cancel();
                delivery = f.Connector.DeliverAsync(Outbound(envelope, true), CancellationToken.None);
            }
            outcome.SetResult(await delivery);
        });
        await f.Admit("E1"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = f.Connector.StopAsync(host.Token); cancel.SetResult();
        Assert.Equal(DeliveryAcceptance.Unavailable, await outcome.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await stop.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, f.Platform.PushCalls + f.Platform.ReplyCalls);
    }

    [Fact]
    public async Task Host_abort_is_not_blocked_by_a_user_cancellation_callback()
    {
        var f = new Fixture(); var entered = Signal(); var releaseWork = Signal();
        using var releaseCallback = new ManualResetEventSlim();
        await f.Start(async (_, token) => { token.Register(() => releaseCallback.Wait()); entered.SetResult(); await releaseWork.Task; });
        await f.Admit("E1"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var host = new CancellationTokenSource(); var stop = f.Connector.StopAsync(host.Token);
        var cancelHost = Task.Run(host.Cancel);
        try
        {
            await stop.WaitAsync(TimeSpan.FromSeconds(5)); await cancelHost.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(503, (await f.Admit("later")).StatusCode);
        }
        finally
        {
            releaseCallback.Set(); releaseWork.TrySetResult();
            await cancelHost.WaitAsync(TimeSpan.FromSeconds(5)); await f.Stop();
            await PipelineTests.Until(() => f.TrackedCount("_cleanupTasks") == 0);
        }
    }

    [Fact]
    public async Task Background_timer_async_dispose_is_joined_within_the_same_five_second_budget()
    {
        var entered = Signal(); var release = Signal();
        var f = new Fixture(wrapTimer: (timer, due) => due == TimeSpan.FromMinutes(1) ? new DelayedTimer(timer, entered, release.Task) : timer);
        await f.Start((_, _) => Task.CompletedTask); var stop = f.Connector.StopAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Clock.WaitTimers(TimeSpan.FromSeconds(5), 1); Assert.False(stop.IsCompleted);
            f.Clock.Advance(5); await stop.WaitAsync(TimeSpan.FromSeconds(5));
            var callbacks = f.Clock.Callbacks; f.Clock.Advance(1200); Assert.Equal(callbacks, f.Clock.Callbacks);
        }
        finally { release.TrySetResult(); await f.Stop(); }
    }

    [Fact]
    public async Task Stopped_delivery_is_rejected_without_creating_any_new_timer()
    {
        var f = new Fixture(); await f.Start((_, _) => Task.CompletedTask); await f.Stop();
        var timers = f.Clock.Timers;
        Assert.Equal(DeliveryAcceptance.Unavailable,
            await f.Connector.DeliverAsync(Outbound(PipelineTests.Event(f.Clock.Fake, "M").Envelope), default));
        Assert.Equal(timers, f.Clock.Timers);
        Assert.Equal(0, f.Platform.PushCalls + f.Platform.ReplyCalls);
    }

    [Theory]
    [InlineData(10, 15)]
    [InlineData(20, 25)]
    public async Task Stop_budget_is_grace_plus_join(int grace, int budget)
    {
        var f = new Fixture(options: new WebhookConnectorOptions { StopGrace = TimeSpan.FromSeconds(grace), JoinTimeout = TimeSpan.FromSeconds(5) });
        Assert.Equal(TimeSpan.FromSeconds(budget), f.Connector.StopBudget);
        await f.Start((_, _) => Task.CompletedTask); await f.Stop();
    }

    private sealed class Fixture
    {
        public TrackingClock Clock { get; }
        public FakeInbound Inbound { get; } = new();
        public FakePlatform Platform { get; } = new() { Capabilities = new(true, true, false, TimeSpan.FromSeconds(50)) };
        public ConcurrentLogger Logger { get; } = new();
        public WebhookConnector Connector { get; }
        public FakeHandler? Handler { get; private set; }
        public EventRegistry Registry => (EventRegistry)Connector.GetType().GetField("_registry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Connector)!;
        public object Gate => typeof(EventRegistry).GetProperty("Gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Registry)!;
        public Fixture(int workers = 1, WebhookConnectorOptions? options = null, Func<ITimer, TimeSpan, ITimer>? wrapTimer = null)
        {
            Clock = new TrackingClock(wrapTimer);
            Connector = new("fake", "main", Inbound, Platform, Clock, Logger, options ?? new() {
                MaxConcurrency = workers, EventTimeout = TimeSpan.FromSeconds(60), ActivityTimeout = TimeSpan.FromSeconds(60), SendTimeout = TimeSpan.FromSeconds(60) });
        }
        public Task Start(Func<InboundEnvelope, CancellationToken, Task> handle) => Connector.StartAsync(Handler = new FakeHandler(handle), default);
        public Task<WebhookResult> Admit(string id)
        {
            Inbound.Events = [PipelineTests.Event(Clock.Fake, id)];
            return Connector.ReceiveAsync(new(new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty), default);
        }
        public Task Stop() => Connector.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        public int TrackedCount(string field)
        {
            var value = typeof(WebhookConnector).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Connector)!;
            return (int)value.GetType().GetProperty("Count")!.GetValue(value)!;
        }
    }
    private sealed class TrackingClock(Func<ITimer, TimeSpan, ITimer>? wrapTimer = null) : TimeProvider
    {
        public FakeTimeProvider Fake { get; } = new();
        private readonly ConcurrentBag<TimeSpan> _timers = [];
        private int _callbacks;
        public int Callbacks => Volatile.Read(ref _callbacks);
        public int Timers => _timers.Count;
        public override DateTimeOffset GetUtcNow() => Fake.GetUtcNow();
        public override long GetTimestamp() => Fake.GetTimestamp();
        public override long TimestampFrequency => Fake.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = Fake.CreateTimer(s => { Interlocked.Increment(ref _callbacks); callback(s); }, state, dueTime, period);
            _timers.Add(dueTime); return wrapTimer?.Invoke(timer, dueTime) ?? timer;
        }
        public void Advance(double seconds) => Fake.Advance(TimeSpan.FromSeconds(seconds));
        public Task WaitTimers(TimeSpan due, int count) => PipelineTests.Until(() => _timers.Count(x => x == due) >= count);
    }
    private sealed class ConcurrentLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }
    private sealed class DelayedTimer(ITimer inner, TaskCompletionSource entered, Task release) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);
        public void Dispose() => inner.Dispose();
        public async ValueTask DisposeAsync() { await inner.DisposeAsync(); entered.TrySetResult(); await release; }
    }
    private sealed class FaultingTimer(ITimer inner) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);
        public void Dispose() => inner.Dispose();
        public ValueTask DisposeAsync()
        {
            inner.Dispose();
            return ValueTask.FromException(new Exception("TOKEN-SECRET"));
        }
    }
    private sealed class ControlledActivity(Func<Task> dispose) : IAsyncDisposable
    {
        private int _calls; public int Calls => Volatile.Read(ref _calls);
        public ValueTask DisposeAsync() { Interlocked.Increment(ref _calls); return new ValueTask(dispose()); }
    }
}
