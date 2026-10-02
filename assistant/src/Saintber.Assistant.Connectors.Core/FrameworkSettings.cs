using System.Globalization;
using System.Text.RegularExpressions;
using Saintber.Assistant.Abstractions;
namespace Saintber.Assistant.Connectors.Core;
public sealed record WebhookConnectorOptions
{
    public TimeSpan DedupTtl { get; init; } = TimeSpan.FromMinutes(10);
    public int DedupMaxEntries { get; init; } = 10000;
    public TimeSpan ActivityTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan EventTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxConcurrency { get; init; } = 4;
    public int MaxPending { get; init; } = 100;
    public TimeSpan MaxQueueAge { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan OverrunGrace { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan JoinTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ActivityMaxDuration { get; init; } = TimeSpan.FromMinutes(2);
}
public static class FrameworkSettings
{
    private static SettingDescriptor Duration(string key, string value) => new(key, SettingKind.Duration, DefaultValue:value, Minimum:"00:00:00.0000001", Description:"正的時間長度；預設為估計值。");
    private static SettingDescriptor Count(string key, string value) => new(key, SettingKind.Integer, DefaultValue:value, Minimum:"1", Description:"至少 1；預設為估計值。");
    public static IReadOnlyList<SettingDescriptor> Descriptors { get; } = Array.AsReadOnly(new[] {
        Duration("Dedup:Ttl","00:10:00"), Count("Dedup:MaxEntries","10000"), Duration("Timeouts:Activity","00:00:02"),
        Duration("Timeouts:Send","00:00:10"), Duration("Timeouts:Event","00:00:30"), Count("Work:MaxConcurrency","4"),
        Count("Work:MaxPending","100"), Duration("Work:MaxQueueAge","00:00:30"), Duration("Work:OverrunGrace","00:00:05"),
        Duration("Stop:Grace","00:00:10"), Duration("Stop:JoinTimeout","00:00:05"), Duration("Activity:MaxDuration","00:02:00") });
    public static WebhookConnectorOptions Parse(IReadOnlyDictionary<string,string> values)
    {
        var input = new Dictionary<string,string>(values,StringComparer.OrdinalIgnoreCase);
        string Value(string key) => input.GetValueOrDefault(key) ?? Descriptors.Single(d=>d.Key==key).DefaultValue!;
        int CountValue(string key)
        {
            if (!int.TryParse(Value(key),NumberStyles.Integer,CultureInfo.InvariantCulture,out var result) || result < 1) throw new ArgumentException(key);
            return result;
        }
        TimeSpan DurationValue(string key)
        {
            var value=Value(key);
            if (!Regex.IsMatch(value,@"^(?:[0-9]+\.)?[0-9]{2,}:[0-5][0-9]:[0-5][0-9](?:\.[0-9]{1,7})?$") || !TimeSpan.TryParse(value,CultureInfo.InvariantCulture,out var result) || result <= TimeSpan.Zero) throw new ArgumentException(key);
            return result;
        }
        return new() { DedupTtl=DurationValue("Dedup:Ttl"),DedupMaxEntries=CountValue("Dedup:MaxEntries"),
            ActivityTimeout=DurationValue("Timeouts:Activity"),SendTimeout=DurationValue("Timeouts:Send"),EventTimeout=DurationValue("Timeouts:Event"),
            MaxConcurrency=CountValue("Work:MaxConcurrency"),MaxPending=CountValue("Work:MaxPending"),MaxQueueAge=DurationValue("Work:MaxQueueAge"),
            OverrunGrace=DurationValue("Work:OverrunGrace"),StopGrace=DurationValue("Stop:Grace"),JoinTimeout=DurationValue("Stop:JoinTimeout"),ActivityMaxDuration=DurationValue("Activity:MaxDuration") };
    }
}
