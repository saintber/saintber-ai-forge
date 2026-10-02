using Saintber.Assistant.Abstractions;
namespace Saintber.Assistant.Host;
/// <summary>通用文字 echo；平台長度限制與 reply/push 的實際選路由由 Connector 處理。</summary>
public sealed class EchoHandler : IInboundMessageHandler
{
    private readonly IOutboundGateway gateway;
    private readonly bool push;
    public EchoHandler(IOutboundGateway gateway, string? mode = null)
    {
        this.gateway = gateway;
        push = ValidateMode(mode) == "push";
    }
    internal static string ValidateMode(string? mode) => mode switch
    {
        null or "reply" => "reply",
        "push" => "push",
        _ => throw new ConnectorStartupException("Assistant:Echo:Mode")
    };
    public async Task HandleAsync(InboundEnvelope envelope, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;
        await gateway.SendAsync(new(envelope.ConnectorType, envelope.ConnectorInstanceId,
            envelope.Chat, push ? null : envelope.Message, new("Echo: " + envelope.Content.Text)), cancellationToken);
    }
}
