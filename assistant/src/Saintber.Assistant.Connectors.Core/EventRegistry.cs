using Microsoft.Extensions.Logging;
using Saintber.Assistant.Abstractions;
namespace Saintber.Assistant.Connectors.Core;

/// <summary>入列與登記共用同一閘門。保護僅限登記保留期間；淘汰會提前結束保護。</summary>
public sealed class EventRegistry : IDisposable, IAsyncDisposable
{
    internal object Gate { get; } = new();
    private readonly TimeProvider _clock;
    private readonly TimeSpan _ttl;
    private readonly int _max;
    private readonly ILogger _logger;
    private readonly ITimer _sweep;
    private readonly Dictionary<string, LinkedListNode<Registration>> _events = new(StringComparer.Ordinal);
    private readonly LinkedList<Registration> _order = new();
    private readonly Dictionary<ExternalKey, ReplyContext> _replies = new();
    private bool _disposed;
    public int Count { get { lock (Gate) return _events.Count; } }
    public int ReplyContextCount { get { lock (Gate) return _replies.Count; } }
    public EventRegistry(TimeProvider clock, TimeSpan ttl, int maxEntries, ILogger logger)
    {
        if (ttl <= TimeSpan.Zero) throw new ArgumentException("Dedup:Ttl");
        if (maxEntries < 1) throw new ArgumentException("Dedup:MaxEntries");
        _clock = clock; _ttl = ttl; _max = maxEntries; _logger = logger;
        _sweep = clock.CreateTimer(_ => { lock (Gate) { if (!_disposed) Sweep(); } }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }
    public bool TryAdmit(string eventId, ExternalKey message, ExternalKey chat, string? replyToken, DateTimeOffset eventTime, Func<bool> tryEnqueue)
    {
        lock (Gate)
        {
            if (_disposed) return false;
            Sweep();
            if (_events.ContainsKey(eventId) || !tryEnqueue()) return false;
            if (_events.Count == _max) { RemoveOldest(); _logger.LogWarning("Registration evicted at capacity"); }
            var now = _clock.GetUtcNow();
            if (!_replies.TryGetValue(message, out var reply))
            {
                reply = new ReplyContext(replyToken, chat, eventTime < now ? eventTime : now);
                _replies.Add(message, reply);
            }
            reply.References++;
            _events.Add(eventId, _order.AddLast(new Registration(eventId, message, now, reply)));
            return true;
        }
    }
    internal ReplyContext? FindReply(ExternalKey message)
    {
        lock (Gate) { Sweep(); return _replies.GetValueOrDefault(message); }
    }
    private void Sweep()
    {
        var now = _clock.GetUtcNow();
        while (_order.First is { } first && now - first.Value.AcceptedAt >= _ttl) RemoveOldest();
    }
    private void RemoveOldest()
    {
        var registration = _order.First!.Value;
        _order.RemoveFirst(); _events.Remove(registration.EventId);
        if (--registration.Reply.References == 0) _replies.Remove(registration.Message);
    }
    public void Dispose()
    {
        lock (Gate) _disposed = true;
        _sweep.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        lock (Gate) _disposed = true;
        await _sweep.DisposeAsync();
    }
    private sealed record Registration(string EventId, ExternalKey Message, DateTimeOffset AcceptedAt, ReplyContext Reply);
    internal sealed class ReplyContext(string? token, ExternalKey chat, DateTimeOffset validFrom)
    {
        internal readonly string? Token = token;
        internal readonly ExternalKey Chat = chat;
        internal readonly DateTimeOffset ValidFrom = validFrom;
        internal bool Used = false;
        internal int References;
    }
}
