using Saintber.Assistant.Abstractions;
namespace Saintber.Assistant.Host;
/// <summary>僅路由到已載入實例；Accepted 不代表訊息已送達。</summary>
public sealed class OutboundGateway : IOutboundGateway
{
    private readonly IReadOnlyDictionary<string, IConnector> targets;
    public OutboundGateway(IReadOnlyList<IConnector> connectors) => targets =
        connectors.ToDictionary(connector => connector.InstanceId, StringComparer.OrdinalIgnoreCase);
    public async Task<SendAcceptance> SendAsync(OutboundEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!targets.TryGetValue(envelope.ConnectorInstanceId, out var connector)
            || !string.Equals(connector.ConnectorType, envelope.ConnectorType, StringComparison.OrdinalIgnoreCase))
            return SendAcceptance.UnknownConnector;
        var acceptance = await connector.DeliverAsync(envelope, cancellationToken);
        return acceptance == DeliveryAcceptance.Accepted ? SendAcceptance.Accepted : SendAcceptance.ConnectorUnavailable;
    }
}
