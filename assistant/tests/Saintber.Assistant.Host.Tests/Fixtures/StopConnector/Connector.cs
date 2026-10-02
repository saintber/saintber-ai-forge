using System.Text.Json;
using Microsoft.Extensions.Logging;
using Saintber.Assistant.Abstractions;
using Saintber.Assistant.Connectors.Core;

public sealed class StopFixtureFactory : IConnectorFactory
{
    public string ConnectorType => "stop_fixture";
    public IReadOnlyList<SettingDescriptor> Settings => [new("Mode", SettingKind.String, DefaultValue: "cooperative"), ..FrameworkSettings.Descriptors];
    public IConnector Create(ConnectorCreationContext context)
    {
        var core = new WebhookConnector(ConnectorType, context.Config.InstanceId, new Inbound(context.Config.InstanceId), new Platform(), context.TimeProvider, context.LoggerFactory.CreateLogger("StopFixture"), FrameworkSettings.Parse(context.Config.Settings));
        return new Wrapper(core, context.Config.Settings.GetValueOrDefault("Mode", "cooperative"));
    }
    private sealed class Wrapper(WebhookConnector core, string mode) : IConnector, IWebhookReceiver
    {
        public string ConnectorType => core.ConnectorType;
        public string InstanceId => core.InstanceId;
        public TimeSpan StopBudget => core.StopBudget;
        public Task StartAsync(IInboundMessageHandler handler, CancellationToken ct) => core.StartAsync(new TestHandler(mode), ct);
        public Task StopAsync(CancellationToken ct) => core.StopAsync(ct);
        public Task<DeliveryAcceptance> DeliverAsync(OutboundEnvelope envelope, CancellationToken ct) => core.DeliverAsync(envelope, ct);
        public Task<WebhookResult> ReceiveAsync(WebhookRequest request, CancellationToken ct) => core.ReceiveAsync(request, ct);
    }
    private sealed class TestHandler(string mode) : IInboundMessageHandler
    {
        public async Task HandleAsync(InboundEnvelope envelope, CancellationToken ct)
        {
            Console.WriteLine("FIXTURE handler entered " + mode);
            if (mode == "uncooperative") { await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task; return; }
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { await Task.Delay(TimeSpan.FromSeconds(2)); Console.WriteLine("FIXTURE cleanup completed"); }
        }
    }
    private sealed class Inbound(string id) : IWebhookInbound
    {
        public void Verify(WebhookRequest request) { }
        public IReadOnlyList<PlatformInboundEvent> Parse(WebhookRequest request)
        {
            var properties = JsonSerializer.SerializeToElement(new { id = "test" });
            var key = new ExternalKey("fixture", "fixture:test", properties);
            var now = DateTimeOffset.UtcNow;
            return [new("stop-event", now, null, new("stop_fixture", id, key, key, null, key, new("test"), now, properties))];
        }
    }
    private sealed class Platform : IMessagingPlatform
    {
        public PlatformCapabilities Capabilities => new(false, false, false, TimeSpan.Zero);
        public Task ReplyAsync(string token, MessageContent content, CancellationToken ct) => Task.CompletedTask;
        public Task PushAsync(ExternalKey chat, MessageContent content, CancellationToken ct) => Task.CompletedTask;
        public Task<IAsyncDisposable?> StartActivityAsync(ExternalKey chat, CancellationToken ct) => Task.FromResult<IAsyncDisposable?>(null);
    }
}
