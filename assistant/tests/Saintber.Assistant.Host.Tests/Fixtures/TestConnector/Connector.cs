using Saintber.Assistant.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text.Json;
public sealed class FixtureFactory : IConnectorFactory {
 public static int Discoveries;
 public static System.Reflection.Assembly LoggingAssembly = typeof(ILogger).Assembly;
 public FixtureFactory() { Discoveries++; }
 public string ConnectorType => "fixture";
 public IReadOnlyList<SettingDescriptor> Settings => [];
 public IConnector Create(ConnectorCreationContext context) {
  if(context.Config.Settings.ContainsKey("Throw")) throw new InvalidOperationException("secret-value");
  return new FixtureConnector(context.Config.InstanceId);
 }
}
public sealed class FixtureConnector(string id) : IConnector {
 public string ConnectorType => "fixture";
 public string InstanceId => id;
 public TimeSpan StopBudget => TimeSpan.FromSeconds(15);
 public bool Stopped {get; private set;}
 public Task StartAsync(IInboundMessageHandler handler, CancellationToken ct) {
  using var json=JsonDocument.Parse("{}");
  var key=new ExternalKey("user","fixture",json.RootElement);
  return handler.HandleAsync(new("fixture",id,key,key,null,key,new(FixtureDependency.FixtureText.Value),DateTimeOffset.UtcNow,json.RootElement.Clone()),ct);
 }
 public Task StopAsync(CancellationToken ct) { Stopped=true; return Task.CompletedTask; }
 public Task<DeliveryAcceptance> DeliverAsync(OutboundEnvelope envelope,CancellationToken ct)=>Task.FromResult(Stopped ? DeliveryAcceptance.Unavailable : DeliveryAcceptance.Accepted);
}