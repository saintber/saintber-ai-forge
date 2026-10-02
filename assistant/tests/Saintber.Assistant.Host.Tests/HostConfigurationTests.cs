using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Saintber.Assistant.Abstractions;
using Saintber.Assistant.Host;
namespace Saintber.Assistant.Host.Tests;

[CollectionDefinition("Configuration environment", DisableParallelization = true)]
public sealed class ConfigurationEnvironmentCollection;
[Collection("Configuration environment")]
public class HostConfigurationTests
{
    private const string Secret = "private-channel-secret";
    private const string Token = "private-channel-token";
    private const string LineDll = "Saintber.Assistant.Connectors.Line.dll";
    private static string Repo
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "assistant", "Assistant.sln"))) directory = directory.Parent;
            return directory!.FullName;
        }
    }
    private static string Published => Path.Combine(Repo, "assistant", "tests", "Saintber.Assistant.Host.Tests", "Fixtures", "line_published");
    private static Dictionary<string, object?> Settings(int concurrency = 4) => new()
    {
        ["ChannelSecret"] = Secret, ["ChannelAccessToken"] = Token,
        ["Work"] = new Dictionary<string, object?> { ["MaxConcurrency"] = concurrency }
    };
    private static Dictionary<string, object?> Entry(Dictionary<string, object?>? settings = null) => new()
    {
        ["Type"] = "line", ["Assembly"] = LineDll, ["Settings"] = settings ?? Settings()
    };
    private static Dictionary<string, object?> Document(params (string Id, Dictionary<string, object?> Entry)[] entries) => new()
    {
        ["Assistant"] = new Dictionary<string, object?> { ["ConnectorsPath"] = Published },
        ["Connectors"] = entries.ToDictionary(item => item.Id, item => (object?)item.Entry)
    };

    [Fact]
    public async Task Common_fields_real_line_defaults_and_summary_never_include_setting_values()
    {
        using var files = new ConfigFiles();
        var entry = Entry(); entry["DisplayName"] = "Main LINE";
        files.Base(Document(("line", entry)));
        using var settings = files.Load();
        var config = Assert.Single(settings.Instances);
        Assert.True(config.Enabled); Assert.Equal("line", config.Type); Assert.Equal("Main LINE", config.DisplayName);
        Assert.Contains(settings.Configuration.Providers, provider => provider.GetType().Name == "JsonConfigurationProvider");
        var logs = new RecordingLogs();
        var connectors = settings.LoadConnectors(logs, TimeProvider.System, () => new HttpClient(new NoNetwork()));
        try
        {
            Assert.Single(connectors);
            Assert.Equal(TimeSpan.FromSeconds(15), connectors[0].StopBudget);
            var factory = new ConnectorLoader(Published, logs, TimeProvider.System, () => new HttpClient()).DiscoverFactory(config);
            var validated = ConnectorSettingsValidator.Validate(config, factory.Settings);
            Assert.Equal("5", validated.Settings["LoadingSeconds"]);
            Assert.Equal("00:00:30", validated.Settings["Timeouts:Event"]);
            Assert.Contains(logs.Messages, message => message.Contains("line") && message.Contains("Main LINE") && message.Contains("/webhook/line") && message.Contains("True"));
            Assert.DoesNotContain(Secret, string.Join(" ", logs.Messages)); Assert.DoesNotContain(Token, string.Join(" ", logs.Messages));
        }
        finally { await Stop(connectors); }
    }

    [Theory]
    [InlineData("Enabeld", "false")]
    [InlineData("Asssembly", "bad-private-value")]
    [InlineData("Enabled", "maybe")]
    [InlineData("Settings", "bad-private-value")]
    public void Misspelled_common_fields_and_scalar_shapes_fail_without_values(string key, string value)
    {
        using var files = new ConfigFiles(); var entry = Entry(); entry[key] = value;
        files.Base(Document(("line", entry)));
        AssertSafeFailure(() => files.Load(), "line", key, value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_null_enabled_is_rejected_and_cannot_reenable_a_disabled_base(bool external)
    {
        using var files = new ConfigFiles(); var entry = Entry(); entry["Enabled"] = external ? false : null;
        files.Base(Document(("line", entry)));
        if (external) { files.External(new { Connectors = new { line = new { Enabled = (string?)null } } }); files.Env("Assistant__ConfigFile", files.ExternalPath); }
        AssertSafeFailure(() => files.Load(), "line", "Enabled");
    }

    [Theory]
    [InlineData("Type")]
    [InlineData("Enabled")]
    [InlineData("Assembly")]
    [InlineData("DisplayName")]
    public void Object_common_fields_fail(string key)
    {
        using var files = new ConfigFiles(); var entry = Entry(); entry[key] = new { secret = Secret };
        files.Base(Document(("line", entry))); AssertSafeFailure(() => files.Load(), "line", key);
    }

    [Theory]
    [InlineData("Line-Main")]
    [InlineData("1line")]
    [InlineData("line__main")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefg")]
    public void Invalid_ids_fail_naming_the_original_id(string id)
    {
        using var files = new ConfigFiles(); files.Base(Document((id, Entry()))); AssertSafeFailure(() => files.Load(), id, "InstanceId");
    }

    [Fact]
    public async Task Ids_and_settings_are_case_insensitive_and_canonicalized()
    {
        using var files = new ConfigFiles();
        var entry = Entry(new() { ["channelsecret"] = Secret, ["channelaccesstoken"] = Token, ["work"] = new { maxconcurrency = 8 } });
        entry["Type"] = "LINE"; files.Base(Document(("LINE_A", entry)));
        using var settings = files.Load(); var config = Assert.Single(settings.Instances);
        Assert.Equal("line_a", config.InstanceId);
        var factory = new ConnectorLoader(Published, new RecordingLogs(), TimeProvider.System, () => new HttpClient()).DiscoverFactory(config);
        var validated = ConnectorSettingsValidator.Validate(config, factory.Settings);
        Assert.Equal("8", validated.Settings["Work:MaxConcurrency"]); Assert.Equal(Secret, validated.Settings["ChannelSecret"]);
        var connectors = settings.LoadConnectors(new RecordingLogs(), TimeProvider.System, () => new HttpClient(new NoNetwork()));
        try { Assert.Equal("line", Assert.Single(connectors).ConnectorType); } finally { await Stop(connectors); }
    }

    [Theory]
    [InlineData("Work:MaxConcurency", "bad-private-value")]
    [InlineData("Work:MaxConcurrency", "abc")]
    [InlineData("LoadingSeconds", "65")]
    [InlineData("LoadingSeconds", "4")]
    [InlineData("Timeouts:Event", "60")]
    [InlineData("Dedup:Ttl", "00:00:00")]
    public void Unknown_kind_range_and_duration_fail_before_factory_without_values(string key, string value)
    {
        using var files = new ConfigFiles(); var input = Settings(); input.Remove("Work"); input[key] = value;
        files.Base(Document(("line", Entry(input)))); using var settings = files.Load();
        AssertSafeFailure(() => settings.LoadConnectors(new RecordingLogs(), TimeProvider.System, () => new HttpClient()), "line", key, value);
    }

    [Fact]
    public void Branch_and_leaf_conflict_fails_with_branch_key()
    {
        using var files = new ConfigFiles(); var input = Settings(); input["Work"] = "bad-private-value"; input["Work:MaxConcurrency"] = "8";
        files.Base(Document(("line", Entry(input)))); AssertSafeFailure(() => files.Load(), "line", "Work", "bad-private-value");
    }

    [Fact]
    public void Seven_passes_host_range_but_fails_real_factory_platform_rule()
    {
        using var files = new ConfigFiles(); var input = Settings(); input["LoadingSeconds"] = "7";
        files.Base(Document(("line", Entry(input)))); using var settings = files.Load(); var config = Assert.Single(settings.Instances);
        var loader = new ConnectorLoader(Published, new RecordingLogs(), TimeProvider.System, () => new HttpClient());
        var factory = loader.DiscoverFactory(config);
        Assert.Equal("7", ConnectorSettingsValidator.Validate(config, factory.Settings).Settings["LoadingSeconds"]);
        AssertSafeFailure(() => settings.LoadConnectors(new RecordingLogs(), TimeProvider.System, () => new HttpClient()), "line", "LoadingSeconds");
    }

    [Theory]
    [InlineData("ChannelSecret")]
    [InlineData("ChannelAccessToken")]
    public void Missing_required_secret_never_prints_another_secret(string missing)
    {
        using var files = new ConfigFiles(); var input = Settings(); input.Remove(missing);
        files.Base(Document(("line", Entry(input)))); using var settings = files.Load(); var logs = new RecordingLogs();
        AssertSafeFailure(() => settings.LoadConnectors(logs, TimeProvider.System, () => new HttpClient()), "line", missing);
        Assert.DoesNotContain(Secret, string.Join(" ", logs.Messages)); Assert.DoesNotContain(Token, string.Join(" ", logs.Messages));
    }

    [Fact]
    public async Task Json_external_environment_precedence_is_eight_then_six_then_four()
    {
        using var files = new ConfigFiles(); files.Base(Document(("line", Entry(Settings(4)))));
        files.External(new { Connectors = new { line = new { Settings = new { Work = new { MaxConcurrency = 6 } } } } });
        files.Env("Assistant__ConfigFile", files.ExternalPath); files.Env("Connectors__line__Settings__Work__MaxConcurrency", "8");
        using (var settings = files.Load()) { Assert.Equal("8", Assert.Single(settings.Instances).Settings["Work:MaxConcurrency"]); await VerifyCreates(settings); }
        files.Env("Connectors__line__Settings__Work__MaxConcurrency", null);
        using (var settings = files.Load()) Assert.Equal("6", Assert.Single(settings.Instances).Settings["Work:MaxConcurrency"]);
        files.Env("Assistant__ConfigFile", null);
        using (var settings = files.Load()) Assert.Equal("4", Assert.Single(settings.Instances).Settings["Work:MaxConcurrency"]);
    }

    [Fact]
    public async Task Merge_by_id_preserves_omitted_instances_and_secrets_follow_id_after_reordering()
    {
        using var files = new ConfigFiles(); files.Base(Document(("line_a", Entry()), ("line_b", Entry())));
        files.External(new { Connectors = new { line_b = new { DisplayName = "B external" }, line_a = new { DisplayName = "A external" } } });
        files.Env("Assistant__ConfigFile", files.ExternalPath); files.Env("Connectors__line_a__Settings__ChannelSecret", "A-environment-secret");
        using (var settings = files.Load())
        {
            Assert.Equal(2, settings.Instances.Count); Assert.Equal("A-environment-secret", settings.Instances.Single(c => c.InstanceId == "line_a").Settings["ChannelSecret"]);
            Assert.Equal(Secret, settings.Instances.Single(c => c.InstanceId == "line_b").Settings["ChannelSecret"]); await VerifyCreates(settings);
        }
        files.External(new { Connectors = new { line_a = new { DisplayName = "A changed" } } });
        using (var settings = files.Load()) { Assert.Equal(2, settings.Instances.Count); Assert.True(settings.Instances.Single(c => c.InstanceId == "line_b").Enabled); }
        files.External(new { Connectors = new { line_b = new { Enabled = false } } });
        using (var settings = files.Load()) { Assert.False(settings.Instances.Single(c => c.InstanceId == "line_b").Enabled); Assert.True(settings.Instances.Single(c => c.InstanceId == "line_a").Enabled); }
    }

    [Fact]
    public async Task Environment_only_configuration_uses_the_real_provider_and_creates_line()
    {
        using var files = new ConfigFiles(); files.Base(new { });
        files.Env("Assistant__ConnectorsPath", Published); files.Env("Connectors__line__Type", "line"); files.Env("Connectors__line__Assembly", LineDll);
        files.Env("Connectors__line__Settings__ChannelSecret", Secret); files.Env("Connectors__line__Settings__ChannelAccessToken", Token);
        using var settings = files.Load(); Assert.Contains(settings.Configuration.Providers, provider => provider.GetType().Name == "EnvironmentVariablesConfigurationProvider"); await VerifyCreates(settings);
    }

    [Fact]
    public void Environment_fragment_without_type_fails_instead_of_silently_ignoring_it()
    {
        using var files = new ConfigFiles(); files.Base(Document(("line", Entry()))); files.Env("Connectors__lne__Settings__ChannelSecret", "bad-private-value");
        AssertSafeFailure(() => files.Load(), "lne", "Type", "bad-private-value");
    }

    [Fact]
    public async Task Disabled_instance_does_not_load_and_logs_only_summary()
    {
        using var files = new ConfigFiles(); var entry = Entry(new()); entry["Enabled"] = false; entry["Assembly"] = "missing.dll"; entry["DisplayName"] = "Disabled LINE";
        files.Base(Document(("line", entry))); using var settings = files.Load(); var logs = new RecordingLogs();
        var connectors = settings.LoadConnectors(logs, TimeProvider.System, () => new HttpClient(new NoNetwork()));
        Assert.Empty(connectors); Assert.Contains(logs.Messages, message => message.Contains("Disabled LINE") && message.Contains("False"));
        await VerifyNoListing(settings);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    [InlineData("5")]
    [InlineData("bad-private-value")]
    public void Invalid_shutdown_margin_names_only_the_key(string value)
    {
        using var files = new ConfigFiles(); files.Base(new { }); files.Env("Assistant__Shutdown__Margin", value);
        AssertSafeFailure(() => files.Load(), "Assistant:Shutdown:Margin", "Assistant:Shutdown:Margin", value);
    }

    [Fact]
    public void Defaults_and_strict_colon_duration_use_invariant_culture()
    {
        using var files = new ConfigFiles(); files.Base(new { }); using var settings = files.Load();
        Assert.Equal(Path.Combine(files.Directory, "connectors"), settings.ConnectorsPath); Assert.Equal(TimeSpan.FromSeconds(5), settings.ShutdownMargin);
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var config = new ConnectorInstanceConfig("test", "sample", true, "test.dll", null, new Dictionary<string, string> { ["Delay"] = "1.02:03:04.1234567", ["Flag"] = "true" });
            var validated = ConnectorSettingsValidator.Validate(config, [new("Delay", SettingKind.Duration), new("Flag", SettingKind.Boolean), new("Count", SettingKind.Integer, DefaultValue: "8", Minimum: "1", Maximum: "8")]);
            Assert.Equal("8", validated.Settings["Count"]); Assert.Equal("true", validated.Settings["Flag"]); Assert.Equal("1.02:03:04.1234567", validated.Settings["Delay"]);
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("directory")]
    [InlineData("malformed")]
    public void Explicit_external_file_failures_are_sanitized(string kind)
    {
        using var files = new ConfigFiles(); files.Base(Document(("line", Entry())));
        var path = files.ExternalPath;
        if (kind == "directory") path = files.Directory;
        else if (kind == "malformed") File.WriteAllText(path, "{ secret: private-channel-secret");
        files.Env("Assistant__ConfigFile", path); AssertSafeFailure(() => files.Load(), "Assistant:ConfigFile", "Assistant:ConfigFile", path);
    }

    [Fact]
    public void Malformed_base_json_is_sanitized()
    {
        using var files = new ConfigFiles(); File.WriteAllText(Path.Combine(files.Directory, "appsettings.json"), "{ secret: private-channel-secret");
        AssertSafeFailure(() => files.Load(), "appsettings.json", "appsettings.json");
    }

    [Fact]
    public async Task Shipped_settings_keep_line_disabled_and_health_available_without_secrets()
    {
        using var files = new ConfigFiles();
        using var settings = AssistantConfiguration.Load(Path.Combine(Repo, "assistant", "src", "Saintber.Assistant.Host"), files.Prefix);
        var line = Assert.Single(settings.Instances); Assert.Equal("line", line.Type); Assert.False(line.Enabled); Assert.Equal("line/" + LineDll, line.Assembly);
        Assert.Empty(settings.LoadConnectors(new RecordingLogs(), TimeProvider.System, () => new HttpClient(new NoNetwork()))); await VerifyNoListing(settings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nested_json_and_environment_really_run_eight_workers_with_sixty_second_budgets(bool environment)
    {
        using var files = new ConfigFiles(); var input = Settings(environment ? 4 : 8);
        input["Timeouts"] = new { Event = environment ? "00:00:30" : "00:01:00" };
        files.Base(Document(("line", Entry(input))));
        if (environment)
        {
            files.Env("Connectors__line__Settings__Work__MaxConcurrency", "8");
            files.Env("Connectors__line__Settings__Timeouts__Event", "00:01:00");
        }
        using var settings = files.Load(); var clock = new ControlledClock();
        var connectors = settings.LoadConnectors(new RecordingLogs(), clock, () => new HttpClient(new NoNetwork()));
        var entered = Enumerable.Range(0, 9).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var release = Enumerable.Range(0, 9).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var tokens = new ConcurrentDictionary<int, CancellationToken>(); var active = 0;
        var handler = new WorkerHandler(async (envelope, cancellationToken) =>
        {
            var index = int.Parse(envelope.Content.Text, CultureInfo.InvariantCulture);
            Interlocked.Increment(ref active);
            tokens[index] = cancellationToken;
            entered[index].TrySetResult();
            try { await release[index].Task.WaitAsync(cancellationToken); }
            finally { Interlocked.Decrement(ref active); }
        });
        try
        {
            var connector = Assert.Single(connectors); await connector.StartAsync(handler, default);
            var receiver = Assert.IsAssignableFrom<IWebhookReceiver>(connector);
            for (var index = 0; index < 8; index++) Assert.Equal(200, (await receiver.ReceiveAsync(WorkerRequest(index), default)).StatusCode);
            await Task.WhenAll(entered.Take(8).Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(8, Volatile.Read(ref active));
            Assert.Equal(200, (await receiver.ReceiveAsync(WorkerRequest(8), default)).StatusCode); Assert.False(entered[8].Task.IsCompleted);
            release[0].TrySetResult(); await entered[8].Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(8, Volatile.Read(ref active));
            clock.Advance(TimeSpan.FromSeconds(59)); Assert.All(Enumerable.Range(1, 8), index => Assert.False(tokens[index].IsCancellationRequested));
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.All(Enumerable.Range(1, 8), index => Assert.True(tokens[index].IsCancellationRequested));
        }
        finally { foreach (var signal in release) signal.TrySetResult(); await Stop(connectors); }
    }

    [Theory]
    [InlineData(SettingKind.Boolean, "maybe")]
    [InlineData(SettingKind.Integer, "8.2")]
    [InlineData(SettingKind.Duration, "00:00:60")]
    [InlineData(SettingKind.String, "")]
    public void Descriptor_kind_and_required_checks_are_generic(SettingKind kind, string value)
    {
        var config = new ConnectorInstanceConfig("fixture", "line", true, "fixture.dll", null, new Dictionary<string, string> { ["TestKey"] = value });
        AssertSafeFailure(() => ConnectorSettingsValidator.Validate(config, [new("TestKey", kind, Required: true)]), "line", "TestKey", value.Length == 0 ? null : value);
    }

    [Theory]
    [InlineData("{\"Connectors\":\"private-channel-secret\"}")]
    [InlineData("{\"Connectors\":[]}")]
    [InlineData("{\"Connectors\":{\"line\":\"private-channel-secret\"}}")]
    [InlineData("{\"Connectors\":{\"line\":{\"Type\":\"line\",\"Assembly\":\"missing.dll\",\"Settings\":[]}}}")]
    public void Array_and_scalar_objects_are_rejected(string json)
    {
        using var files = new ConfigFiles(); File.WriteAllText(Path.Combine(files.Directory, "appsettings.json"), json);
        var error = Assert.Throws<ConnectorStartupException>(() => files.Load()); Assert.DoesNotContain(Secret, error.ToString());
    }

    private static WebhookRequest WorkerRequest(int index)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { events = new[] { new { type = "message", mode = "active", webhookEventId = "event-" + index,
            timestamp = 0L, source = new { type = "group", userId = "U1", groupId = "G1" }, message = new { type = "text", id = "message-" + index, text = index.ToString(CultureInfo.InvariantCulture) } } } });
        return new(new Dictionary<string, string> { ["X-Line-Signature"] = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body)) }, body);
    }
    private sealed class WorkerHandler(Func<InboundEnvelope, CancellationToken, Task> handle) : IInboundMessageHandler
    {
        public Task HandleAsync(InboundEnvelope envelope, CancellationToken cancellationToken) => handle(envelope, cancellationToken);
    }
    private sealed class ControlledClock : TimeProvider
    {
        private readonly object gate = new(); private DateTimeOffset now = DateTimeOffset.UnixEpoch; private readonly List<Timer> timers = [];
        public override DateTimeOffset GetUtcNow() { lock (gate) return now; }
        public override long GetTimestamp() { lock (gate) return now.UtcTicks; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (gate) { var timer = new Timer(this, callback, state, dueTime, period); timers.Add(timer); return timer; }
        }
        public void Advance(TimeSpan duration)
        {
            Timer[] due;
            lock (gate)
            {
                now += duration; due = timers.Where(timer => !timer.Disposed && timer.Due <= now).ToArray();
                foreach (var timer in due) timer.Due = timer.Period < TimeSpan.Zero ? DateTimeOffset.MaxValue : now + timer.Period;
            }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        private sealed class Timer : ITimer
        {
            private readonly ControlledClock clock;
            public TimerCallback Callback { get; } public object? State { get; } public DateTimeOffset Due; public TimeSpan Period; public bool Disposed;
            public Timer(ControlledClock clock, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            { this.clock = clock; Callback = callback; State = state; Due = dueTime < TimeSpan.Zero ? DateTimeOffset.MaxValue : clock.now + dueTime; Period = period; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { lock (clock.gate) { if (Disposed) return false; Due = dueTime < TimeSpan.Zero ? DateTimeOffset.MaxValue : clock.now + dueTime; Period = period; return true; } }
            public void Dispose() { lock (clock.gate) Disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static void AssertSafeFailure(Action action, string instance, string key, string? rejected = null)
    {
        var error = Assert.Throws<ConnectorStartupException>(action);
        Assert.Contains(instance, error.Message); Assert.Contains(key, error.Message);
        Assert.DoesNotContain(Secret, error.ToString()); Assert.DoesNotContain(Token, error.ToString());
        if (rejected is not null) Assert.DoesNotContain(rejected, error.Message);
    }
    private static async Task VerifyCreates(AssistantConfiguration settings)
    {
        var connectors = settings.LoadConnectors(new RecordingLogs(), TimeProvider.System, () => new HttpClient(new NoNetwork()));
        try { Assert.Equal(settings.Instances.Count(c => c.Enabled), connectors.Count); } finally { await Stop(connectors); }
    }
    private static async Task Stop(IEnumerable<IConnector> connectors)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await Task.WhenAll(connectors.Select(c => c.StopAsync(cancellation.Token)));
    }
    private static async Task VerifyNoListing(AssistantConfiguration settings)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Configuration.Sources.Clear(); builder.Configuration.AddConfiguration(settings.Configuration);
        await using var app = builder.Build(); app.MapAssistantHealth(); await app.StartAsync();
        using var http = new HttpClient(); var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Assert.Equal("healthy", await http.GetStringAsync(address + "/healthz"));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(address + "/connectors")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(address + "/settings")).StatusCode); await app.StopAsync();
    }
    private sealed class ConfigFiles : IDisposable
    {
        public string Directory { get; } = Path.Combine(Repo, "assistant", "tests", "Saintber.Assistant.Host.Tests", "Fixtures", "config_scratch", Guid.NewGuid().ToString("N"));
        public string Prefix { get; } = "ASSISTANT_CONFIG_" + Guid.NewGuid().ToString("N") + "_";
        public string ExternalPath => Path.Combine(Directory, "external.json");
        private readonly Dictionary<string, string?> previous = new();
        public ConfigFiles() => System.IO.Directory.CreateDirectory(Directory);
        public void Base(object document) => File.WriteAllText(Path.Combine(Directory, "appsettings.json"), JsonSerializer.Serialize(document));
        public void External(object document) => File.WriteAllText(ExternalPath, JsonSerializer.Serialize(document));
        public void Env(string key, string? value) { var name = Prefix + key; if (!previous.ContainsKey(name)) previous.Add(name, Environment.GetEnvironmentVariable(name)); Environment.SetEnvironmentVariable(name, value); }
        public AssistantConfiguration Load() => AssistantConfiguration.Load(Directory, Prefix);
        public void Dispose() { foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value); System.IO.Directory.Delete(Directory, true); }
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("Test must not contact LINE.");
    }
    private sealed class RecordingLogs : ILoggerFactory
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string name) => new Logger(Messages);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
        }
    }
}
