using System.Text.Json;
using Saintber.Assistant.Abstractions;
using Saintber.Assistant.Host;
namespace Saintber.Assistant.Host.Tests;
public class HostMessagingTests
{
    private static ExternalKey Key(string id)=>new("message",id,JsonSerializer.SerializeToElement(new{id}));
    private static InboundEnvelope Inbound(string text="hi")=>new("fake","main",Key("actor"),Key("chat"),null,Key("message"),new(text),DateTimeOffset.UnixEpoch,JsonSerializer.SerializeToElement(new{}));
    [Theory][InlineData(null,true)][InlineData("reply",true)][InlineData("push",false)]
    public async Task Echo_preserves_target_and_text_with_mode_specific_reply(string? mode,bool reply)
    {
        var gateway=new Gateway();var inbound=Inbound();var handler=new EchoHandler(gateway,mode);
        await handler.HandleAsync(inbound,default);var outgoing=Assert.Single(gateway.Sent);
        Assert.Equal("fake",outgoing.ConnectorType);Assert.Equal("main",outgoing.ConnectorInstanceId);Assert.Equal(inbound.Chat,outgoing.Chat);
        Assert.Equal("Echo: hi",outgoing.Content.Text);Assert.Equal(reply?inbound.Message:null,outgoing.InReplyTo);
    }
    [Theory][InlineData("broadcast")][InlineData("invalid-secret-value")]
    public void Invalid_echo_mode_names_only_the_setting(string value)
    {
        var error=Assert.Throws<ConnectorStartupException>(()=>new EchoHandler(new Gateway(),value));
        Assert.Equal("Assistant:Echo:Mode",error.Message);Assert.DoesNotContain(value,error.Message);
    }
    [Fact]
    public async Task Cancelled_handler_sends_nothing()
    {
        var gateway=new Gateway();using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        await new EchoHandler(gateway).HandleAsync(Inbound(),cancellation.Token);Assert.Empty(gateway.Sent);
    }
    [Fact]
    public async Task Echo_preserves_all_five_thousand_input_units()
    {
        var gateway=new Gateway();await new EchoHandler(gateway).HandleAsync(Inbound(new string('x',5000)),default);
        Assert.Equal(5006,Assert.Single(gateway.Sent).Content.Text.Length);
    }
    [Theory][InlineData(DeliveryAcceptance.Accepted,SendAcceptance.Accepted)][InlineData(DeliveryAcceptance.Unavailable,SendAcceptance.ConnectorUnavailable)]
    public async Task Gateway_routes_only_to_matching_type_and_instance(DeliveryAcceptance delivery,SendAcceptance result)
    {
        var first=new Connector("first");var second=new Connector("second"){Result=delivery};var gateway=new OutboundGateway(new IConnector[]{first,second});
        var outgoing=new OutboundEnvelope("fake","second",Key("chat"),null,new("hi"));
        Assert.Equal(result,await gateway.SendAsync(outgoing,default));Assert.Empty(first.Sent);Assert.Equal(outgoing,Assert.Single(second.Sent));
    }
    [Theory][InlineData("fake","disabled")][InlineData("wrong-type","main")]
    public async Task Unknown_or_disabled_target_calls_no_connector(string type,string instance)
    {
        var connector=new Connector("main");var gateway=new OutboundGateway(new IConnector[]{connector});
        Assert.Equal(SendAcceptance.UnknownConnector,await gateway.SendAsync(new(type,instance,Key("chat"),null,new("hi")),default));Assert.Empty(connector.Sent);
    }
    private sealed class Gateway:IOutboundGateway
    {
        public List<OutboundEnvelope> Sent {get;}=[];
        public Task<SendAcceptance> SendAsync(OutboundEnvelope envelope,CancellationToken ct){Sent.Add(envelope);return Task.FromResult(SendAcceptance.Accepted);}
    }
    private sealed class Connector(string instance):IConnector
    {
        public string ConnectorType=>"fake";public string InstanceId=>instance;public TimeSpan StopBudget=>TimeSpan.FromSeconds(15);
        public DeliveryAcceptance Result=DeliveryAcceptance.Accepted;public List<OutboundEnvelope> Sent {get;}=[];
        public Task StartAsync(IInboundMessageHandler h,CancellationToken ct)=>Task.CompletedTask;
        public Task StopAsync(CancellationToken ct)=>Task.CompletedTask;
        public Task<DeliveryAcceptance> DeliverAsync(OutboundEnvelope envelope,CancellationToken ct){Sent.Add(envelope);return Task.FromResult(Result);}
    }
}
