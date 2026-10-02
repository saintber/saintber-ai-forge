using System.Text.Json;
namespace Saintber.Assistant.Abstractions;

/// <summary>平台產生的外部識別；相等性僅由 CanonicalValue 決定。</summary>
public sealed class ExternalKey : IEquatable<ExternalKey>
{
    public string Kind { get; }
    public string CanonicalValue { get; }
    public JsonElement Properties { get; }
    public ExternalKey(string kind, string canonicalValue, JsonElement properties)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(canonicalValue);
        Kind = kind; CanonicalValue = canonicalValue; Properties = properties.Clone();
    }
    public bool Equals(ExternalKey? other) => other is not null && StringComparer.Ordinal.Equals(CanonicalValue, other.CanonicalValue);
    public override bool Equals(object? obj) => obj is ExternalKey key && Equals(key);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(CanonicalValue);
    public override string ToString() => CanonicalValue;
}
