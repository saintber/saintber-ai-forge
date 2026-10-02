using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Saintber.Assistant.Abstractions;
using Saintber.Assistant.Connectors.Core;

namespace Saintber.Assistant.Connectors.Line;

public sealed class LineWebhookInbound(string instanceId, string channelSecret, ILogger logger) : IWebhookInbound
{
    public void Verify(WebhookRequest request)
    {
        var signature = request.Headers.FirstOrDefault(header =>
            string.Equals(header.Key, "X-Line-Signature", StringComparison.OrdinalIgnoreCase)).Value;
        Span<byte> decoded = stackalloc byte[32];
        if (signature is null || signature.Length != 44
            || !Convert.TryFromBase64String(signature, decoded, out var written) || written != 32)
            throw new WebhookVerificationException();

        var digest = HMACSHA256.HashData(Encoding.UTF8.GetBytes(channelSecret), request.Body.Span);
        var expected = Encoding.ASCII.GetBytes(Convert.ToBase64String(digest));
        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(signature)))
            throw new WebhookVerificationException();
    }

    public IReadOnlyList<PlatformInboundEvent> Parse(WebhookRequest request)
    {
        try
        {
            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
                throw new ConnectorPayloadException();

            List<PlatformInboundEvent> parsed = [];
            foreach (var item in events.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new ConnectorPayloadException();
                var eventType = String(item, "type");
                var message = item.TryGetProperty("message", out var value) ? value : default;
                var messageType = String(message, "type");
                var source = item.TryGetProperty("source", out value) ? value : default;
                var userId = String(source, "userId");
                var eventId = String(item, "webhookEventId");
                if (eventType != "message" || messageType != "text" || String(item, "mode") != "active"
                    || string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(eventId))
                {
                    logger.LogInformation("Skipped LINE event type {EventType}",
                        eventType == "message" ? messageType ?? "unknown" : eventType ?? "unknown");
                    continue;
                }

                var actor = LineExternalKeys.Create("user", userId);
                var chat = String(source, "type") switch
                {
                    "user" => actor,
                    "group" => LineExternalKeys.Create("group", RequiredString(source, "groupId")),
                    "room" => LineExternalKeys.Create("room", RequiredString(source, "roomId")),
                    _ => throw new ConnectorPayloadException()
                };
                if (!item.TryGetProperty("timestamp", out var timestamp)
                    || timestamp.ValueKind != JsonValueKind.Number || !timestamp.TryGetInt64(out var milliseconds))
                    throw new ConnectorPayloadException();
                var occurredAt = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
                var messageKey = LineExternalKeys.Create("message", RequiredString(message, "id"));
                var text = String(message, "text") ?? throw new ConnectorPayloadException();
                var envelope = new InboundEnvelope("line", instanceId, actor, chat, null, messageKey,
                    new MessageContent(text), occurredAt, SanitizeMetadata(item));
                parsed.Add(new PlatformInboundEvent(eventId, occurredAt, String(item, "replyToken"), envelope));
            }
            return parsed;
        }
        catch (JsonException)
        {
            throw new ConnectorPayloadException();
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ConnectorPayloadException();
        }
    }

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string RequiredString(JsonElement element, string property)
    {
        var value = String(element, property);
        return !string.IsNullOrEmpty(value) ? value : throw new ConnectorPayloadException();
    }

    private static JsonElement SanitizeMetadata(JsonElement item)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in item.EnumerateObject())
            {
                if (property.Name is "replyToken" or "webhookEventId" or "deliveryContext")
                    continue;
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        using var metadata = JsonDocument.Parse(buffer.ToArray());
        return metadata.RootElement.Clone();
    }
}
