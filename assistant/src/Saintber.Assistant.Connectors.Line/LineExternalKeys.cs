using System.Text.Json;
using Saintber.Assistant.Abstractions;

namespace Saintber.Assistant.Connectors.Line;

/// <summary>由同一種類與識別碼同時建立 canonical value 與獨立 properties。</summary>
public static class LineExternalKeys
{
    public static ExternalKey Create(string kind, string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var property = kind switch
        {
            "user" => "userId",
            "group" => "groupId",
            "room" => "roomId",
            "message" => "messageId",
            _ => throw new ArgumentException("Unsupported LINE key kind.", nameof(kind))
        };
        var properties = JsonSerializer.SerializeToElement(new Dictionary<string, string> { [property] = id });
        return new ExternalKey(kind, $"line:{kind}:{id}", properties);
    }
}
