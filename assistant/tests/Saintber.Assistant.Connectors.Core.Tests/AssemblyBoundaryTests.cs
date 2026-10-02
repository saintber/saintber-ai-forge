using System.Reflection;
namespace Saintber.Assistant.Connectors.Core.Tests;
public class AssemblyBoundaryTests
{
    [Fact] public void Component_references_only_allowed_assistant_assemblies()
    {
        var assembly = Assembly.Load("Saintber.Assistant.Connectors.Core");
        string[] allowed = ["Saintber.Assistant.Abstractions"];
        Assert.All(assembly.GetReferencedAssemblies().Where(a => a.Name!.StartsWith("Saintber.Assistant.")), a => Assert.Contains(a.Name!, allowed));
    }
}
