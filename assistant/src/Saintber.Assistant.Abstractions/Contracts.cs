using System.Text.Json;
using Microsoft.Extensions.Logging;
namespace Saintber.Assistant.Abstractions;

public sealed record MessageContent(string Text);
public sealed record InboundEnvelope(string ConnectorType, string ConnectorInstanceId,
    ExternalKey Actor, ExternalKey Chat, ExternalKey? Thread, ExternalKey Message,
    MessageContent Content, DateTimeOffset OccurredAt, JsonElement RawMetadata);
public sealed record OutboundEnvelope(string ConnectorType, string ConnectorInstanceId,
    ExternalKey Chat, ExternalKey? InReplyTo, MessageContent Content);
public sealed record WebhookRequest(IReadOnlyDictionary<string, string> Headers, ReadOnlyMemory<byte> Body);
public sealed record WebhookResult(int StatusCode);

/// <summary>Accepted 僅代表取得送出許可，不代表已送達。平台失敗由 Connector 記錄。</summary>
public enum DeliveryAcceptance { Accepted, Unavailable }
/// <summary>Accepted 僅代表取得送出許可，不代表已送達；另區分未知實例與已知但不可用實例。</summary>
public enum SendAcceptance { Accepted, UnknownConnector, ConnectorUnavailable }

/// <summary>實例的收發與生命週期；停止後所有送出必須回 Unavailable。</summary>
public interface IConnector
{
    string ConnectorType { get; }
    string InstanceId { get; }
    /// <summary>最壞停止時間（停止寬限加取消後等待），供 Host 計算關閉預算。</summary>
    TimeSpan StopBudget { get; }
    Task StartAsync(IInboundMessageHandler handler, CancellationToken cancellationToken);
    /// <summary>停止接受事件、於寬限後取消工作並清理。Host 取消時立即返回並隔離遲到延續。</summary>
    Task StopAsync(CancellationToken cancellationToken);
    /// <summary>取得送出許可後直接嘗試投遞；平台投遞失敗不改變 Accepted 結果。</summary>
    Task<DeliveryAcceptance> DeliverAsync(OutboundEnvelope envelope, CancellationToken cancellationToken);
}
public interface IWebhookReceiver
{
    Task<WebhookResult> ReceiveAsync(WebhookRequest request, CancellationToken cancellationToken);
}
/// <summary>取消為合作式；必須在給定的取消期限內返回。長工作必須排入佇列，不可在此內聯執行。</summary>
public interface IInboundMessageHandler
{
    Task HandleAsync(InboundEnvelope envelope, CancellationToken cancellationToken);
}
public interface IOutboundGateway
{
    /// <summary>Accepted 不承諾送達；平台失敗由 Connector 記錄且不擲回呼叫者。</summary>
    Task<SendAcceptance> SendAsync(OutboundEnvelope envelope, CancellationToken cancellationToken);
}
public enum SettingKind { String, Integer, Duration, Boolean }
/// <summary>僅描述結構、型別及範圍。平台額外規則由 factory 驗證。Secret 值不得記錄。</summary>
public sealed record SettingDescriptor(string Key, SettingKind Kind, bool Required = false,
    string? DefaultValue = null, bool Secret = false, string? Minimum = null,
    string? Maximum = null, string Description = "");
public sealed record ConnectorInstanceConfig(string Type, string InstanceId, bool Enabled,
    string Assembly, string? DisplayName, IReadOnlyDictionary<string, string> Settings);
public sealed record ConnectorCreationContext(ConnectorInstanceConfig Config,
    ILoggerFactory LoggerFactory, TimeProvider TimeProvider, Func<HttpClient> CreateHttpClient);
public interface IConnectorFactory
{
    string ConnectorType { get; }
    IReadOnlyList<SettingDescriptor> Settings { get; }
    IConnector Create(ConnectorCreationContext context);
}
