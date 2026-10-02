using Saintber.Assistant.Abstractions;
namespace Saintber.Assistant.Connectors.Core;
public sealed record PlatformCapabilities(bool Reply, bool Push, bool Activity, TimeSpan ReplyValidity);
public sealed record PlatformInboundEvent(string EventId, DateTimeOffset EventTime, string? ReplyToken, InboundEnvelope Envelope);
public interface IWebhookInbound
{
    void Verify(WebhookRequest request);
    IReadOnlyList<PlatformInboundEvent> Parse(WebhookRequest request);
}
public interface IMessagingPlatform
{
    PlatformCapabilities Capabilities { get; }
    Task ReplyAsync(string replyToken, MessageContent content, CancellationToken cancellationToken);
    Task PushAsync(ExternalKey chat, MessageContent content, CancellationToken cancellationToken);
    Task<IAsyncDisposable?> StartActivityAsync(ExternalKey chat, CancellationToken cancellationToken);
}
public sealed class WebhookVerificationException() : Exception("Webhook verification failed");
public sealed class ConnectorPayloadException() : Exception("Webhook payload invalid");
