using Saintber.Assistant.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Saintber.Assistant.Host;
namespace Saintber.Assistant.Host.Tests;
public class HostHealthTests {
 [Fact] public async Task Composition_starts_with_registered_handler_and_sets_budget_then_stops_once() {
  var connector=new CompositionConnector();var handler=new CompositionHandler();var builder=WebApplication.CreateBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");
  builder.Services.AddSingleton<IInboundMessageHandler>(handler);builder.Services.AddConnectorHosting(_=>new Saintber.Assistant.Abstractions.IConnector[]{connector});
  await using var app=builder.Build();await app.StartAsync();Assert.Equal(1,connector.Starts);Assert.Same(handler,connector.Handler);Assert.Equal(TimeSpan.FromSeconds(30),app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.Extensions.Hosting.HostOptions>>().Value.ShutdownTimeout);
  await app.StopAsync();Assert.Equal(1,connector.Stops);
 }
 sealed class CompositionHandler:Saintber.Assistant.Abstractions.IInboundMessageHandler {public Task HandleAsync(Saintber.Assistant.Abstractions.InboundEnvelope e,CancellationToken ct)=>Task.CompletedTask;}
 sealed class CompositionConnector:Saintber.Assistant.Abstractions.IConnector {
  public string ConnectorType=>"fixture";public string InstanceId=>"line_a";public TimeSpan StopBudget=>TimeSpan.FromSeconds(25);public int Starts;public int Stops;public Saintber.Assistant.Abstractions.IInboundMessageHandler? Handler;
  public Task StartAsync(Saintber.Assistant.Abstractions.IInboundMessageHandler handler,CancellationToken ct){Handler=handler;Starts++;return Task.CompletedTask;}public Task StopAsync(CancellationToken ct){Stops++;return Task.CompletedTask;}public Task<Saintber.Assistant.Abstractions.DeliveryAcceptance> DeliverAsync(Saintber.Assistant.Abstractions.OutboundEnvelope e,CancellationToken ct)=>Task.FromResult(Saintber.Assistant.Abstractions.DeliveryAcceptance.Accepted);
 }
 [Fact] public async Task Health_is_fixed_200_without_configuration_or_external_calls() {
  var builder=WebApplication.CreateBuilder();builder.Configuration["Secret"]="secret-value";builder.WebHost.UseUrls("http://127.0.0.1:0");
  await using var app=builder.Build();app.MapAssistantHealth();await app.StartAsync();
  using var http=new HttpClient();var address=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
  var response=await http.GetAsync(address+"/healthz");Assert.Equal(System.Net.HttpStatusCode.OK,response.StatusCode);Assert.Equal("healthy",await response.Content.ReadAsStringAsync());await app.StopAsync();
 }
}