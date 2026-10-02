using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Saintber.Assistant.Abstractions;
namespace Saintber.Assistant.Host;

/// <summary>固定的三層啟動設定快照；外部檔案與設定錯誤只回傳定位鍵名。</summary>
public sealed class AssistantConfiguration : IDisposable
{
    private static readonly HashSet<string> CommonFields = new(["Type", "Enabled", "Assembly", "DisplayName", "Settings"], StringComparer.OrdinalIgnoreCase);
    private static readonly Regex ValidId = new("^[a-z](?:[a-z0-9]|_(?!_))*$", RegexOptions.CultureInvariant);
    public IConfigurationRoot Configuration { get; }
    public IReadOnlyList<ConnectorInstanceConfig> Instances { get; }
    public string ConnectorsPath { get; }
    public TimeSpan ShutdownMargin { get; }

    private AssistantConfiguration(IConfigurationRoot configuration, string contentRoot)
    {
        Configuration = configuration;
        Instances = ReadInstances(configuration);
        var path = Scalar(configuration.GetSection("Assistant:ConnectorsPath"), "", "Assistant:ConnectorsPath") ?? "connectors";
        try { ConnectorsPath = Path.GetFullPath(path, Path.GetFullPath(contentRoot)); }
        catch (Exception) { throw Error("", "Assistant:ConnectorsPath"); }
        var margin = Scalar(configuration.GetSection("Assistant:Shutdown:Margin"), "", "Assistant:Shutdown:Margin") ?? "00:00:05";
        if (!ConnectorSettingsValidator.TryDuration(margin, out var parsed) || parsed <= TimeSpan.Zero)
            throw Error("", "Assistant:Shutdown:Margin");
        ShutdownMargin = parsed;
    }

    public static AssistantConfiguration Load(string contentRoot, string? environmentPrefix = null)
    {
        var root = Path.GetFullPath(contentRoot);
        var basePath = Path.Combine(root, "appsettings.json");
        CheckJsonShape(basePath, "appsettings.json");
        // Bootstrap selects the external file from base + environment, before adding that file.
        var bootstrapBuilder = new ConfigurationBuilder().AddJsonFile(basePath, optional: false, reloadOnChange: false);
        AddEnvironment(bootstrapBuilder, environmentPrefix);
        IConfigurationRoot bootstrap;
        try { bootstrap = bootstrapBuilder.Build(); }
        catch (Exception) { throw Error("", "appsettings.json"); }
        string? external;
        try { external = Scalar(bootstrap.GetSection("Assistant:ConfigFile"), "", "Assistant:ConfigFile"); }
        finally { (bootstrap as IDisposable)?.Dispose(); }
        var builder = new ConfigurationBuilder().AddJsonFile(basePath, optional: false, reloadOnChange: false);
        if (external is not null)
        {
            string externalPath;
            try
            {
                if (string.IsNullOrWhiteSpace(external)) throw Error("", "Assistant:ConfigFile");
                externalPath = Path.GetFullPath(external, root);
            }
            catch (Exception) { throw Error("", "Assistant:ConfigFile"); }
            CheckJsonShape(externalPath, "Assistant:ConfigFile");
            builder.AddJsonFile(externalPath, optional: false, reloadOnChange: false);
        }
        AddEnvironment(builder, environmentPrefix);
        IConfigurationRoot configuration;
        try { configuration = builder.Build(); }
        catch (Exception) { throw Error("", external is null ? "appsettings.json" : "Assistant:ConfigFile"); }
        try { return new(configuration, root); }
        catch
        {
            (configuration as IDisposable)?.Dispose();
            throw;
        }
    }

    public IReadOnlyList<IConnector> LoadConnectors(ILoggerFactory logs, TimeProvider clock, Func<HttpClient> createClient)
    {
        var loader = new ConnectorLoader(ConnectorsPath, logs, clock, createClient);
        var connectors = loader.Load(Instances, (config, factory) =>
            ConnectorSettingsValidator.Validate(config with { Type = factory.ConnectorType }, factory.Settings));
        var logger = logs.CreateLogger("Saintber.Assistant.Host.Configuration");
        foreach (var instance in Instances)
        {
            var webhook = connectors.Any(connector => connector.InstanceId == instance.InstanceId && connector is IWebhookReceiver)
                ? "/webhook/" + instance.InstanceId : null;
            logger.LogInformation("Connector {ConnectorType} {InstanceId} enabled {Enabled} display {DisplayName} webhook {WebhookPath}",
                instance.Type, instance.InstanceId, instance.Enabled, instance.DisplayName, webhook);
        }
        return connectors;
    }

    private static void AddEnvironment(IConfigurationBuilder builder, string? prefix)
    {
        if (prefix is null) builder.AddEnvironmentVariables();
        else builder.AddEnvironmentVariables(prefix);
    }

    private static IReadOnlyList<ConnectorInstanceConfig> ReadInstances(IConfiguration configuration)
    {
        var section = configuration.GetSection("Connectors");
        if (section.Value is not null) throw Error("", "Connectors");
        var instances = new List<ConnectorInstanceConfig>();
        foreach (var instance in section.GetChildren())
        {
            var id = instance.Key.ToLowerInvariant();
            if (id.Length > 32 || !ValidId.IsMatch(id)) throw Error(instance.Key, "InstanceId");
            if (instance.Value is not null) throw Error(id, "Connectors");
            foreach (var field in instance.GetChildren())
                if (!CommonFields.Contains(field.Key)) throw Error(id, field.Key);
            var type = RequiredScalar(instance.GetSection("Type"), id, "Type");
            var assembly = RequiredScalar(instance.GetSection("Assembly"), id, "Assembly");
            var enabledValue = Scalar(instance.GetSection("Enabled"), id, "Enabled");
            var enabled = true;
            if (enabledValue is not null && !bool.TryParse(enabledValue, out enabled)) throw Error(id, "Enabled");
            var displayName = Scalar(instance.GetSection("DisplayName"), id, "DisplayName");
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Flatten(instance.GetSection("Settings"), "", id, settings);
            instances.Add(new(type, id, enabled, assembly, displayName, new ReadOnlyDictionary<string, string>(settings)));
        }
        return instances.AsReadOnly();
    }

    private static void Flatten(IConfigurationSection section, string relative, string id, Dictionary<string, string> result)
    {
        var children = section.GetChildren().ToArray();
        if (relative.Length == 0 && section.Value is not null) throw Error(id, "Settings");
        if (section.Value is not null && children.Length != 0) throw Error(id, relative);
        if (children.Length == 0)
        {
            if (relative.Length != 0) result.Add(relative, section.Value ?? "");
            return;
        }
        foreach (var child in children)
            Flatten(child, relative.Length == 0 ? child.Key : relative + ":" + child.Key, id, result);
    }

    private static string RequiredScalar(IConfigurationSection section, string id, string key)
    {
        var value = Scalar(section, id, key);
        return string.IsNullOrWhiteSpace(value) ? throw Error(id, key) : value;
    }
    private static string? Scalar(IConfigurationSection section, string id, string key)
    {
        if (section.GetChildren().Any()) throw Error(id, key);
        return section.Value;
    }

    // Providers flatten JSON; retain shape checks for empty arrays/objects before that information is lost.
    private static void CheckJsonShape(string path, string fileKey)
    {
        JsonDocument document;
        try
        {
            using var stream = File.OpenRead(path);
            document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (Exception) { throw Error("", fileKey); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Error("", fileKey);
            foreach (var property in document.RootElement.EnumerateObject().Where(property => property.Name.Equals("Connectors", StringComparison.OrdinalIgnoreCase)))
            {
                if (property.Value.ValueKind != JsonValueKind.Object) throw Error("", "Connectors");
                foreach (var instance in property.Value.EnumerateObject())
                {
                    if (instance.Value.ValueKind != JsonValueKind.Object) throw Error(instance.Name, "Connectors");
                    foreach (var field in instance.Value.EnumerateObject())
                    {
                        if (!CommonFields.Contains(field.Name)) throw Error(instance.Name, field.Name);
                        if (field.Name.Equals("Settings", StringComparison.OrdinalIgnoreCase))
                        {
                            if (field.Value.ValueKind != JsonValueKind.Object) throw Error(instance.Name, "Settings");
                            CheckSettingArrays(field.Value, instance.Name, "");
                        }
                        else if (field.Name.Equals("Enabled", StringComparison.OrdinalIgnoreCase) && field.Value.ValueKind == JsonValueKind.Null)
                            throw Error(instance.Name, field.Name);
                        else if (field.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                            throw Error(instance.Name, field.Name);
                    }
                }
            }
        }
    }
    private static void CheckSettingArrays(JsonElement element, string id, string parent)
    {
        foreach (var property in element.EnumerateObject())
        {
            var key = parent.Length == 0 ? property.Name : parent + ":" + property.Name;
            if (property.Value.ValueKind == JsonValueKind.Array) throw Error(id, key);
            if (property.Value.ValueKind == JsonValueKind.Object) CheckSettingArrays(property.Value, id, key);
        }
    }

    private static ConnectorStartupException Error(string id, string key) => new(id.Length == 0 ? key : id + " " + key);
    public void Dispose() => (Configuration as IDisposable)?.Dispose();
}
