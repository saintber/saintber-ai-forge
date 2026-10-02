using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Saintber.Assistant.Abstractions;
using Saintber.Assistant.Connectors.Core;

namespace Saintber.Assistant.Connectors.Line.Tests;

public class LineOutboundTests
{
    [Fact]
    public async Task Reply_uses_post_bearer_and_one_text_message()
    {
        var f = new LineFixture();
        await f.Platform.ReplyAsync("token-1", new("hello"), default);
        var request = Assert.Single(f.Handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.line.me/v2/bot/message/reply", request.Url.AbsoluteUri);
        Assert.Equal("Bearer ACCESS-TOKEN-SECRET", request.Authorization);
        Assert.Equal("application/json", request.ContentType);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("token-1", body.RootElement.GetProperty("replyToken").GetString());
        AssertText(body.RootElement, "hello");
    }

    [Theory]
    [InlineData("user", "U123")]
    [InlineData("group", "C987")]
    [InlineData("room", "R555")]
    public async Task Push_uses_the_chat_properties_destination(string kind, string id)
    {
        var f = new LineFixture();
        await f.Platform.PushAsync(LineExternalKeys.Create(kind, id), new("hello"), default);
        var request = Assert.Single(f.Handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v2/bot/message/push", request.Url.AbsolutePath);
        Assert.Equal("Bearer ACCESS-TOKEN-SECRET", request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(id, body.RootElement.GetProperty("to").GetString());
        AssertText(body.RootElement, "hello");
    }

    [Theory]
    [InlineData("https://line.test/prefix", "/prefix/v2/bot/message/reply")]
    [InlineData("https://line.test/prefix/", "/prefix/v2/bot/message/reply")]
    [InlineData("https://line.test/one/two", "/one/two/v2/bot/message/reply")]
    public async Task Api_paths_preserve_the_configured_base_prefix(string apiBase, string path)
    {
        var f = new LineFixture(apiBase);
        await f.Platform.ReplyAsync("token-1", new("hello"), default);
        Assert.Equal(path, Assert.Single(f.Handler.Requests).Url.AbsolutePath);
    }

    [Theory]
    [InlineData("user", "line:user:U123", "{\"userId\":\"U999\"}")]
    [InlineData("group", "line:group:C987", "{\"groupId\":\"C999\"}")]
    [InlineData("room", "line:room:R555", "{\"roomId\":\"R999\"}")]
    [InlineData("user", "line:user:U123", "{\"userId\":1}")]
    [InlineData("user", "line:user:U123", "{}")]
    [InlineData("user", "line:user:U123", "null")]
    [InlineData("message", "line:message:M42", "{\"messageId\":\"M42\"}")]
    public async Task Invalid_chat_is_undeliverable_without_http_or_identifier_disclosure(string kind, string canonical, string properties)
    {
        var f = new LineFixture();
        await f.Platform.PushAsync(new ExternalKey(kind, canonical, JsonSerializer.Deserialize<JsonElement>(properties)), new("TEXT-SECRET"), default);
        Assert.Empty(f.Handler.Requests);
        Assert.Contains(f.Logger.Messages, message => message.Contains("undeliverable", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(f.Logger.Messages, message => message.Contains("U123") || message.Contains("U999")
            || message.Contains("C987") || message.Contains("C999") || message.Contains("R555") || message.Contains("R999")
            || message.Contains("M42") || message.Contains("TEXT-SECRET"));
    }

    [Theory]
    [InlineData("reply")]
    [InlineData("push")]
    [InlineData("loading")]
    public async Task Http_400_errors_contain_only_status_without_response_or_sensitive_values(string operation)
    {
        var f = new LineFixture();
        f.Handler.Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("BODY-SECRET ACCESS-TOKEN-SECRET token-1 TEXT-SECRET https://secret-api.test")
        });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => operation switch
        {
            "reply" => f.Platform.ReplyAsync("token-1", new("TEXT-SECRET"), default),
            "push" => f.Platform.PushAsync(LineExternalKeys.Create("user", "U123"), new("TEXT-SECRET"), default),
            _ => f.Platform.StartActivityAsync(LineExternalKeys.Create("user", "U123"), default)
        });
        Assert.Contains("400", error.Message);
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("SECRET", error.ToString());
        Assert.DoesNotContain("token-1", error.ToString());
        Assert.DoesNotContain("secret-api", error.ToString());
        Assert.DoesNotContain(f.Logger.Messages, message => message.Contains("SECRET") || message.Contains("token-1"));
    }

    [Theory]
    [InlineData("reply")]
    [InlineData("push")]
    public async Task Transport_exception_is_sanitized_without_retries(string operation)
    {
        var f = new LineFixture();
        f.Handler.Response = (_, _) => throw new HttpRequestException("ACCESS-TOKEN-SECRET BODY-SECRET TEXT-SECRET token-1");
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => operation == "reply"
            ? f.Platform.ReplyAsync("token-1", new("TEXT-SECRET"), default)
            : f.Platform.PushAsync(LineExternalKeys.Create("user", "U123"), new("TEXT-SECRET"), default));
        Assert.Null(error.InnerException); Assert.DoesNotContain("SECRET", error.ToString()); Assert.DoesNotContain("token-1", error.ToString());
        Assert.Single(f.Handler.Requests);
    }

    [Theory]
    [InlineData("reply")]
    [InlineData("push")]
    public async Task Text_length_is_limited_to_five_thousand_utf16_units(string operation)
    {
        var f = new LineFixture();
        await Send(f, operation, new string('a', 6000));
        using var json = JsonDocument.Parse(Assert.Single(f.Handler.Requests).Body);
        AssertText(json.RootElement, new string('a', 5000));
    }

    [Theory]
    [InlineData("reply")]
    [InlineData("push")]
    public async Task Emoji_crossing_the_limit_is_removed_as_a_complete_pair(string operation)
    {
        var f = new LineFixture();
        await Send(f, operation, new string('a', 4999) + "😀more");
        using var json = JsonDocument.Parse(Assert.Single(f.Handler.Requests).Body);
        var text = json.RootElement.GetProperty("messages")[0].GetProperty("text").GetString();
        Assert.Equal(new string('a', 4999), text); Assert.DoesNotContain(text!, char.IsSurrogate);
    }

    [Theory]
    [InlineData("reply")]
    [InlineData("push")]
    public async Task Exactly_five_thousand_units_and_complete_emoji_at_boundary_are_unchanged(string operation)
    {
        var f = new LineFixture(); var text = new string('a', 4998) + "😀";
        await Send(f, operation, text);
        using var json = JsonDocument.Parse(Assert.Single(f.Handler.Requests).Body);
        AssertText(json.RootElement, text);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(60)]
    public async Task User_loading_request_uses_configured_seconds_and_returns_noop_disposable(int seconds)
    {
        var f = new LineFixture(loadingSeconds: seconds);
        var activity = await f.Platform.StartActivityAsync(LineExternalKeys.Create("user", "U123"), default);
        Assert.NotNull(activity);
        var request = Assert.Single(f.Handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("/v2/bot/chat/loading/start", request.Url.AbsolutePath);
        Assert.Equal("Bearer ACCESS-TOKEN-SECRET", request.Authorization);
        using var json = JsonDocument.Parse(request.Body);
        Assert.Equal("U123", json.RootElement.GetProperty("chatId").GetString());
        Assert.Equal(seconds, json.RootElement.GetProperty("loadingSeconds").GetInt32());
        await activity.DisposeAsync(); await activity.DisposeAsync(); Assert.Single(f.Handler.Requests);
    }

    [Theory]
    [InlineData("group", "C987")]
    [InlineData("room", "R555")]
    public async Task Group_and_room_activity_return_null_without_request(string kind, string id)
    {
        var f = new LineFixture();
        Assert.Null(await f.Platform.StartActivityAsync(LineExternalKeys.Create(kind, id), default)); Assert.Empty(f.Handler.Requests);
    }

    [Fact]
    public async Task Invalid_user_activity_key_does_not_make_request()
    {
        var f = new LineFixture();
        Assert.Null(await f.Platform.StartActivityAsync(new ExternalKey("user", "line:user:U123",
            JsonSerializer.SerializeToElement(new { userId = "U999" })), default));
        Assert.Empty(f.Handler.Requests); Assert.DoesNotContain(f.Logger.Messages, message => message.Contains("U123") || message.Contains("U999"));
    }

    [Fact]
    public async Task Late_loading_result_can_be_disposed_without_any_http_action()
    {
        var f = new LineFixture(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Handler.Response = (_, _) => { entered.SetResult(); return response.Task; };
        using var cancellation = new CancellationTokenSource();
        var pending = f.Platform.StartActivityAsync(LineExternalKeys.Create("user", "U123"), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        response.SetResult(new HttpResponseMessage(HttpStatusCode.OK));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5)); Assert.NotNull(result);
        await result.DisposeAsync(); Assert.Single(f.Handler.Requests);
    }

    [Fact]
    public void Platform_declares_all_capabilities_and_delegates_signature_and_payload_to_existing_parser()
    {
        var f = new LineFixture();
        Assert.Equal(new PlatformCapabilities(true, true, true, TimeSpan.FromSeconds(50)), f.Platform.Capabilities);
        var request = new WebhookRequest(new Dictionary<string, string> { ["X-Line-Signature"] = "AQQTPDN0VEjXZIlgGdat3T+SL3wHGAG+cHc684p/XqU=" },
            Encoding.UTF8.GetBytes("""{"destination":"U0","events":[]}"""));
        f.Platform.Verify(request); Assert.Empty(f.Platform.Parse(request));
        var bad = new WebhookRequest(new Dictionary<string, string> { ["X-Line-Signature"] = "SUPPLIED-SIGNATURE-SECRET" }, Encoding.UTF8.GetBytes("BODY-SECRET"));
        var error = Assert.Throws<WebhookVerificationException>(() => f.Platform.Verify(bad));
        Assert.DoesNotContain("SECRET", error.ToString()); Assert.DoesNotContain(f.Logger.Messages, message => message.Contains("SECRET"));
    }

    private static Task Send(LineFixture fixture, string operation, string text) => operation == "reply"
        ? fixture.Platform.ReplyAsync("token-1", new(text), default)
        : fixture.Platform.PushAsync(LineExternalKeys.Create("user", "U123"), new(text), default);
    private static void AssertText(JsonElement body, string text)
    {
        var message = Assert.Single(body.GetProperty("messages").EnumerateArray());
        Assert.Equal("text", message.GetProperty("type").GetString()); Assert.Equal(text, message.GetProperty("text").GetString());
    }
}

internal sealed class LineFixture
{
    public RecordingHandler Handler { get; } = new();
    public RecordingLogger Logger { get; } = new();
    public HttpClient Http { get; }
    public LinePlatform Platform { get; }
    public LineFixture(string apiBase = "https://api.line.me", int loadingSeconds = 5)
    {
        Http = new HttpClient(Handler);
        Platform = new("main", "test-secret", new LineApiClient(Http, new Uri(apiBase), "ACCESS-TOKEN-SECRET", Logger), loadingSeconds, Logger);
    }
}
internal sealed record RecordedRequest(HttpMethod Method, Uri Url, string? Authorization, string? ContentType, string Body);
internal sealed class RecordingHandler : HttpMessageHandler
{
    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Response { get; set; } = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(new(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(),
            request.Content?.Headers.ContentType?.MediaType, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
        return await Response(request, cancellationToken);
    }
}
internal sealed class RecordingLogger : ILogger
{
    public ConcurrentQueue<string> Messages { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
}
