using Saintber.Assistant.Abstractions;
namespace Saintber.Assistant.Host;
/// <summary>Host 只組合設定、通用介面與 HTTP；平台程式碼在獨立載入環境中。</summary>
public static class AssistantComposition
{
    public static IServiceCollection AddAssistant(this IServiceCollection services, AssistantConfiguration settings)
    {
        services.AddSingleton(settings);
        services.AddSingleton(TimeProvider.System);
        services.AddHttpClient("connector", client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddConnectorHosting(provider =>
        {
            _ = EchoHandler.ValidateMode(provider.GetRequiredService<IConfiguration>()["Assistant:Echo:Mode"]);
            return settings.LoadConnectors(provider.GetRequiredService<ILoggerFactory>(),
                provider.GetRequiredService<TimeProvider>(),
                () => provider.GetRequiredService<IHttpClientFactory>().CreateClient("connector"));
        }, settings.ShutdownMargin);
        services.AddSingleton<IOutboundGateway, OutboundGateway>();
        services.AddSingleton<IInboundMessageHandler>(provider => new EchoHandler(
            provider.GetRequiredService<IOutboundGateway>(), provider.GetRequiredService<IConfiguration>()["Assistant:Echo:Mode"]));
        return services;
    }
}
