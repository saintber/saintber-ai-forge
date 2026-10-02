using System.Diagnostics;
using System.Text.Json;

namespace Saintber.Assistant.Host.Tests;

/// <summary>直接驗證部署入口的退出碼與安全輸出，不依賴 WebApplicationFactory 的失敗清理。</summary>
public sealed class HostStartupProcessTests
{
    private const string Secret = "FAKE-SECRET-STARTUP-945";
    private const string Token = "FAKE-TOKEN-STARTUP-946";
    private const string RejectedValue = "FAKE-INVALID-SETTING-947";

    [Fact]
    public async Task Explicit_missing_config_file_exits_nonzero_and_names_only_the_key()
    {
        using var files = new StartupFiles();
        files.Write(enabled: false);
        var missing = Path.Combine(files.Directory, "FAKE-NONEXISTENT-CONFIG-948.json");
        var result = await RunHost(files.Directory, new Dictionary<string, string>
        {
            ["Assistant__ConfigFile"] = missing
        });
        AssertRejected(result, "Assistant:ConfigFile");
        Assert.DoesNotContain(missing, result.Output);
        Assert.DoesNotContain("FAKE-NONEXISTENT-CONFIG-948", result.Output);
    }

    [Fact]
    public async Task Invalid_echo_mode_exits_nonzero_without_printing_the_mode_value()
    {
        using var files = new StartupFiles();
        files.Write(enabled: false, echoMode: RejectedValue);
        var result = await RunHost(files.Directory);
        AssertRejected(result, "Assistant:Echo:Mode");
    }

    [Fact]
    public async Task Missing_access_token_exits_nonzero_without_printing_the_present_secret()
    {
        using var files = new StartupFiles();
        files.Write(enabled: true, includeAccessToken: false);
        var result = await RunHost(files.Directory);
        AssertRejected(result, "ChannelAccessToken");
        Assert.Contains("line", result.Output);
    }

    [Fact]
    public async Task Unknown_setting_exits_nonzero_without_printing_any_setting_values()
    {
        using var files = new StartupFiles();
        files.Write(enabled: true, unknownSetting: true);
        var result = await RunHost(files.Directory);
        AssertRejected(result, "Work:MaxConcurency");
        Assert.Contains("line", result.Output);
    }

    private static void AssertRejected(ProcessResult result, string key)
    {
        Assert.True(result.Exited, "Host did not reject invalid configuration within 15 seconds; child process was cleaned up.");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(key, result.Output);
        Assert.DoesNotContain(Secret, result.Output);
        Assert.DoesNotContain(Token, result.Output);
        Assert.DoesNotContain(RejectedValue, result.Output);
    }

    private static async Task<ProcessResult> RunHost(string contentRoot, IReadOnlyDictionary<string, string>? environment = null)
    {
        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = outputDirectory.Parent!.Name;
        var framework = outputDirectory.Name;
        var hostDirectory = Path.Combine(Repo, "assistant", "src", "Saintber.Assistant.Host", "bin", configuration, framework);
        var host = Path.Combine(hostDirectory, "Saintber.Assistant.Host.dll");
        Assert.True(File.Exists(host), "Host build output missing; build Host.Tests before running process tests.");
        Assert.True(File.Exists(Path.ChangeExtension(host, ".runtimeconfig.json")), "Host runtimeconfig missing.");
        Assert.True(File.Exists(Path.Combine(PublishedLine, "Saintber.Assistant.Connectors.Line.dll")), "Clean LINE publish fixture missing.");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = contentRoot
        };
        start.ArgumentList.Add(host);
        start.ArgumentList.Add("--contentRoot");
        start.ArgumentList.Add(contentRoot);
        // Preserve PATH/DOTNET runtime resolution, while removing inherited application configuration.
        foreach (var key in start.Environment.Keys.Where(key =>
                     key.Equals("Connectors", StringComparison.OrdinalIgnoreCase)
                     || key.StartsWith("Connectors__", StringComparison.OrdinalIgnoreCase)
                     || key.StartsWith("Connectors:", StringComparison.OrdinalIgnoreCase)
                     || key.Equals("Assistant", StringComparison.OrdinalIgnoreCase)
                     || key.StartsWith("Assistant__", StringComparison.OrdinalIgnoreCase)
                     || key.StartsWith("Assistant:", StringComparison.OrdinalIgnoreCase)
                     || key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
                     || key.Equals("DOTNET_CONTENTROOT", StringComparison.OrdinalIgnoreCase)
                     || key.Equals("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_CONTENTROOT"] = contentRoot;
        start.Environment["DOTNET_CONTENTROOT"] = contentRoot;
        if (environment is not null)
            foreach (var entry in environment) start.Environment[entry.Key] = entry.Value;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var exited = false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(deadline.Token);
            exited = true;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        return new(exited, process.ExitCode, await stdout + Environment.NewLine + await stderr);
    }

    private static string Repo
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "assistant", "Assistant.sln")))
                directory = directory.Parent;
            return directory!.FullName;
        }
    }
    private static string PublishedLine => Path.Combine(Repo, "assistant", "tests", "Saintber.Assistant.Host.Tests", "Fixtures", "line_published");
    private sealed record ProcessResult(bool Exited, int ExitCode, string Output);

    private sealed class StartupFiles : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "assistant-host-startup-" + Guid.NewGuid().ToString("N"));
        public StartupFiles() => System.IO.Directory.CreateDirectory(Directory);
        public void Write(bool enabled, string echoMode = "reply", bool includeAccessToken = true, bool unknownSetting = false)
        {
            var settings = new Dictionary<string, object?> { ["ChannelSecret"] = Secret };
            if (includeAccessToken) settings["ChannelAccessToken"] = Token;
            if (unknownSetting) settings["Work:MaxConcurency"] = RejectedValue;
            File.WriteAllText(Path.Combine(Directory, "appsettings.json"), JsonSerializer.Serialize(new
            {
                Assistant = new { ConnectorsPath = PublishedLine, Echo = new { Mode = echoMode } },
                Connectors = new
                {
                    line = new
                    {
                        Type = "line", Enabled = enabled, Assembly = "Saintber.Assistant.Connectors.Line.dll",
                        Settings = settings
                    }
                }
            }));
        }
        public void Dispose()
        {
            var path = Path.GetFullPath(Directory);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(temporaryRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Startup fixture directory must stay inside the temporary root.");
            if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, recursive: true);
        }
    }
}
