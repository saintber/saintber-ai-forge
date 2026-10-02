using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Saintber.Assistant.Abstractions;
namespace Saintber.Assistant.Host;
/// <summary>設定解析與訊息 handler 由呼叫端提供，Host 只註冊通用生命週期。</summary>
public static class ConnectorComposition
{
    public static IServiceCollection AddConnectorHosting(this IServiceCollection services,
        Func<IServiceProvider, IReadOnlyList<IConnector>> load, TimeSpan? margin = null)
    {
        services.AddSingleton(load);
        services.AddSingleton(provider => new ConnectorLifecycleService(
            provider.GetRequiredService<IReadOnlyList<IConnector>>(),
            provider.GetRequiredService<IInboundMessageHandler>(),
            provider.GetRequiredService<IOptions<HostOptions>>(), margin ?? TimeSpan.FromSeconds(5),
            provider.GetRequiredService<ILogger<ConnectorLifecycleService>>()));
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ConnectorLifecycleService>());
        return services;
    }
}
