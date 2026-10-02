using System.Collections.Concurrent;
using System.Net;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Saintber.Assistant.Abstractions;
using Saintber.Assistant.Host;

namespace Saintber.Assistant.Host.Tests;

public class HostLinePublishTests
{
    private const string DllName = "Saintber.Assistant.Connectors.Line.dll";
    private static string Published
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "assistant", "Assistant.sln")))
                directory = directory.Parent;
            return Path.Combine(directory!.FullName, "assistant", "tests", "Saintber.Assistant.Host.Tests", "Fixtures", "line_published");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clean_published_line_instances_keep_dedup_reply_and_stop_state_independent(bool differentPaths)
    {
        using var api = new RecordingLineApi();
        var connectors = Load(api, differentPaths);
        AssertLoadContexts(connectors, differentPaths);
        var processed = new ConcurrentDictionary<(string Instance, string Text), TaskCompletionSource>();
        TaskCompletionSource Completion(string instance, string text) => processed.GetOrAdd((instance, text), _ => NewSignal());
        var invocations = new ConcurrentQueue<(string Instance, string Text)>();
        var byId = connectors.ToDictionary(connector => connector.InstanceId);
        var handler = new Handler(async (envelope, cancellationToken) =>
        {
            // Host receives the actual Abstractions type from the published connector.
            Assert.IsType<InboundEnvelope>(envelope);
            invocations.Enqueue((envelope.ConnectorInstanceId, envelope.Content.Text));
            var result = await byId[envelope.ConnectorInstanceId].DeliverAsync(new(
                "line", envelope.ConnectorInstanceId, envelope.Chat, envelope.Message,
                new MessageContent("reply-" + envelope.ConnectorInstanceId)), cancellationToken);
            Assert.Equal(DeliveryAcceptance.Accepted, result);
            Completion(envelope.ConnectorInstanceId, envelope.Content.Text).TrySetResult();
        });
        var running = new List<IConnector>();
        try
        {
            foreach (var connector in connectors)
            {
                await connector.StartAsync(handler, default);
                running.Add(connector);
            }
            var receivers = connectors.Select(connector => Assert.IsAssignableFrom<IWebhookReceiver>(connector)).ToArray();
            // Identical EventId and message key, but instance-local reply tokens.
            var a = Request("shared-event", "shared-message", "shared", "reply-token-a");
            var b = Request("shared-event", "shared-message", "shared", "reply-token-b");
            Assert.Equal(200, (await receivers[0].ReceiveAsync(a, default)).StatusCode);
            Assert.Equal(200, (await receivers[1].ReceiveAsync(b, default)).StatusCode);
            await Task.WhenAll(Completion("line_a", "shared").Task, Completion("line_b", "shared").Task).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(200, (await receivers[0].ReceiveAsync(a, default)).StatusCode);
            Assert.Equal(200, (await receivers[1].ReceiveAsync(b, default)).StatusCode);
            // One worker and a FIFO fence ensure duplicate work cannot hide behind our assertion.
            Assert.Equal(200, (await receivers[0].ReceiveAsync(Request("fence-a", "fence-message-a", "fence", "fence-token-a"), default)).StatusCode);
            Assert.Equal(200, (await receivers[1].ReceiveAsync(Request("fence-b", "fence-message-b", "fence", "fence-token-b"), default)).StatusCode);
            await Task.WhenAll(Completion("line_a", "fence").Task, Completion("line_b", "fence").Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(invocations, item => item == ("line_a", "shared"));
            Assert.Single(invocations, item => item == ("line_b", "shared"));
            var sharedReplies = api.Calls.Where(call => call.Path == "/v2/bot/message/reply" && call.Token is "reply-token-a" or "reply-token-b").ToArray();
            Assert.Equal(2, sharedReplies.Length);
            Assert.Single(sharedReplies, call => call.Token == "reply-token-a" && call.Text == "reply-line_a");
            Assert.Single(sharedReplies, call => call.Token == "reply-token-b" && call.Text == "reply-line_b");
            Assert.DoesNotContain(api.Calls, call => call.Path == "/v2/bot/message/push");

            await connectors[0].StopAsync(default);
            running.Remove(connectors[0]);
            Assert.Equal(503, (await receivers[0].ReceiveAsync(Request("after-stop", "after-stop-message", "after-stop", "after-stop-token-a"), default)).StatusCode);
            Assert.Equal(200, (await receivers[1].ReceiveAsync(Request("after-stop", "after-stop-message", "after-stop", "after-stop-token-b"), default)).StatusCode);
            await Completion("line_b", "after-stop").Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(invocations, item => item == ("line_b", "after-stop"));
            Assert.DoesNotContain(invocations, item => item == ("line_a", "after-stop"));
            Assert.Single(api.Calls, call => call.Token == "after-stop-token-b" && call.Text == "reply-line_b");
        }
        finally
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Task.WhenAll(running.Select(connector => connector.StopAsync(cancellation.Token)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lease_from_A_cannot_authorize_delivery_to_stopping_B(bool differentPaths)
    {
        using var api = new RecordingLineApi();
        var connectors = Load(api, differentPaths);
        AssertLoadContexts(connectors, differentPaths);
        var aEntered = NewSignal();
        var bEntered = NewSignal();
        var releaseA = NewSignal();
        var releaseB = NewSignal();
        var acceptance = new TaskCompletionSource<DeliveryAcceptance>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (envelope, cancellationToken) =>
        {
            if (envelope.ConnectorInstanceId == "line_b")
            {
                bEntered.TrySetResult();
                await releaseB.Task.WaitAsync(cancellationToken);
                return;
            }
            aEntered.TrySetResult();
            await releaseA.Task.WaitAsync(cancellationToken);
            acceptance.TrySetResult(await connectors[1].DeliverAsync(new(
                "line", "line_b", envelope.Chat, envelope.Message,
                new MessageContent("foreign-lease")), cancellationToken));
        });
        var running = new List<IConnector>();
        Task? stoppingB = null;
        try
        {
            foreach (var connector in connectors)
            {
                await connector.StartAsync(handler, default);
                running.Add(connector);
            }
            var a = Assert.IsAssignableFrom<IWebhookReceiver>(connectors[0]);
            var b = Assert.IsAssignableFrom<IWebhookReceiver>(connectors[1]);
            Assert.Equal(200, (await a.ReceiveAsync(Request("lease-a", "lease-message", "lease", "lease-token-a"), default)).StatusCode);
            Assert.Equal(200, (await b.ReceiveAsync(Request("lease-b", "lease-message", "lease", "lease-token-b"), default)).StatusCode);
            await Task.WhenAll(aEntered.Task, bEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            stoppingB = connectors[1].StopAsync(default);
            Assert.False(stoppingB.IsCompleted);
            Assert.Equal(503, (await b.ReceiveAsync(Request("probe", "probe-message", "probe", "probe-token"), default)).StatusCode);
            releaseA.TrySetResult();
            Assert.Equal(DeliveryAcceptance.Unavailable, await acceptance.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(api.Calls);
            releaseB.TrySetResult();
            await stoppingB.WaitAsync(TimeSpan.FromSeconds(5));
            running.Remove(connectors[1]);
        }
        finally
        {
            releaseA.TrySetResult();
            releaseB.TrySetResult();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (stoppingB is not null)
            {
                await stoppingB.WaitAsync(cancellation.Token);
                running.Remove(connectors[1]);
            }
            await Task.WhenAll(running.Select(connector => connector.StopAsync(cancellation.Token)));
        }
    }

    private static IReadOnlyList<IConnector> Load(RecordingLineApi api, bool differentPaths)
    {
        var loader = new ConnectorLoader(Published, NullLoggerFactory.Instance, new EpochClock(),
            () => new HttpClient(api, disposeHandler: false));
        return loader.Load([
            Config("line_a", DllName),
            Config("line_b", differentPaths ? "copy/" + DllName : DllName)
        ]);
    }

    private static ConnectorInstanceConfig Config(string id, string assembly) => new(
        "line", id, true, assembly, null, new Dictionary<string, string>
        {
            ["ChannelSecret"] = "test-secret",
            ["ChannelAccessToken"] = "test-token",
            ["Work:MaxConcurrency"] = "1",
            ["Stop:Grace"] = "00:00:30",
            ["Stop:JoinTimeout"] = "00:00:01"
        });

    private static void AssertLoadContexts(IReadOnlyList<IConnector> connectors, bool differentPaths)
    {
        var first = AssemblyLoadContext.GetLoadContext(connectors[0].GetType().Assembly);
        var second = AssemblyLoadContext.GetLoadContext(connectors[1].GetType().Assembly);
        Assert.NotSame(AssemblyLoadContext.Default, first);
        if (differentPaths) Assert.NotSame(first, second);
        else Assert.Same(first, second);
        Assert.NotSame(connectors[0], connectors[1]);
    }

    private static WebhookRequest Request(string eventId, string messageId, string text, string replyToken)
    {
        // Signature comes directly from the standard HMAC primitive, never from LINE implementation code.
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            events = new[]
            {
                new
                {
                    type = "message", mode = "active", webhookEventId = eventId, replyToken,
                    timestamp = 0L,
                    source = new { type = "group", userId = "U1", groupId = "G1" },
                    message = new { type = "text", id = messageId, text }
                }
            }
        });
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("test-secret"), body));
        return new(new Dictionary<string, string> { ["X-Line-Signature"] = signature }, body);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class EpochClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }
    private sealed class Handler(Func<InboundEnvelope, CancellationToken, Task> handle) : IInboundMessageHandler
    {
        public Task HandleAsync(InboundEnvelope envelope, CancellationToken cancellationToken) => handle(envelope, cancellationToken);
    }
    private sealed record ApiCall(string Path, string? Token, string? Text);
    private sealed class RecordingLineApi : HttpMessageHandler
    {
        public ConcurrentQueue<ApiCall> Calls { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            var root = body.RootElement;
            var token = root.TryGetProperty("replyToken", out var value) ? value.GetString() : null;
            var text = root.TryGetProperty("messages", out value) ? value[0].GetProperty("text").GetString() : null;
            Calls.Enqueue(new(request.RequestUri!.AbsolutePath, token, text));
            return new(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
    }
}
