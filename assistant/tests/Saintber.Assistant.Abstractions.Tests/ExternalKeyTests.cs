using System.Reflection;
using System.Text.Json;
namespace Saintber.Assistant.Abstractions.Tests;
public class ExternalKeyTests
{
    private static Type KeyType => Assembly.Load("Saintber.Assistant.Abstractions").GetType("Saintber.Assistant.Abstractions.ExternalKey", true)!;
    private static object Create(string canonical, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Activator.CreateInstance(KeyType, "user", canonical, doc.RootElement)!;
    }
    [Fact] public void Equality_and_hash_use_canonical_not_property_order()
    {
        var a = Create("line:user:U123", "{\"userId\":\"U123\",\"other\":1}");
        var b = Create("line:user:U123", "{\"other\":1,\"userId\":\"U123\"}");
        Assert.Equal(a, b); Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, Create("line:user:U999", "{\"userId\":\"U999\"}"));
    }
    [Fact] public void Properties_survive_parser_disposal()
    {
        var a = Create("line:user:U123", "{\"userId\":\"U123\"}");
        Assert.Equal("line:user:U123", KeyType.GetProperty("CanonicalValue")!.GetValue(a));
        var properties = (JsonElement)KeyType.GetProperty("Properties")!.GetValue(a)!;
        Assert.Equal("U123", properties.GetProperty("userId").GetString());
    }
    [Fact] public void Envelope_contract_has_no_transport_properties()
    {
        var type = KeyType.Assembly.GetType("Saintber.Assistant.Abstractions.InboundEnvelope", true)!;
        Assert.Equal(new[] {"Actor", "Chat", "ConnectorInstanceId", "ConnectorType", "Content", "Message", "OccurredAt", "RawMetadata", "Thread"}, type.GetProperties().Select(p => p.Name).Order().ToArray());
        Assert.Equal(typeof(JsonElement), type.GetProperty("RawMetadata")!.PropertyType);
    }
}
