using System.Text.Json;
using Microsoft.Extensions.Logging;
using Saintber.Assistant.Abstractions;
using Saintber.Assistant.Connectors.Core;

namespace Saintber.Assistant.Connectors.Line;

public sealed class LinePlatform : IMessagingPlatform, IWebhookInbound
{
    private readonly LineWebhookInbound _inbound;
    private readonly LineApiClient _api;
    private readonly int _loadingSeconds;
    private readonly ILogger _logger;

    public LinePlatform(string instanceId, string secret, LineApiClient api, int loadingSeconds, ILogger logger)
    {
        _inbound = new LineWebhookInbound(instanceId, secret, logger);
        _api = api;
        _loadingSeconds = loadingSeconds;
        _logger = logger;
    }

    // Reply token 有效期以保守的 50 秒估計；實際投遞仍由 LINE 判定。
    public PlatformCapabilities Capabilities { get; } = new(true, true, true, TimeSpan.FromSeconds(50));
    public void Verify(WebhookRequest request) => _inbound.Verify(request);
    public IReadOnlyList<PlatformInboundEvent> Parse(WebhookRequest request) => _inbound.Parse(request);

    public Task ReplyAsync(string replyToken, MessageContent content, CancellationToken cancellationToken) =>
        _api.ReplyAsync(replyToken, LimitText(content.Text), cancellationToken);

    public Task PushAsync(ExternalKey chat, MessageContent content, CancellationToken cancellationToken) =>
        TryDestination(chat, out var destination)
            ? _api.PushAsync(destination, LimitText(content.Text), cancellationToken)
            : Task.CompletedTask;

    public async Task<IAsyncDisposable?> StartActivityAsync(ExternalKey chat, CancellationToken cancellationToken)
    {
        if (chat.Kind != "user" || !TryDestination(chat, out var destination))
            return null;
        await _api.LoadingAsync(destination, _loadingSeconds, cancellationToken).ConfigureAwait(false);
        return LoadingActivity.Instance;
    }

    private bool TryDestination(ExternalKey chat, out string destination)
    {
        destination = "";
        var property = chat.Kind switch { "user" => "userId", "group" => "groupId", "room" => "roomId", _ => null };
        if (property is not null && chat.Properties.ValueKind == JsonValueKind.Object
            && chat.Properties.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
        {
            var id = value.GetString();
            if (!string.IsNullOrEmpty(id) && StringComparer.Ordinal.Equals(chat.CanonicalValue,
                LineExternalKeys.Create(chat.Kind, id).CanonicalValue))
            {
                destination = id;
                return true;
            }
        }
        _logger.LogWarning("Message undeliverable: invalid LINE chat key");
        return false;
    }

    private static string LimitText(string text)
    {
        const int limit = 5000;
        if (text.Length <= limit) return text;
        var length = char.IsHighSurrogate(text[limit - 1]) && char.IsLowSurrogate(text[limit]) ? limit - 1 : limit;
        return text[..length];
    }

    /// <summary>LINE loading 在指定秒數或訊息到達後結束，沒有取消 API。</summary>
    private sealed class LoadingActivity : IAsyncDisposable
    {
        public static LoadingActivity Instance { get; } = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
