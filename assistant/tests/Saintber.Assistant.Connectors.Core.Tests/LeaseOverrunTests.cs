using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Saintber.Assistant.Abstractions;

namespace Saintber.Assistant.Connectors.Core.Tests;

public class LeaseOverrunTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly WebhookRequest Request = new(new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty);
    private static OutboundEnvelope Outbound(InboundEnvelope envelope, bool reply = false) =>
        new(envelope.ConnectorType, envelope.ConnectorInstanceId, envelope.Chat, reply ? envelope.Message : null, new("hello"));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Child_delivery_after_return_or_budget_expiry_has_revoked_lease(bool expireBudget, bool reply)
    {
        var fixture = new Fixture();
        var ready = Signal(); var releaseHandler = Signal(); var send = Signal(); var cancelled = Signal();
        Task<DeliveryAcceptance>? child = null;
        await fixture.Start(async (envelope, token) =>
        {
            if (!envelope.Message.Equals(PipelineTests.Key("E1"))) return;
            token.Register(() => cancelled.TrySetResult());
            child = Task.Run(async () => { await send.Task; return await fixture.Connector.DeliverAsync(Outbound(envelope, reply), CancellationToken.None); });
            ready.SetResult();
            if (expireBudget) await releaseHandler.Task;
        });
        await fixture.Admit("E1"); await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (expireBudget)
        {
            fixture.Clock.Advance(30);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        else
        {
            // The next item starts only after the first handler has fully returned and its lease was revoked.
            await fixture.Admit("E2");
            await PipelineTests.Until(() => fixture.Handler!.Calls == 2);
        }
        send.SetResult();
        try
        {
            Assert.Equal(DeliveryAcceptance.Unavailable, await child!.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, fixture.Platform.PushCalls + fixture.Platform.ReplyCalls);
        }
        finally { releaseHandler.TrySetResult(); await fixture.Stop(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Valid_lease_TaskRun_delivery_is_accepted_in_running_and_stopping(bool stopping)
    {
        var fixture = new Fixture();
        var entered = Signal(); var send = Signal();
        var outcome = new TaskCompletionSource<DeliveryAcceptance>(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.Start(async (envelope, _) =>
        {
            entered.SetResult(); await send.Task;
            outcome.SetResult(await Task.Run(() => fixture.Connector.DeliverAsync(Outbound(envelope, true), CancellationToken.None)));
        });
        await fixture.Admit("E1"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = stopping ? fixture.Connector.StopAsync(default) : null;
        if (stopping)
        {
            Assert.Equal(503, (await fixture.Admit("rejected")).StatusCode);
            Assert.Equal(DeliveryAcceptance.Unavailable,
                await fixture.Connector.DeliverAsync(Outbound(PipelineTests.Event(fixture.Clock.Fake, "M").Envelope), default));
        }
        send.SetResult();
        Assert.Equal(DeliveryAcceptance.Accepted, await outcome.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, fixture.Platform.ReplyCalls);
        Assert.Equal(0, fixture.Platform.PushCalls);
        if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(5)); else await fixture.Stop();
        Assert.Equal(DeliveryAcceptance.Unavailable,
            await fixture.Connector.DeliverAsync(Outbound(PipelineTests.Event(fixture.Clock.Fake, "M").Envelope), default));
        Assert.Equal(1, fixture.Platform.ReplyCalls);
        Assert.Equal(0, fixture.Platform.PushCalls);
    }

    [Fact]
    public async Task No_lease_push_and_platform_failure_keep_accepted_result()
    {
        var fixture = new Fixture();
        fixture.Platform.Push = (_, _, _) => Task.FromException(new Exception("TOKEN-SECRET TEXT-SECRET"));
        await fixture.Start((_, _) => Task.CompletedTask);
        Assert.Equal(DeliveryAcceptance.Accepted,
            await fixture.Connector.DeliverAsync(Outbound(PipelineTests.Event(fixture.Clock.Fake, "M").Envelope), default));
        Assert.Equal(1, fixture.Platform.PushCalls);
        Assert.Contains(fixture.Logger.Entries, x => x.Level == LogLevel.Error && x.Message.Contains("Delivery failed"));
        Assert.DoesNotContain(fixture.Logger.Entries, x => x.Message.Contains("SECRET"));
        await fixture.Stop();
    }

    [Fact]
    public async Task Synchronous_platform_failure_keeps_accepted_and_ends_reply_activity()
    {
        var fixture = new Fixture(); var activity = new CountedActivity(); var entered = Signal();
        fixture.Platform.Capabilities = new(true, true, true, TimeSpan.FromSeconds(50));
        fixture.Platform.Activity = _ => Task.FromResult<IAsyncDisposable?>(activity);
        fixture.Platform.Reply = (_, _, _) => throw new Exception("TOKEN-SECRET");
        await fixture.Start((_, _) => { entered.SetResult(); return Task.CompletedTask; });
        await fixture.Admit("E1"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(DeliveryAcceptance.Accepted,
                await fixture.Connector.DeliverAsync(Outbound(PipelineTests.Event(fixture.Clock.Fake, "E1").Envelope, true), default));
            Assert.Equal(1, activity.Disposals);
            Assert.Equal(1, fixture.Platform.ReplyCalls);
        }
        finally { await fixture.Stop(); }
    }

    [Fact]
    public async Task Overrun_handler_cannot_deliver_before_stopping()
    {
        var fixture = new Fixture(); var entered = Signal(); var release = Signal();
        var outcome = new TaskCompletionSource<DeliveryAcceptance>(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.Start(async (envelope, _) =>
        {
            entered.SetResult(); await release.Task;
            outcome.SetResult(await Task.Run(() => fixture.Connector.DeliverAsync(Outbound(envelope), CancellationToken.None)));
        });
        await fixture.Admit("E1"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            fixture.Clock.Advance(30); await fixture.Clock.WaitOverrunTimers(1); fixture.Clock.Advance(5);
            await PipelineTests.Until(() => fixture.Logger.Count("overrun", LogLevel.Error) == 1);
            release.SetResult();
            Assert.Equal(DeliveryAcceptance.Unavailable, await outcome.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, fixture.Platform.PushCalls);
        }
        finally { release.TrySetResult(); await fixture.Stop(); }
    }

    [Fact]
    public async Task One_overrun_worker_does_not_block_other_three_and_logs_error_once()
    {
        var fixture = new Fixture(workers: 4);
        var entered = Signal(); var release = Signal(); var seen = new ConcurrentBag<string>();
        await fixture.Start(async (envelope, _) =>
        {
            seen.Add(envelope.Message.CanonicalValue);
            if (envelope.Message.Equals(PipelineTests.Key("busy"))) { entered.SetResult(); await release.Task; }
        });
        await fixture.Admit("busy"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            fixture.Clock.Advance(30); await fixture.Clock.WaitOverrunTimers(1); fixture.Clock.Advance(5);
            await PipelineTests.Until(() => fixture.Logger.Count("overrun", LogLevel.Error) == 1);
            fixture.Clock.Advance(5);
            foreach (var id in new[] { "other-1", "other-2", "other-3" }) await fixture.Admit(id);
            await PipelineTests.Until(() => seen.Count == 4);
            Assert.Equal(1, fixture.Logger.Count("overrun", LogLevel.Error));
            Assert.Equal(0, fixture.Logger.Count("Degraded entered"));
        }
        finally { release.TrySetResult(); await fixture.Stop(); }
    }

    [Fact]
    public async Task All_four_overrun_drop_without_registration_then_recover_and_replay()
    {
        var fixture = new Fixture(workers: 4);
        var releases = Enumerable.Range(0, 4).Select(_ => Signal()).ToArray();
        var seen = new ConcurrentBag<string>(); var active = 0; var maximum = 0;
        await fixture.Start(async (envelope, _) =>
        {
            seen.Add(envelope.Message.CanonicalValue);
            var count = Interlocked.Increment(ref active);
            int prior; do { prior = maximum; } while (count > prior && Interlocked.CompareExchange(ref maximum, count, prior) != prior);
            try { if (envelope.Message.CanonicalValue.StartsWith("fake:message:busy")) await releases[int.Parse(envelope.Message.CanonicalValue[^1..])].Task; }
            finally { Interlocked.Decrement(ref active); }
        });
        for (var i = 0; i < 4; i++) { await fixture.Admit("busy" + i); await PipelineTests.Until(() => Volatile.Read(ref active) == i + 1); }
        try
        {
            fixture.Clock.Advance(30); await fixture.Clock.WaitOverrunTimers(4); fixture.Clock.Advance(5);
            await PipelineTests.Until(() => fixture.Logger.Count("Degraded entered") == 1);
            Assert.Equal(200, (await fixture.Admit("retry")).StatusCode);
            Assert.Equal(4, fixture.Registrations);
            Assert.Equal(4, seen.Count);
            releases[0].SetResult();
            await PipelineTests.Until(() => fixture.Logger.Count("Degraded exited") == 1);
            await fixture.Admit("retry"); await PipelineTests.Until(() => seen.Contains("fake:message:retry"));
            Assert.Equal(5, fixture.Registrations); Assert.Equal(4, maximum);
            Assert.Equal(4, fixture.Logger.Count("overrun", LogLevel.Error));
        }
        finally { foreach (var release in releases) release.TrySetResult(); await fixture.Stop(); }
    }

    [Fact]
    public async Task Recovery_discards_forty_second_queue_item_and_processes_ten_second_item()
    {
        var fixture = new Fixture(workers: 4);
        var releases = Enumerable.Range(0, 4).Select(_ => Signal()).ToArray();
        var seen = new ConcurrentBag<string>();
        await fixture.Start(async (envelope, _) =>
        {
            seen.Add(envelope.Message.CanonicalValue);
            if (envelope.Message.CanonicalValue.StartsWith("fake:message:busy")) await releases[int.Parse(envelope.Message.CanonicalValue[^1..])].Task;
        });
        for (var i = 0; i < 4; i++) { await fixture.Admit("busy" + i); await PipelineTests.Until(() => seen.Count == i + 1); }
        await fixture.Admit("old");
        try
        {
            fixture.Clock.Advance(30); await fixture.Clock.WaitOverrunTimers(4);
            await fixture.Admit("recent"); fixture.Clock.Advance(5);
            await PipelineTests.Until(() => fixture.Logger.Count("Degraded entered") == 1);
            fixture.Clock.Advance(5); releases[0].SetResult();
            await PipelineTests.Until(() => seen.Contains("fake:message:recent"));
            Assert.DoesNotContain("fake:message:old", seen);
            Assert.Contains(fixture.Logger.Entries, x => x.Message.Contains("queue age"));
            Assert.Equal(6, fixture.Registrations);
        }
        finally { foreach (var release in releases) release.TrySetResult(); await fixture.Stop(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Degraded_return_during_stopping_or_after_stopped_does_not_recover(bool afterStopped)
    {
        var fixture = new Fixture(workers: 1);
        var entered = Signal(); var release = Signal(); var late = new TaskCompletionSource<DeliveryAcceptance>(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.Start(async (envelope, _) =>
        {
            entered.SetResult(); await release.Task;
            late.SetResult(await fixture.Connector.DeliverAsync(Outbound(envelope), CancellationToken.None));
        });
        await fixture.Admit("busy"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Admit("pending");
        fixture.Clock.Advance(30); await fixture.Clock.WaitOverrunTimers(1); fixture.Clock.Advance(5);
        await PipelineTests.Until(() => fixture.Logger.Count("Degraded entered") == 1);
        var stop = fixture.Connector.StopAsync(default);
        try
        {
            if (afterStopped)
            {
                fixture.Clock.Advance(10); await fixture.Clock.WaitTimer(TimeSpan.FromSeconds(5), 2); fixture.Clock.Advance(5);
                await stop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            release.SetResult();
            Assert.Equal(DeliveryAcceptance.Unavailable, await late.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(503, (await fixture.Admit("later")).StatusCode);
            Assert.Equal(1, fixture.Handler!.Calls);
            Assert.Equal(0, fixture.Platform.PushCalls);
            Assert.Equal(0, fixture.Logger.Count("Degraded exited"));
        }
        finally { release.TrySetResult(); await fixture.Stop(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Another_instance_lease_is_only_a_no_lease_delivery(bool separateContexts, bool stopping)
    {
        using var contextA = separateContexts ? new CoreLoadContext() : null;
        using var contextB = separateContexts ? new CoreLoadContext() : null;
        var a = new Fixture(context: contextA, instanceId: "A");
        var b = new Fixture(context: contextB, instanceId: "B");
        var bEntered = Signal(); var bRelease = Signal();
        await b.Start(async (_, _) => { bEntered.SetResult(); await bRelease.Task; });
        await b.Admit("B-busy"); await bEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopB = stopping ? b.Connector.StopAsync(default) : null;
        var done = new TaskCompletionSource<DeliveryAcceptance>(TaskCreationOptions.RunContinuationsAsynchronously);
        await a.Start(async (envelope, _) => done.SetResult(await Task.Run(() =>
            b.Connector.DeliverAsync(Outbound(envelope) with { ConnectorInstanceId = "B" }, CancellationToken.None))));
        try
        {
            await a.Admit("A-work");
            Assert.Equal(stopping ? DeliveryAcceptance.Unavailable : DeliveryAcceptance.Accepted,
                await done.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(stopping ? 0 : 1, b.Platform.PushCalls);
        }
        finally { bRelease.TrySetResult(); await a.Stop(); if (stopB is not null) await stopB; else await b.Stop(); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Options_constructor_rejects_no_worker_capacity(int workers)
    {
        var exception = Assert.Throws<ArgumentException>(() => new Fixture(workers));
        Assert.Contains("Work:MaxConcurrency", exception.Message);
    }

    [Fact]
    public void Lease_is_not_exposed_to_host_visible_contracts()
    {
        var types = typeof(InboundEnvelope).Assembly.GetExportedTypes().Concat(typeof(WebhookConnector).Assembly.GetExportedTypes());
        Assert.DoesNotContain(types, type => type.Name.Contains("Lease", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(types.SelectMany(type => type.GetProperties()), property =>
            property.Name.Contains("Lease", StringComparison.OrdinalIgnoreCase)
            || property.PropertyType.Name.Contains("Lease", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Fixture
    {
        public ArmedClock Clock { get; } = new();
        public FakeInbound Inbound { get; } = new();
        public FakePlatform Platform { get; } = new() { Capabilities = new(true, true, false, TimeSpan.FromSeconds(50)) };
        public StateLogger Logger { get; } = new();
        public IConnector Connector { get; }
        public FakeHandler? Handler { get; private set; }
        public int Registrations => ((dynamic)Connector.GetType().GetField("_registry", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Connector)!).Count;
        public Fixture(int workers = 1, CoreLoadContext? context = null, string instanceId = "main")
        {
            var options = new WebhookConnectorOptions { MaxConcurrency = workers };
            Connector = context is null ? new WebhookConnector("fake", instanceId, Inbound, Platform, Clock, Logger, options)
                : context.Create(instanceId, Inbound, Platform, Clock, Logger, options);
        }
        public Task Start(Func<InboundEnvelope, CancellationToken, Task> handle) =>
            Connector.StartAsync(Handler = new FakeHandler(handle), default);
        public Task<WebhookResult> Admit(string id)
        {
            Inbound.Events = [PipelineTests.Event(Clock.Fake, id)];
            return ((IWebhookReceiver)Connector).ReceiveAsync(Request, default);
        }
        public Task Stop() => Connector.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class ArmedClock : TimeProvider
    {
        public FakeTimeProvider Fake { get; } = new();
        private readonly ConcurrentBag<TimeSpan> _timers = [];
        public override DateTimeOffset GetUtcNow() => Fake.GetUtcNow();
        public override long GetTimestamp() => Fake.GetTimestamp();
        public override long TimestampFrequency => Fake.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = Fake.CreateTimer(callback, state, dueTime, period);
            _timers.Add(dueTime);
            return timer;
        }
        public void Advance(int seconds) => Fake.Advance(TimeSpan.FromSeconds(seconds));
        public Task WaitOverrunTimers(int count) => WaitTimer(TimeSpan.FromSeconds(5), count);
        public Task WaitTimer(TimeSpan due, int count) => PipelineTests.Until(() => _timers.Count(x => x == due) >= count);
    }

    private sealed class StateLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        public int Count(string text, LogLevel? level = null) => Entries.Count(x => x.Message.Contains(text, StringComparison.Ordinal) && (level is null || x.Level == level));
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }

    private sealed class CoreLoadContext : AssemblyLoadContext, IDisposable
    {
        private readonly Assembly _core;
        private readonly Type _bridgeType;
        public CoreLoadContext() : base(isCollectible: false)
        {
            _core = LoadFromAssemblyPath(typeof(WebhookConnector).Assembly.Location);
            _bridgeType = LoadFromAssemblyPath(typeof(BridgeProxy).Assembly.Location).GetType(typeof(BridgeProxy).FullName!)!;
        }
        protected override Assembly? Load(AssemblyName name) =>
            name.Name == typeof(IConnector).Assembly.GetName().Name ? typeof(IConnector).Assembly
            : name.Name == typeof(ILogger).Assembly.GetName().Name ? typeof(ILogger).Assembly
            : name.Name == typeof(WebhookConnector).Assembly.GetName().Name ? _core : null;
        public IConnector Create(string instanceId, FakeInbound inbound, FakePlatform platform, TimeProvider clock, ILogger logger, WebhookConnectorOptions options)
        {
            var inboundType = _core.GetType(typeof(IWebhookInbound).FullName!)!;
            Func<MethodInfo, object?[]?, object?> inboundCall = (method, args) =>
            {
                if (method.Name == "Verify") { inbound.Verify((WebhookRequest)args![0]!); return null; }
                var events = inbound.Parse((WebhookRequest)args![0]!);
                var eventType = _core.GetType(typeof(PlatformInboundEvent).FullName!)!;
                var array = Array.CreateInstance(eventType, events.Count);
                for (var i = 0; i < events.Count; i++) array.SetValue(Activator.CreateInstance(eventType, events[i].EventId, events[i].EventTime, events[i].ReplyToken, events[i].Envelope), i);
                return array;
            };
            var inboundProxy = CreateBridge(inboundType, inboundCall);
            var platformType = _core.GetType(typeof(IMessagingPlatform).FullName!)!;
            Func<MethodInfo, object?[]?, object?> platformCall = (method, args) => method.Name switch
            {
                "get_Capabilities" => Activator.CreateInstance(_core.GetType(typeof(PlatformCapabilities).FullName!)!,
                    platform.Capabilities.Reply, platform.Capabilities.Push, platform.Capabilities.Activity, platform.Capabilities.ReplyValidity),
                "ReplyAsync" => platform.ReplyAsync((string)args![0]!, (MessageContent)args[1]!, (CancellationToken)args[2]!),
                "PushAsync" => platform.PushAsync((ExternalKey)args![0]!, (MessageContent)args[1]!, (CancellationToken)args[2]!),
                "StartActivityAsync" => platform.StartActivityAsync((ExternalKey)args![0]!, (CancellationToken)args[1]!),
                _ => throw new InvalidOperationException(method.Name)
            };
            var platformProxy = CreateBridge(platformType, platformCall);
            var optionsType = _core.GetType(typeof(WebhookConnectorOptions).FullName!)!;
            var ownOptions = Activator.CreateInstance(optionsType)!;
            foreach (var property in typeof(WebhookConnectorOptions).GetProperties())
                optionsType.GetProperty(property.Name)!.SetValue(ownOptions, property.GetValue(options));
            return (IConnector)Activator.CreateInstance(_core.GetType(typeof(WebhookConnector).FullName!)!,
                "fake", instanceId, inboundProxy, platformProxy, clock, logger, ownOptions)!;
        }
        private object CreateBridge(Type interfaceType, Func<MethodInfo, object?[]?, object?> call)
        {
            var proxy = DispatchProxy.Create(interfaceType, _bridgeType);
            _bridgeType.GetProperty("Call")!.SetValue(proxy, call);
            return proxy;
        }
        public void Dispose() { }
    }

    public class BridgeProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args);
    }
}
