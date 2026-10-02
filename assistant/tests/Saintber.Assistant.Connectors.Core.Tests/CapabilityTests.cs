using Microsoft.Extensions.Time.Testing;
using Saintber.Assistant.Abstractions;
namespace Saintber.Assistant.Connectors.Core.Tests;

public class CapabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsupported_activity_and_reply_are_never_invoked(bool supportsPush)
    {
        var clock = new FakeTimeProvider();
        var inbound = new FakeInbound { Events = [PipelineTests.Event(clock, "E1")] };
        var platform = new FakePlatform { Capabilities = new(false, supportsPush, false, TimeSpan.FromSeconds(50)) };
        var logger = new CaptureLogger();
        var connector = new Saintber.Assistant.Connectors.Core.WebhookConnector("fake", "main", inbound, platform, clock, logger, new());
        var completed = new TaskCompletionSource<DeliveryAcceptance>(TaskCreationOptions.RunContinuationsAsynchronously);
        await connector.StartAsync(new FakeHandler(async (envelope, ct) =>
        {
            completed.TrySetResult(await connector.DeliverAsync(new("fake", "main", envelope.Chat, envelope.Message, new("reply")), ct));
        }), default);
        try
        {
            Assert.Equal(200, (await connector.ReceiveAsync(new(new Dictionary<string,string>(), ReadOnlyMemory<byte>.Empty), default)).StatusCode);
            Assert.Equal(DeliveryAcceptance.Accepted, await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, platform.ActivityCalls);
            Assert.Equal(0, platform.ReplyCalls);
            Assert.Equal(supportsPush ? 1 : 0, platform.PushCalls);
            if (!supportsPush) Assert.Contains(logger.Messages, message => message.Contains("undeliverable"));
        }
        finally { await connector.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}
