using System.Reflection;
namespace Saintber.Assistant.Abstractions.Tests;
public class AssemblyBoundaryTests
{
    [Fact] public void Component_references_only_allowed_assistant_assemblies()
    {
        var assembly = Assembly.Load("Saintber.Assistant.Abstractions");
        string[] allowed = [];
        Assert.All(assembly.GetReferencedAssemblies().Where(a => a.Name!.StartsWith("Saintber.Assistant.")), a => Assert.Contains(a.Name!, allowed));
    }
}
