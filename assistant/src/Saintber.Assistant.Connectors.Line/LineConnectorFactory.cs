using System.Globalization;
using Microsoft.Extensions.Logging;
using Saintber.Assistant.Abstractions;
using Saintber.Assistant.Connectors.Core;

namespace Saintber.Assistant.Connectors.Line;

public sealed class LineConnectorFactory : IConnectorFactory
{
    public string ConnectorType => "line";
    public IReadOnlyList<SettingDescriptor> Settings { get; } = Array.AsReadOnly(new[]
    {
        new SettingDescriptor("ChannelSecret", SettingKind.String, Required: true, Secret: true),
        new SettingDescriptor("ChannelAccessToken", SettingKind.String, Required: true, Secret: true),
        new SettingDescriptor("ApiBaseUrl", SettingKind.String, DefaultValue: "https://api.line.me"),
        new SettingDescriptor("LoadingSeconds", SettingKind.Integer, DefaultValue: "5", Minimum: "5", Maximum: "60",
            Description: "5～60 秒，必須為 5 的倍數；預設為估計值。")
    }.Concat(FrameworkSettings.Descriptors).ToArray());

    public IConnector Create(ConnectorCreationContext context)
    {
        var values = new Dictionary<string, string>(context.Config.Settings, StringComparer.OrdinalIgnoreCase);
        var missing = new[] { "ChannelSecret", "ChannelAccessToken" }
            .Where(key => !values.TryGetValue(key, out var value) || string.IsNullOrEmpty(value)).ToArray();
        if (missing.Length != 0) throw new ArgumentException(string.Join(", ", missing));

        var baseUrl = values.GetValueOrDefault("ApiBaseUrl") ?? "https://api.line.me";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(baseUri.Host))
            throw new ArgumentException("ApiBaseUrl");
        var loading = values.GetValueOrDefault("LoadingSeconds") ?? "5";
        if (!int.TryParse(loading, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            || seconds < 5 || seconds > 60 || seconds % 5 != 0)
            throw new ArgumentException("LoadingSeconds");
        var options = FrameworkSettings.Parse(values);

        var logger = context.LoggerFactory.CreateLogger<LinePlatform>();
        var api = new LineApiClient(context.CreateHttpClient(), baseUri, values["ChannelAccessToken"], logger);
        var platform = new LinePlatform(context.Config.InstanceId, values["ChannelSecret"], api, seconds, logger);
        return new WebhookConnector(ConnectorType, context.Config.InstanceId, platform, platform,
            context.TimeProvider, logger, options);
    }
}
