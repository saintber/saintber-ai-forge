using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Saintber.Assistant.Abstractions;
using Saintber.Assistant.Host;
namespace Saintber.Assistant.Host.Tests;
public class HostWebhookTests
{
    [Theory][InlineData("reply","user","/v2/bot/message/reply",2)][InlineData("push","user","/v2/bot/message/push",2)][InlineData("reply","group","/v2/bot/message/reply",1)]
    public async Task Real_line_echo_routes_and_user_loading_order(string mode,string source,string expected,int count)
    {
        await using var factory=new Factory(mode);using var client=factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK,(await Post(client,Body("E1","hi",source))).StatusCode);
        await Until(()=>factory.Api.Calls.Count==count);
        var calls=factory.Api.Calls.ToArray();Assert.Equal(expected,calls[^1].Path);Assert.Equal("Echo: hi",calls[^1].Body.GetProperty("messages")[0].GetProperty("text").GetString());
        if(source=="user")Assert.Equal("/v2/bot/chat/loading/start",calls[0].Path);
        if(mode=="push")Assert.Equal("U1",calls[^1].Body.GetProperty("to").GetString());else Assert.Equal("reply-E1",calls[^1].Body.GetProperty("replyToken").GetString());
    }
    [Theory][InlineData(false,401)][InlineData(true,400)]
    public async Task Bad_signature_or_malformed_json_is_rejected(bool validSignature,int status)
    {
        await using var factory=new Factory();using var client=factory.CreateClient();
        var body=validSignature?Encoding.UTF8.GetBytes("malformed"):Body("E1","hi","group");
        Assert.Equal(status,(int)(await Post(client,body,secret:validSignature?"test-secret":"different-secret")).StatusCode);Assert.Empty(factory.Api.Calls);
    }
    [Theory][InlineData("unknown")][InlineData("disabled")][InlineData("plain")]
    public async Task Unknown_disabled_or_non_webhook_instance_returns_404(string id)
    {
        await using var factory=new Factory();using var client=factory.CreateClient();Assert.Equal(HttpStatusCode.NotFound,(await Post(client,Body("E1","hi","group"),id)).StatusCode);Assert.Empty(factory.Api.Calls);
    }
    [Theory][InlineData(1048576,true,200)][InlineData(1048577,true,413)][InlineData(1048577,false,413)]
    public async Task Body_limit_applies_to_known_and_streamed_lengths_before_connector(int length,bool knownLength,int status)
    {
        await using var factory=new Factory();using var client=factory.CreateClient();var bytes=new byte[length];bytes[0]=123;
        using HttpContent content=knownLength?new ByteArrayContent(bytes):new StreamingContent(bytes);
        var response=await client.PostAsync("/webhook/raw",content);Assert.Equal(status,(int)response.StatusCode);
        Assert.Equal(status==200?1:0,factory.Raw.Calls);if(status==200)Assert.Equal(bytes,factory.Raw.Body);
    }
    [Theory][InlineData(200)][InlineData(401)][InlineData(400)][InlineData(503)]
    public async Task Route_forwards_original_bytes_headers_and_status(int status)
    {
        await using var factory=new Factory();factory.Raw.Status=status;using var client=factory.CreateClient();var bytes=new byte[]{0,255,13,10,128};
        using var request=new HttpRequestMessage(HttpMethod.Post,"/webhook/raw"){Content=new ByteArrayContent(bytes)};request.Headers.Add("X-Test","original-header");
        Assert.Equal(status,(int)(await client.SendAsync(request)).StatusCode);Assert.Equal(bytes,factory.Raw.Body);Assert.Equal("original-header",factory.Raw.Headers!["X-Test"]);
    }
    [Fact]
    public async Task Response_returns_before_delayed_platform_and_handler()
    {
        var releaseApi=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var releaseHandler=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var enteredHandler=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var factory=new Factory(handler:new Handler(async(_,ct)=>{enteredHandler.TrySetResult();await releaseHandler.Task.WaitAsync(ct);}));factory.Api.Delay=releaseApi.Task;
        using var client=factory.CreateClient();var response=await Post(client,Body("E1","hi","user")).WaitAsync(TimeSpan.FromSeconds(1));Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        await factory.Api.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(enteredHandler.Task.IsCompleted);Assert.False(releaseApi.Task.IsCompleted);
        await Task.Delay(3000);releaseApi.SetResult();await enteredHandler.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(releaseHandler.Task.IsCompleted);releaseHandler.SetResult();
    }
    [Fact]
    public async Task Redelivery_makes_no_second_handler_or_line_call()
    {
        await using var factory=new Factory();using var client=factory.CreateClient();var duplicate=Body("E1","hi","group");
        Assert.Equal(HttpStatusCode.OK,(await Post(client,duplicate)).StatusCode);await Until(()=>factory.Api.Calls.Count==1);
        await Post(client,duplicate);await Post(client,Body("fence","after","group"));await Until(()=>factory.Api.Calls.Count==2);
        Assert.Equal(new[]{"reply-E1","reply-fence"},factory.Api.Calls.Select(c=>c.Body.GetProperty("replyToken").GetString()).ToArray());
    }
    [Fact]
    public async Task Line_truncates_the_echo_of_five_thousand_characters()
    {
        await using var factory=new Factory();using var client=factory.CreateClient();await Post(client,Body("E1",new string('x',5000),"group"));await Until(()=>factory.Api.Calls.Count==1);
        Assert.Equal("Echo: "+new string('x',4994),factory.Api.Calls.Single().Body.GetProperty("messages")[0].GetProperty("text").GetString());
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Gateway_remains_accepted_on_platform_failure_or_transport_exception(bool throws)
    {
        await using var factory=new Factory();factory.Api.Throws=throws;factory.Api.Status=HttpStatusCode.BadRequest;using var client=factory.CreateClient();
        var gateway=factory.Services.GetRequiredService<IOutboundGateway>();var key=new ExternalKey("user","line:user:U1",JsonSerializer.SerializeToElement(new{userId="U1"}));
        Assert.Equal(SendAcceptance.Accepted,await gateway.SendAsync(new("line","line",key,null,new("hi")),default));Assert.Single(factory.Api.Calls);
    }
    [Fact]
    public async Task Stopping_and_stopped_return_unavailable_and_webhook_503_without_platform()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var factory=new Factory(handler:new Handler(async(_,ct)=>{entered.TrySetResult();await release.Task.WaitAsync(ct);}));using var client=factory.CreateClient();await Post(client,Body("E1","hi","group"));await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var connector=factory.Connectors[0];var gateway=factory.Services.GetRequiredService<IOutboundGateway>();var key=new ExternalKey("user","line:user:U1",JsonSerializer.SerializeToElement(new{userId="U1"}));var outgoing=new OutboundEnvelope("line","line",key,null,new("hi"));
        var stop=connector.StopAsync(default);Assert.Equal(SendAcceptance.ConnectorUnavailable,await gateway.SendAsync(outgoing,default));Assert.Equal(HttpStatusCode.ServiceUnavailable,(await Post(client,Body("E2","hi","group"))).StatusCode);
        release.SetResult();await stop.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(SendAcceptance.ConnectorUnavailable,await gateway.SendAsync(outgoing,default));Assert.Empty(factory.Api.Calls);
        Assert.Equal(SendAcceptance.UnknownConnector,await gateway.SendAsync(outgoing with{ConnectorInstanceId="disabled"},default));
    }
    [Theory][InlineData("/connectors")][InlineData("/settings")][InlineData("/instances")]
    public async Task No_configuration_listing_endpoint_exists(string path)
    {await using var factory=new Factory();using var client=factory.CreateClient();Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync(path)).StatusCode);}
    private static byte[] Body(string id,string text,string source)=>JsonSerializer.SerializeToUtf8Bytes(new{events=new[]{new{type="message",mode="active",webhookEventId=id,replyToken="reply-"+id,timestamp=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),source=new{type=source,userId="U1",groupId=source=="group"?"G1":null},message=new{type="text",id=id,text}}}});
    private static async Task<HttpResponseMessage> Post(HttpClient client,byte[] bytes,string id="line",string secret="test-secret")
    {using var request=new HttpRequestMessage(HttpMethod.Post,"/webhook/"+id){Content=new ByteArrayContent(bytes)};request.Headers.Add("X-Line-Signature",Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),bytes)));return await client.SendAsync(request);}
    private static async Task Until(Func<bool> check){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(!check())await Task.Delay(1,timeout.Token);}
    private sealed class Factory:WebApplicationFactory<Program>
    {
        private readonly string mode;private readonly IInboundMessageHandler? handler;
        public Api Api {get;}=new();public RawConnector Raw {get;}=new();public IReadOnlyList<IConnector> Connectors {get;}
        public Factory(string mode="reply",IInboundMessageHandler? handler=null)
        {
            this.mode=mode;this.handler=handler;var directory=new DirectoryInfo(AppContext.BaseDirectory);while(directory!=null&&!File.Exists(Path.Combine(directory.FullName,"assistant","Assistant.sln")))directory=directory.Parent;
            var published=Path.Combine(directory!.FullName,"assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/line_published");
            var loader=new ConnectorLoader(published,NullLoggerFactory.Instance,TimeProvider.System,()=>new HttpClient(Api,false));
            var line=Assert.Single(loader.Load([new("line","line",true,"Saintber.Assistant.Connectors.Line.dll",null,new Dictionary<string,string>{{"ChannelSecret","test-secret"},{"ChannelAccessToken","test-token"},{"Work:MaxConcurrency","1"},{"Timeouts:Activity","00:00:10"},{"Stop:Grace","00:00:00.100"},{"Stop:JoinTimeout","00:00:01"}})]));
            Connectors=new IConnector[]{line,Raw,new PlainConnector()};
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_,config)=>config.AddInMemoryCollection(new Dictionary<string,string?>{{"Assistant:Echo:Mode",mode}}));
            builder.ConfigureTestServices(services=>{services.RemoveAll<IReadOnlyList<IConnector>>();services.AddSingleton(Connectors);if(handler!=null){services.RemoveAll<IInboundMessageHandler>();services.AddSingleton(handler);}});
        }
    }
    private sealed class Handler(Func<InboundEnvelope,CancellationToken,Task> action):IInboundMessageHandler{public Task HandleAsync(InboundEnvelope envelope,CancellationToken ct)=>action(envelope,ct);}
    private sealed class Api:HttpMessageHandler
    {
        public ConcurrentQueue<(string Path,JsonElement Body)> Calls {get;}=new();public Task? Delay;public bool Throws;public HttpStatusCode Status=HttpStatusCode.OK;public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {using var document=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));Calls.Enqueue((request.RequestUri!.AbsolutePath,document.RootElement.Clone()));Entered.TrySetResult();if(Delay!=null)await Delay.WaitAsync(ct);if(Throws)throw new HttpRequestException("transport-secret");return new(Status){Content=new StringContent("response-secret")};}
    }
    private class PlainConnector:IConnector
    {
        public virtual string InstanceId=>"plain";public string ConnectorType=>"fixture";public TimeSpan StopBudget=>TimeSpan.Zero;
        public Task StartAsync(IInboundMessageHandler h,CancellationToken ct)=>Task.CompletedTask;public Task StopAsync(CancellationToken ct)=>Task.CompletedTask;
        public Task<DeliveryAcceptance> DeliverAsync(OutboundEnvelope e,CancellationToken ct)=>Task.FromResult(DeliveryAcceptance.Accepted);
    }
    private sealed class RawConnector:PlainConnector,IWebhookReceiver
    {
        public override string InstanceId=>"raw";public int Calls;public int Status=200;public byte[]? Body;public IReadOnlyDictionary<string,string>? Headers;
        public Task<WebhookResult> ReceiveAsync(WebhookRequest request,CancellationToken ct){Calls++;Body=request.Body.ToArray();Headers=request.Headers;return Task.FromResult(new WebhookResult(Status));}
    }
    private sealed class StreamingContent(byte[] bytes):HttpContent
    {protected override bool TryComputeLength(out long length){length=0;return false;}protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>stream.WriteAsync(bytes).AsTask();}
}
