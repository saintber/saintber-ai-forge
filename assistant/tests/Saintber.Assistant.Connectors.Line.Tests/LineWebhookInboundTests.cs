using System.Text;
using Microsoft.Extensions.Logging;
using Saintber.Assistant.Abstractions;
using Saintber.Assistant.Connectors.Core;

namespace Saintber.Assistant.Connectors.Line.Tests;

public class LineWebhookInboundTests
{
    private const string VectorBody = """{"destination":"U0","events":[]}""";
    private const string VectorSignature = "AQQTPDN0VEjXZIlgGdat3T+SL3wHGAG+cHc684p/XqU=";

    private static IWebhookInbound Create(CaptureLogger? logger = null, string instanceId = "line-main") =>
        new LineWebhookInbound(instanceId, "test-secret", logger ?? new CaptureLogger());

    private static WebhookRequest Request(string body, string? signature = null, string header = "X-Line-Signature") =>
        new(signature is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [header] = signature },
            Encoding.UTF8.GetBytes(body));

    [Fact]
    public void Independent_signature_vector_verifies()
    {
        Create().Verify(Request(VectorBody, VectorSignature));
    }

    [Fact]
    public void One_changed_body_byte_is_rejected()
    {
        Assert.Throws<WebhookVerificationException>(() =>
            Create().Verify(Request(VectorBody.Replace("U0", "U1"), VectorSignature)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("AQQTPDN0VEjXZIlgGdat3T+SL3wHGAG+cHc684p/XqUA")]
    [InlineData("AQQTPDN0VEjXZIlgGdat3T+SL3wHGAG+cHc684p/XqV=")]
    [InlineData("AA==")]
    public void Missing_invalid_or_changed_signature_is_rejected(string? signature)
    {
        Assert.Throws<WebhookVerificationException>(() => Create().Verify(Request(VectorBody, signature)));
    }

    [Fact]
    public void Raw_non_ascii_and_escaped_bytes_verify_with_case_insensitive_header()
    {
        const string body = """{"destination":"U0","events":[],"text":"中文 English \u4e2d \n"}""";
        Create().Verify(Request(body, "tgY1IgpVKqeTuLihFtLaIMwIiYmmyEmnrlrHbRIL4nY=", "x-line-signature"));
    }

    private static string Event(string sourceType = "user", string extraSource = "", string eventId = "01ABC",
        string messageId = "M42", string messageType = "text", string eventType = "message", string mode = "active",
        bool user = true, bool includeId = true) =>
        "{\"type\":\"" + eventType + "\",\"mode\":\"" + mode + "\","
        + (includeId ? "\"webhookEventId\":\"" + eventId + "\"," : "")
        + "\"timestamp\":1700000000123,\"replyToken\":\"token-1\",\"deliveryContext\":{\"isRedelivery\":true},"
        + "\"source\":{\"type\":\"" + sourceType + "\"" + (user ? ",\"userId\":\"U123\"" : "") + extraSource + "},"
        + "\"message\":{\"type\":\"" + messageType + "\",\"id\":\"" + messageId + "\",\"text\":\"你好 English\"}}";

    private static WebhookRequest Events(params string[] events) => Request("{\"events\":[" + string.Join(",", events) + "]}");

    [Theory]
    [InlineData("user", "", "user", "line:user:U123", "userId", "U123")]
    [InlineData("group", ",\"groupId\":\"C987\"", "group", "line:group:C987", "groupId", "C987")]
    [InlineData("room", ",\"roomId\":\"R555\"", "room", "line:room:R555", "roomId", "R555")]
    public void Text_event_carries_platform_fields_and_matching_stable_keys(
        string sourceType, string extraSource, string chatKind, string canonical, string propertyName, string id)
    {
        var parsed = Assert.Single(Create().Parse(Events(Event(sourceType, extraSource))));
        Assert.Equal("01ABC", parsed.EventId);
        Assert.Equal("token-1", parsed.ReplyToken);
        Assert.Equal(new DateTimeOffset(2023, 11, 14, 22, 13, 20, 123, TimeSpan.Zero), parsed.EventTime);
        var envelope = parsed.Envelope;
        Assert.Equal("line", envelope.ConnectorType);
        Assert.Equal("line-main", envelope.ConnectorInstanceId);
        Assert.Equal(parsed.EventTime, envelope.OccurredAt);
        Assert.Equal("line:user:U123", envelope.Actor.CanonicalValue);
        Assert.Equal("U123", envelope.Actor.Properties.GetProperty("userId").GetString());
        Assert.Equal(chatKind, envelope.Chat.Kind);
        Assert.Equal(canonical, envelope.Chat.CanonicalValue);
        Assert.Equal(id, envelope.Chat.Properties.GetProperty(propertyName).GetString());
        if (sourceType == "user") Assert.Same(envelope.Actor, envelope.Chat);
        Assert.Null(envelope.Thread);
        Assert.Equal("message", envelope.Message.Kind);
        Assert.Equal("line:message:M42", envelope.Message.CanonicalValue);
        Assert.Equal("M42", envelope.Message.Properties.GetProperty("messageId").GetString());
        Assert.Equal("你好 English", envelope.Content.Text);
    }

    [Fact]
    public void Multiple_events_keep_original_order()
    {
        var parsed = Create().Parse(Events(Event(eventId: "event-1"), Event(eventId: "event-2"), Event(eventId: "event-3")));
        Assert.Equal(new[] { "event-1", "event-2", "event-3" }, parsed.Select(e => e.EventId));
    }

    [Fact]
    public void Skipped_events_only_log_types_and_do_not_hide_later_text()
    {
        var logger = new CaptureLogger();
        var parsed = Create(logger).Parse(Events(
            Event(messageType: "image"), Event(messageType: "sticker"), Event(eventType: "follow"),
            Event(mode: "standby"), Event(user: false), Event(includeId: false), Event(mode: "unknown"),
            Event(eventId: "accepted")));
        Assert.Equal("accepted", Assert.Single(parsed).EventId);
        Assert.Equal(7, logger.Entries.Count);
        Assert.All(logger.Entries, entry =>
        {
            Assert.DoesNotContain("token-1", entry);
            Assert.DoesNotContain("01ABC", entry);
            Assert.DoesNotContain("U123", entry);
            Assert.DoesNotContain("你好", entry);
            Assert.DoesNotContain("English", entry);
            Assert.DoesNotContain("isRedelivery", entry);
        });
        Assert.Contains(logger.Entries, e => e.Contains("follow", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Contains("image", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Contains("sticker", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"events\":null}")]
    [InlineData("{\"events\":{}}")]
    [InlineData("{\"events\":[null]}")]
    [InlineData("{\"events\":[42]}")]
    public void Malformed_payload_is_connector_payload_error(string body)
    {
        Assert.Throws<ConnectorPayloadException>(() => Create().Parse(Request(body)));
    }

    [Theory]
    [InlineData("\"timestamp\":1700000000123", "\"timestamp\":\"invalid\"")]
    [InlineData("\"timestamp\":1700000000123", "\"timestamp\":9223372036854775807")]
    [InlineData("\"type\":\"user\"", "\"type\":\"group\"")]
    [InlineData("\"text\":\"你好 English\"", "\"text\":null")]
    [InlineData("\"id\":\"M42\"", "\"id\":null")]
    public void Invalid_eligible_text_payload_is_connector_payload_error(string original, string changed)
    {
        Assert.Throws<ConnectorPayloadException>(() => Create().Parse(Events(Event().Replace(original, changed))));
    }

    [Fact]
    public void Empty_events_are_supported()
    {
        Assert.Empty(Create().Parse(Request(VectorBody)));
    }

    [Fact]
    public void Keys_and_sanitized_metadata_survive_released_source()
    {
        var bytes = Events(Event()).Body.ToArray();
        var parsed = Assert.Single(Create().Parse(new WebhookRequest(new Dictionary<string, string>(), bytes)));
        Array.Clear(bytes);
        var metadata = parsed.Envelope.RawMetadata;
        Assert.False(metadata.TryGetProperty("replyToken", out _));
        Assert.False(metadata.TryGetProperty("webhookEventId", out _));
        Assert.False(metadata.TryGetProperty("deliveryContext", out _));
        Assert.DoesNotContain("token-1", metadata.GetRawText());
        Assert.DoesNotContain("01ABC", metadata.GetRawText());
        Assert.Equal("U123", metadata.GetProperty("source").GetProperty("userId").GetString());
        Assert.Equal("M42", metadata.GetProperty("message").GetProperty("id").GetString());
        Assert.Equal(1700000000123, metadata.GetProperty("timestamp").GetInt64());
        Assert.Equal("U123", parsed.Envelope.Actor.Properties.GetProperty("userId").GetString());
        Assert.Equal("M42", parsed.Envelope.Message.Properties.GetProperty("messageId").GetString());
    }

    [Fact]
    public void Keys_do_not_depend_on_instance_event_or_json_property_order()
    {
        var first = Assert.Single(Create(instanceId: "instance-a").Parse(Events(Event(eventId: "first")))).Envelope;
        var secondBody = Event(eventId: "second").Replace("\"type\":\"user\",\"userId\":\"U123\"", "\"userId\":\"U123\",\"type\":\"user\"");
        var second = Assert.Single(Create(instanceId: "instance-b").Parse(Events(secondBody))).Envelope;
        Assert.Equal(first.Actor, second.Actor);
        Assert.Equal(first.Chat, second.Chat);
        Assert.Equal(first.Message, second.Message);
        Assert.Equal("line:user:U123", second.Chat.CanonicalValue);
        Assert.Equal("line:message:M42", second.Message.CanonicalValue);
    }

    private sealed class CaptureLogger : ILogger
    {
        public List<string> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add(formatter(state, exception));
    }
}
