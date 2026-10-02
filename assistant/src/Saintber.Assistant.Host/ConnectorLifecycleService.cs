using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Saintber.Assistant.Abstractions;
namespace Saintber.Assistant.Host;
/// <summary>Host 僅讀取公開 StopBudget；所有成功啟動的實例並行且恰好停止一次。</summary>
public sealed class ConnectorLifecycleService : IHostedService
{
    private readonly IReadOnlyList<IConnector> connectors;
    private readonly IInboundMessageHandler handler;
    private readonly ILogger<ConnectorLifecycleService> logger;
    private readonly List<IConnector> started = [];
    private readonly object stopLock = new();
    private Task? stopTask;
    private readonly TimeSpan shutdownBudget;
    public ConnectorLifecycleService(IReadOnlyList<IConnector> connectors, IInboundMessageHandler handler,
        IOptions<HostOptions> options, TimeSpan margin, ILogger<ConnectorLifecycleService> logger)
    {
        if (margin <= TimeSpan.Zero) throw new ConnectorStartupException("Assistant:Shutdown:Margin");
        this.connectors = connectors;
        this.handler = handler;
        this.logger = logger;
        try
        {
            shutdownBudget = (connectors.Count == 0 ? TimeSpan.Zero : connectors.Max(c => c.StopBudget)) + margin;
            options.Value.ShutdownTimeout = shutdownBudget;
        }
        catch (Exception) { throw new ConnectorStartupException("Assistant:Shutdown:Margin StopBudget"); }
    }
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Host shutdown budget {ShutdownBudget}", shutdownBudget);
        foreach (var connector in connectors)
        {
            try
            {
                await connector.StartAsync(handler, cancellationToken).ConfigureAwait(false);
                started.Add(connector);
            }
            catch (Exception)
            {
                await StopAsync(cancellationToken).ConfigureAwait(false);
                throw new ConnectorStartupException($"{connector.InstanceId} Start");
            }
        }
    }
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (stopLock)
            return stopTask ??= Task.WhenAll(started.Select(connector => StopOneAsync(connector, cancellationToken)));
    }
    private async Task StopOneAsync(IConnector connector, CancellationToken cancellationToken)
    {
        try { await connector.StopAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Connector stop cancelled {InstanceId}", connector.InstanceId);
        }
        catch (Exception)
        {
            // 不傳入例外：平台與 factory 的訊息可能攜帶設定值。
            logger.LogError("Connector stop failed {InstanceId}", connector.InstanceId);
        }
    }
}
