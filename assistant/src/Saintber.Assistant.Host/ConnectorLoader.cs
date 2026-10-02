using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Saintber.Assistant.Abstractions;
namespace Saintber.Assistant.Host;
/// <summary>只攜帶允許公開的定位資訊，不保留原始例外或設定值。</summary>
public sealed class ConnectorStartupException(string message) : Exception(message);
/// <summary>同一實體 DLL 路徑共用 factory 與載入環境。Connector 必須將實例狀態放在實例欄位。</summary>
public sealed class ConnectorLoader(string connectorsPath, ILoggerFactory loggerFactory, TimeProvider timeProvider, Func<HttpClient> createHttpClient)
{
    private readonly Dictionary<string, IReadOnlyDictionary<string, IConnectorFactory>> factories = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly Regex ValidId = new("^[a-z](?:[a-z0-9]|_(?!_))*$", RegexOptions.CultureInvariant);
    public IReadOnlyList<IConnector> Load(IEnumerable<ConnectorInstanceConfig> configurations,
        Func<ConnectorInstanceConfig, IConnectorFactory, ConnectorInstanceConfig>? validate = null)
    {
        var configs = configurations.ToArray();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var config in configs)
        {
            if (config.InstanceId.Length > 32 || !ValidId.IsMatch(config.InstanceId) || !ids.Add(config.InstanceId))
                throw Error(config, "InstanceId");
        }
        var result = new List<IConnector>();
        foreach (var config in configs.Where(c => c.Enabled))
        {
            var factory = DiscoverFactory(config);
            var validated = validate is null ? config : validate(config, factory);
            try
            {
                var connector = factory.Create(new(validated, loggerFactory, timeProvider, createHttpClient));
                if (connector is null || connector.InstanceId != validated.InstanceId || connector.ConnectorType != validated.Type)
                    throw Error(config, "InstanceId Type");
                result.Add(connector);
            }
            catch (Exception)
            {
                // Factory 例外可能含設定值；僅由可信描述輸出鍵名。
                throw Error(config, string.Join(" ", factory.Settings.Select(d => d.Key)));
            }
        }
        return result;
    }
    public IConnectorFactory DiscoverFactory(ConnectorInstanceConfig config)
    {
        try
        {
            var root = ResolvePath(connectorsPath);
            var path = ResolvePath(Path.Combine(root, config.Assembly));
            var relative = Path.GetRelativePath(root, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
                throw Error(config, "Assembly");
            if (!factories.TryGetValue(path, out var available))
            {
                CheckContractVersion(path, config);
                var context = new ConnectorLoadContext(path);
                var assembly = context.LoadFromAssemblyPath(path);
                var discovered = new Dictionary<string, IConnectorFactory>(StringComparer.OrdinalIgnoreCase);
                foreach (var type in assembly.GetTypes().Where(t => !t.IsAbstract && !t.IsInterface && typeof(IConnectorFactory).IsAssignableFrom(t)))
                {
                    var factory = (IConnectorFactory)Activator.CreateInstance(type)!;
                    if (!discovered.TryAdd(factory.ConnectorType, factory)) throw Error(config, "Type");
                }
                available = discovered;
                factories.Add(path, available);
            }
            return available.TryGetValue(config.Type, out var matched) ? matched : throw Error(config, "Type");
        }
        catch (ConnectorStartupException) { throw; }
        catch (Exception) { throw Error(config, "Assembly Type"); }
    }
    private static void CheckContractVersion(string path, ConnectorInstanceConfig config)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var host = typeof(IConnector).Assembly.GetName();
        foreach (var handle in metadata.AssemblyReferences)
        {
            var reference = metadata.GetAssemblyReference(handle);
            if (metadata.GetString(reference.Name) == host.Name && reference.Version.Major != host.Version!.Major)
                throw Error(config, $"{reference.Version} {host.Version}");
        }
    }
    // Resolve every component, including parents. ResolveLinkTarget also handles Windows junctions.
    private static string ResolvePath(string value)
    {
        var full = Path.GetFullPath(value);
        var current = Path.GetPathRoot(full)!;
        foreach (var component in full[current.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (component.Length == 0) continue;
            current = Path.Combine(current, component);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not null)
                current = ResolvePath(entry.ResolveLinkTarget(true)!.FullName);
        }
        return Path.GetFullPath(current);
    }
    private static ConnectorStartupException Error(ConnectorInstanceConfig config, string keys) => new($"{Path.GetFileName(config.Assembly)} {config.InstanceId} {keys}");
    private sealed class ConnectorLoadContext(string path) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver resolver = new(path);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IConnector).Assembly.GetName().Name) return typeof(IConnector).Assembly;
            if (name.Name == typeof(ILogger).Assembly.GetName().Name) return typeof(ILogger).Assembly;
            var dependency = resolver.ResolveAssemblyToPath(name);
            return dependency is null ? null : LoadFromAssemblyPath(dependency);
        }
        protected override IntPtr LoadUnmanagedDll(string name)
        {
            var dependency = resolver.ResolveUnmanagedDllToPath(name);
            return dependency is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(dependency);
        }
    }
}
