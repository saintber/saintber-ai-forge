using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Saintber.Assistant.Abstractions;
using Saintber.Assistant.Connectors.Core;

namespace Saintber.Assistant.Connectors.Line.Tests;

public class FactoryTests
{
    [Fact]
    public void Schema_includes_required_secret_defaults_and_every_core_descriptor()
    {
        IConnectorFactory factory = new LineConnectorFactory();
        Assert.Equal("line", factory.ConnectorType);
        Assert.Equal(16, factory.Settings.Count);
        Assert.Equal(16, factory.Settings.Select(s => s.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var key in new[] { "ChannelSecret", "ChannelAccessToken" })
        {
            var descriptor = Assert.Single(factory.Settings, s => s.Key == key);
            Assert.Equal(SettingKind.String, descriptor.Kind); Assert.True(descriptor.Required); Assert.True(descriptor.Secret);
        }
        var api = factory.Settings.Single(s => s.Key == "ApiBaseUrl"); Assert.Equal("https://api.line.me", api.DefaultValue);
        var loading = factory.Settings.Single(s => s.Key == "LoadingSeconds");
        Assert.Equal(SettingKind.Integer, loading.Kind); Assert.Equal("5", loading.DefaultValue);
        Assert.Equal("5", loading.Minimum); Assert.Equal("60", loading.Maximum);
        Assert.All(FrameworkSettings.Descriptors, descriptor => Assert.Equal(descriptor, factory.Settings.Single(s => s.Key == descriptor.Key)));
    }

    [Theory]
    [InlineData("ChannelSecret", null)]
    [InlineData("ChannelSecret", "")]
    [InlineData("ChannelAccessToken", null)]
    [InlineData("ChannelAccessToken", "")]
    public void Missing_or_empty_secret_fails_with_key_only_before_creating_client(string key, string? value)
    {
        var f = new FactoryFixture();
        if (value is null) f.Settings.Remove(key); else f.Settings[key] = value;
        var error = Assert.Throws<ArgumentException>(() => f.Create());
        Assert.Contains(key, error.Message); Assert.DoesNotContain("SECRET", error.ToString()); Assert.Null(error.InnerException);
        Assert.Equal(0, f.ClientCreations);
    }

    [Fact]
    public void Both_missing_secret_keys_are_named_without_setting_values()
    {
        var f = new FactoryFixture(); f.Settings.Remove("ChannelSecret"); f.Settings.Remove("ChannelAccessToken");
        var error = Assert.Throws<ArgumentException>(() => f.Create());
        Assert.Contains("ChannelSecret", error.Message); Assert.Contains("ChannelAccessToken", error.Message); Assert.DoesNotContain("SECRET", error.ToString());
    }

    [Theory]
    [InlineData("7")]
    [InlineData("65")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("not-integer-SECRET")]
    public void Loading_validation_names_key_without_value(string loading)
    {
        var f = new FactoryFixture(); f.Settings["LoadingSeconds"] = loading;
        var error = Assert.Throws<ArgumentException>(() => f.Create());
        Assert.Equal("LoadingSeconds", error.Message); Assert.DoesNotContain("SECRET", error.ToString()); Assert.Equal(0, f.ClientCreations);
    }

    [Theory]
    [InlineData("not-a-uri-SECRET")]
    [InlineData("/relative-SECRET")]
    [InlineData("ftp://SECRET.test")]
    [InlineData("")]
    public void Invalid_base_uri_error_contains_only_setting_key(string uri)
    {
        var f = new FactoryFixture(); f.Settings["ApiBaseUrl"] = uri;
        var error = Assert.Throws<ArgumentException>(() => f.Create());
        Assert.Equal("ApiBaseUrl", error.Message); Assert.DoesNotContain("SECRET", error.ToString()); Assert.Equal(0, f.ClientCreations);
    }

    [Fact]
    public async Task Defaults_compose_webhook_platform_and_infinite_http_timeout_with_matching_context()
    {
        var f = new FactoryFixture(); var connector = f.Create();
        try
        {
            Assert.Equal("line", connector.ConnectorType); Assert.Equal("line-alt", connector.InstanceId);
            Assert.IsAssignableFrom<IWebhookReceiver>(connector); Assert.IsType<WebhookConnector>(connector);
            Assert.Equal(Timeout.InfiniteTimeSpan, f.Http.Timeout); Assert.Equal(1, f.ClientCreations);
            Assert.Same(f.Clock, typeof(WebhookConnector).GetField("_clock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connector));
            var platform = (LinePlatform)typeof(WebhookConnector).GetField("_platform", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connector)!;
            Assert.IsAssignableFrom<IWebhookInbound>(platform); Assert.IsAssignableFrom<IMessagingPlatform>(platform);
            await platform.StartActivityAsync(LineExternalKeys.Create("user", "U123"), default);
            var request = Assert.Single(f.Handler.Requests);
            Assert.Equal("https://api.line.me/v2/bot/chat/loading/start", request.Url.AbsoluteUri);
            using var json = JsonDocument.Parse(request.Body); Assert.Equal(5, json.RootElement.GetProperty("loadingSeconds").GetInt32());
            var empty = new WebhookRequest(new Dictionary<string, string> { ["X-Line-Signature"] = "AQQTPDN0VEjXZIlgGdat3T+SL3wHGAG+cHc684p/XqU=" },
                Encoding.UTF8.GetBytes("""{"destination":"U0","events":[]}"""));
            platform.Verify(empty); Assert.Empty(platform.Parse(empty));
        }
        finally { await connector.StopAsync(default); }
    }

    [Fact]
    public async Task Case_insensitive_settings_and_overrides_are_applied_to_context_platform_and_core()
    {
        var f = new FactoryFixture();
        f.Settings["channelSecret"] = f.Settings["ChannelSecret"]; f.Settings.Remove("ChannelSecret");
        f.Settings["channelaccesstoken"] = f.Settings["ChannelAccessToken"]; f.Settings.Remove("ChannelAccessToken");
        f.Settings["apibaseurl"] = "https://line.test/api-prefix";
        f.Settings["loadingseconds"] = "10"; f.Settings["Stop:Grace"] = "00:00:20"; f.Settings["Timeouts:Send"] = "00:02:40";
        var connector = f.Create();
        try
        {
            Assert.Equal(TimeSpan.FromSeconds(25), connector.StopBudget); Assert.Equal(Timeout.InfiniteTimeSpan, f.Http.Timeout);
            var platform = (LinePlatform)typeof(WebhookConnector).GetField("_platform", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connector)!;
            await platform.StartActivityAsync(LineExternalKeys.Create("user", "U123"), default);
            var request = Assert.Single(f.Handler.Requests); Assert.Equal("/api-prefix/v2/bot/chat/loading/start", request.Url.AbsolutePath);
            using var json = JsonDocument.Parse(request.Body); Assert.Equal(10, json.RootElement.GetProperty("loadingSeconds").GetInt32());
        }
        finally { await connector.StopAsync(default); }
    }

    [Fact]
    public void Invalid_framework_duration_preserves_key_only_error()
    {
        var f = new FactoryFixture(); f.Settings["Timeouts:Send"] = "DURATION-SECRET";
        var error = Assert.Throws<ArgumentException>(() => f.Create()); Assert.Equal("Timeouts:Send", error.Message);
        Assert.DoesNotContain("SECRET", error.ToString()); Assert.Equal(0, f.ClientCreations);
    }

    private sealed class FactoryFixture
    {
        public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal) {
            ["ChannelSecret"] = "test-secret", ["ChannelAccessToken"] = "ACCESS-TOKEN-SECRET" };
        public RecordingHandler Handler { get; } = new();
        public HttpClient Http { get; }
        public TimeProvider Clock { get; } = TimeProvider.System;
        public int ClientCreations;
        public FactoryFixture() => Http = new HttpClient(Handler);
        public IConnector Create() => new LineConnectorFactory().Create(new(
            new("line", "line-alt", true, "Saintber.Assistant.Connectors.Line.dll", null, Settings),
            new RecordingLoggerFactory(), Clock, () => { ClientCreations++; return Http; }));
    }
    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        private readonly RecordingLogger _logger = new();
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => _logger;
        public void Dispose() { }
    }
}
