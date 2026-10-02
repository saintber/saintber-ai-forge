using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Saintber.Assistant.Abstractions;
namespace Saintber.Assistant.Host;

/// <summary>僅驗證 factory 宣告的鍵、種類及範圍；平台額外規則仍由 factory 負責。</summary>
public static class ConnectorSettingsValidator
{
    private static readonly Regex DurationFormat = new(@"^(?:[0-9]+\.)?[0-9]{2}:[0-5][0-9]:[0-5][0-9](?:\.[0-9]{1,7})?$", RegexOptions.CultureInvariant);
    public static ConnectorInstanceConfig Validate(ConnectorInstanceConfig config, IReadOnlyList<SettingDescriptor> descriptors)
    {
        var known = descriptors.ToDictionary(descriptor => descriptor.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var key in config.Settings.Keys)
            if (!known.ContainsKey(key)) throw Error(config.InstanceId, key);
        var supplied = new Dictionary<string, string>(config.Settings, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in descriptors)
        {
            var value = supplied.GetValueOrDefault(descriptor.Key) ?? descriptor.DefaultValue;
            if (value is null)
            {
                if (descriptor.Required) throw Error(config.InstanceId, descriptor.Key);
                continue;
            }
            if (descriptor.Required && value.Length == 0) throw Error(config.InstanceId, descriptor.Key);
            try
            {
                var parsed = Parse(value, descriptor.Kind);
                if (descriptor.Minimum is not null && Compare(parsed, Parse(descriptor.Minimum, descriptor.Kind)) < 0
                    || descriptor.Maximum is not null && Compare(parsed, Parse(descriptor.Maximum, descriptor.Kind)) > 0)
                    throw Error(config.InstanceId, descriptor.Key);
            }
            catch (Exception) { throw Error(config.InstanceId, descriptor.Key); }
            result.Add(descriptor.Key, value);
        }
        return config with { Settings = new ReadOnlyDictionary<string, string>(result) };
    }
    internal static bool TryDuration(string value, out TimeSpan duration)
    {
        duration = default;
        return DurationFormat.IsMatch(value) && TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out duration);
    }
    private static object Parse(string value, SettingKind kind) => kind switch
    {
        SettingKind.String => value,
        SettingKind.Integer when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
        SettingKind.Duration when TryDuration(value, out var duration) => duration,
        SettingKind.Boolean when bool.TryParse(value, out var boolean) => boolean,
        _ => throw new FormatException()
    };
    private static int Compare(object value, object bound) => value is string text
        ? string.Compare(text, (string)bound, StringComparison.Ordinal)
        : ((IComparable)value).CompareTo(bound);
    private static ConnectorStartupException Error(string id, string key) => new(id + " " + key);
}
