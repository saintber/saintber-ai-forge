using System.Xml.Linq;
using System.Reflection;
using System.Diagnostics;

namespace Saintber.Assistant.Host.Tests;

public class ArchitectureTests
{
    internal static string AssistantRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "openspec"))) dir = dir.Parent;
            return Path.Combine(dir!.FullName, "assistant");
        }
    }
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["Abstractions"] = [], ["Connectors.Core"] = ["Abstractions"],
        ["Connectors.Line"] = ["Abstractions", "Connectors.Core"], ["Host"] = ["Abstractions"]
    };
    private static string Name(string suffix) => "Saintber.Assistant." + suffix;
    private static string Project(string suffix) => Path.Combine(AssistantRoot, "src", Name(suffix), Name(suffix) + ".csproj");
    private static string[] Violations(string suffix, string path)
    {
        var xml = XDocument.Load(path);
        var allowed = Allowed[suffix].Select(Name).ToHashSet();
        return xml.Descendants("ProjectReference").Select(x => Path.GetFileNameWithoutExtension((string)x.Attribute("Include")!))
            .Where(x => !allowed.Contains(x)).Concat(suffix == "Abstractions"
                ? xml.Descendants("PackageReference").Select(x => (string)x.Attribute("Include")!).Where(x => x != "Microsoft.Extensions.Logging.Abstractions")
                : []).ToArray();
    }
    [Theory]
    [InlineData("Abstractions")][InlineData("Connectors.Core")][InlineData("Connectors.Line")][InlineData("Host")]
    public void Project_dependencies_follow_contract(string suffix)
    {
        Assert.True(File.Exists(Project(suffix)), "Required source project missing: " + suffix);
        Assert.Empty(Violations(suffix, Project(suffix)));
    }
    [Theory]
    [InlineData("Abstractions")][InlineData("Connectors.Core")][InlineData("Connectors.Line")][InlineData("Host")]
    public void Compiled_dependencies_follow_contract(string suffix)
    {
        var files = Directory.Exists(Path.GetDirectoryName(Project(suffix)))
            ? Directory.GetFiles(Path.GetDirectoryName(Project(suffix))!, Name(suffix) + ".dll", SearchOption.AllDirectories).Where(p => p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)).ToArray() : [];
        Assert.NotEmpty(files);
        var assembly = Assembly.LoadFrom(files[0]);
        var allowed = Allowed[suffix].Select(Name).ToHashSet();
        Assert.All(assembly.GetReferencedAssemblies().Where(x => x.Name!.StartsWith("Saintber.Assistant.")), x => Assert.Contains(x.Name!, allowed));
    }
    [Fact]
    public async Task Compilable_fixtures_detect_forbidden_project_and_package()
    {
        var temp = Path.Combine(Path.GetTempPath(), "assistant-architecture-" + Guid.NewGuid());
        Directory.CreateDirectory(temp);
        File.Copy(Path.Combine(AssistantRoot, "Directory.Build.props"), Path.Combine(temp, "Directory.Build.props"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(temp, "Independent.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup></Project>""");
            var line = XDocument.Load(Project("Connectors.Line"));
            foreach (var reference in line.Descendants("ProjectReference")) reference.SetAttributeValue("Include", Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Project("Connectors.Line"))!, (string)reference.Attribute("Include")!)));
            line.Root!.Add(new XElement("ItemGroup", new XElement("ProjectReference", new XAttribute("Include", "Independent.csproj"))));
            line.Root.Add(new XElement("PropertyGroup", new XElement("EnableDefaultCompileItems", "false")));
            var linePath = Path.Combine(temp, "LineFixture.csproj");
            line.Save(linePath);
            Assert.Contains("Independent", Violations("Connectors.Line", linePath));
            var abstraction = XDocument.Load(Project("Abstractions"));
            abstraction.Root!.Add(new XElement("ItemGroup", new XElement("PackageReference", new XAttribute("Include", "xunit"), new XAttribute("Version", "2.9.3"))));
            var absPath = Path.Combine(temp, "AbstractionsFixture.csproj");
            abstraction.Save(absPath);
            Assert.Contains("xunit", Violations("Abstractions", absPath));
            foreach (var project in new[] { linePath, absPath })
            {
                using var proc = Process.Start(new ProcessStartInfo("dotnet") { ArgumentList = { "build", project, "--nologo", "-v:q" }, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
                var output = proc.StandardOutput.ReadToEndAsync(); var error = proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();
                Assert.True(proc.ExitCode == 0, await output + await error);
            }
        }
        finally { Directory.Delete(temp, true); }
    }
}
