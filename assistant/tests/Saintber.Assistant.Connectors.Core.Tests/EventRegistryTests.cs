using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Saintber.Assistant.Abstractions;
namespace Saintber.Assistant.Connectors.Core.Tests;
public class EventRegistryTests
{
    private static readonly ExternalKey Key = new("message", "fake:message:M1", JsonSerializer.SerializeToElement(new { messageId = "M1" }));
    private static readonly ExternalKey Chat = new("user", "fake:user:U1", JsonSerializer.SerializeToElement(new { userId = "U1" }));
    private static dynamic Registry(FakeTimeProvider clock, CaptureLogger logger, int max = 10000, int ttlSeconds = 600)
    {
        var type = Assembly.Load("Saintber.Assistant.Connectors.Core").GetType("Saintber.Assistant.Connectors.Core.EventRegistry", true)!;
        return Activator.CreateInstance(type, clock, TimeSpan.FromSeconds(ttlSeconds), max, logger)!;
    }
    private static bool Admit(dynamic registry, string id, DateTimeOffset time, Func<bool>? enqueue = null)
        => registry.TryAdmit(id, Key, Chat, "private-token", time, enqueue ?? (() => true));
    [Theory][InlineData(599, false)][InlineData(601, true)]
    public void Ttl_example_boundaries(int elapsedSeconds, bool acceptAgain)
    {
        var clock = new FakeTimeProvider(); using IDisposable registry = Registry(clock, new CaptureLogger());
        Assert.True(Admit((object)registry, "E1", clock.GetUtcNow())); clock.Advance(TimeSpan.FromSeconds(elapsedSeconds));
        Assert.Equal(acceptAgain, Admit((object)registry, "E1", clock.GetUtcNow()));
    }
    [Fact] public async Task Concurrent_duplicates_only_enqueue_once_and_other_ids_are_independent()
    {
        var clock = new FakeTimeProvider(); dynamic registry = Registry(clock, new CaptureLogger()); using IDisposable cleanup = registry;
        var calls = 0;
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => Admit((object)registry, "E1", clock.GetUtcNow(), () => { Interlocked.Increment(ref calls); return true; }))));
        Assert.Equal(1, calls); Assert.True(Admit((object)registry, "E2", clock.GetUtcNow()));
    }
    [Fact] public void Oldest_eviction_and_failed_enqueue_do_not_evict_or_register()
    {
        var clock = new FakeTimeProvider(); var logger = new CaptureLogger(); dynamic registry = Registry(clock, logger, 2); using IDisposable cleanup = registry;
        Assert.True(Admit((object)registry, "E1", clock.GetUtcNow())); Assert.True(Admit((object)registry, "E2", clock.GetUtcNow()));
        Assert.False(Admit((object)registry, "E3", clock.GetUtcNow(), () => false));
        Assert.False(Admit((object)registry, "E1", clock.GetUtcNow())); Assert.Equal(2, (int)registry.Count);
        Assert.True(Admit((object)registry, "E3", clock.GetUtcNow())); clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(Admit((object)registry, "E1", clock.GetUtcNow())); Assert.Contains(logger.Messages, x => x.Contains("evict"));
    }
    [Fact] public void Twenty_thousand_unique_events_stay_bounded_and_background_sweep_clears()
    {
        var clock = new FakeTimeProvider(); dynamic registry = Registry(clock, new CaptureLogger()); using IDisposable cleanup = registry;
        for (var i = 0; i < 20000; i++) { Assert.True(Admit((object)registry, "E" + i, clock.GetUtcNow())); Assert.InRange((int)registry.Count, 1, 10000); }
        Assert.Equal(1, (int)registry.ReplyContextCount); clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(0, (int)registry.Count); Assert.Equal(0, (int)registry.ReplyContextCount);
    }
    [Fact] public void Eviction_does_not_wait_for_first_work_to_finish()
    {
        var clock = new FakeTimeProvider(); dynamic registry = Registry(clock, new CaptureLogger(), 2); using IDisposable cleanup = registry;
        var running = new TaskCompletionSource();
        Assert.True(Admit((object)registry, "E1", clock.GetUtcNow(), () => { _ = running.Task; return true; }));
        Admit((object)registry, "E2", clock.GetUtcNow()); Admit((object)registry, "E3", clock.GetUtcNow());
        Assert.False(running.Task.IsCompleted); Assert.True(Admit((object)registry, "E1", clock.GetUtcNow())); running.SetResult();
    }
    [Theory][InlineData(0)][InlineData(-1)]
    public void Invalid_ttl_names_setting(int ttl)
    {
        var exception = Assert.Throws<TargetInvocationException>(() => Registry(new FakeTimeProvider(), new CaptureLogger(), ttlSeconds: ttl));
        Assert.Equal("Dedup:Ttl", exception.InnerException!.Message);
    }
}
public sealed class CaptureLogger : ILogger
{
    private readonly List<string> _messages = [];
    public IReadOnlyList<string> Messages { get { lock (_messages) return _messages.ToArray(); } }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    { lock (_messages) _messages.Add(formatter(state, exception)); }
}
