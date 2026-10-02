using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Saintber.Assistant.Abstractions;
namespace Saintber.Assistant.Connectors.Core.Tests;
public class PipelineTests
{
    private static readonly WebhookRequest Request = new(new Dictionary<string,string>(), ReadOnlyMemory<byte>.Empty);
    internal static ExternalKey Key(string id, string kind = "message") => new(kind, "fake:" + kind + ":" + id, JsonSerializer.SerializeToElement(new { id }));
    internal static PlatformInboundEvent Event(FakeTimeProvider clock, string id) => new(id, clock.GetUtcNow(), "TOKEN-SECRET", new("fake", "main", Key("U1", "user"), Key("U1", "user"), null, Key(id), new("TEXT-SECRET"), clock.GetUtcNow(), JsonSerializer.SerializeToElement(new { })));
    internal static IConnector Create(FakeTimeProvider clock, FakeInbound inbound, FakePlatform platform, CaptureLogger logger, int workers = 4, int pending = 100, int activitySeconds = 2)
    {
        var type = Assembly.Load("Saintber.Assistant.Connectors.Core").GetType("Saintber.Assistant.Connectors.Core.WebhookConnector", true)!;
        return (IConnector)Activator.CreateInstance(type, "fake", "main", inbound, platform, clock, logger, workers, pending, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(activitySeconds))!;
    }
    internal static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(1, timeout.Token);
    }
    private static Task<WebhookResult> Receive(IConnector connector, CancellationToken token = default) => ((IWebhookReceiver)connector).ReceiveAsync(Request, token);
    [Fact] public async Task Early_ack_before_three_second_activity_and_five_second_handler_and_request_abort()
    {
        var clock = new FakeTimeProvider(); var activityArmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var handlerArmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var platform = new FakePlatform { Capabilities = new(false,true,true,TimeSpan.FromSeconds(50)), Activity = async ct => { var delay = Task.Delay(TimeSpan.FromSeconds(3), clock, ct); activityArmed.SetResult(); await delay; return null; } };
        var inbound = new FakeInbound { Events = [Event(clock, "E1")] }; var logger = new CaptureLogger(); var connector = Create(clock, inbound, platform, logger, activitySeconds: 10);
        var completed = false; var handler = new FakeHandler(async (_, ct) => { var delay = Task.Delay(TimeSpan.FromSeconds(5), clock, ct); handlerArmed.SetResult(); await delay; completed = true; });
        await connector.StartAsync(handler, default); using var requestAbort = new CancellationTokenSource();
        Assert.Equal(200, (await Receive(connector, requestAbort.Token)).StatusCode); requestAbort.Cancel();
        await activityArmed.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(completed);
        clock.Advance(TimeSpan.FromSeconds(3)); await handlerArmed.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(completed);
        clock.Advance(TimeSpan.FromSeconds(5)); await Until(() => completed); await connector.StopAsync(default);
    }
    [Theory][InlineData(true, 401)][InlineData(false, 400)]
    public async Task Verification_and_payload_errors_do_not_admit(bool verify, int status)
    {
        var clock = new FakeTimeProvider(); var inbound = new FakeInbound { VerifyFailure = verify, ParseFailure = !verify };
        var connector = Create(clock, inbound, new FakePlatform(), new CaptureLogger()); var handler = new FakeHandler((_, _) => Task.CompletedTask);
        await connector.StartAsync(handler, default); Assert.Equal(status, (await Receive(connector)).StatusCode); Assert.Equal(0, handler.Calls); await connector.StopAsync(default);
    }
    [Fact] public async Task Concurrent_duplicates_call_handler_once()
    {
        var clock = new FakeTimeProvider(); var inbound = new FakeInbound { Events = [Event(clock, "E1")] };
        var connector = Create(clock, inbound, new FakePlatform(), new CaptureLogger()); var handler = new FakeHandler((_, _) => Task.CompletedTask);
        await connector.StartAsync(handler, default); await Task.WhenAll(Enumerable.Range(0,100).Select(_ => Task.Run(() => Receive(connector))));
        await Until(() => handler.Calls == 1); await connector.StopAsync(default); Assert.Equal(1,handler.Calls);
    }
    [Fact] public async Task Failure_isolated_no_retries_and_sensitive_logs_excluded()
    {
        var clock = new FakeTimeProvider(); var inbound = new FakeInbound { Events = [Event(clock, "E1"),Event(clock,"E2"),Event(clock,"E3")] };
        var platform = new FakePlatform(); var logger = new CaptureLogger(); var connector = Create(clock, inbound, platform, logger);
        var handler = new FakeHandler((e,_) => e.Message.Equals(Key("E2")) ? Task.FromException(new Exception("TEXT-SECRET TOKEN-SECRET BODY-SECRET")) : Task.CompletedTask);
        await connector.StartAsync(handler,default); await Receive(connector); await Until(() => handler.Calls == 3); await Receive(connector); await connector.StopAsync(default);
        Assert.Equal(3,handler.Calls); Assert.Equal(0,platform.ActivityCalls); Assert.Contains(logger.Messages,x=>x.Contains("failed") && x.Contains("E2")); Assert.DoesNotContain(logger.Messages,x=>x.Contains("SECRET"));
    }
    [Fact] public async Task Four_workers_and_two_pending_only_admit_first_two_after_parse_cancellation()
    {
        var clock = new FakeTimeProvider(); var inbound = new FakeInbound { Events = Enumerable.Range(1,4).Select(i=>Event(clock,"busy"+i)).ToArray() };
        var logger = new CaptureLogger(); var connector = Create(clock,inbound,new FakePlatform(),logger,pending:2); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>(); var active = 0; var max = 0;
        var handler = new FakeHandler(async(e,_)=> {seen.Add(e.Message.CanonicalValue); var count = Interlocked.Increment(ref active); lock(seen) max=Math.Max(max,count); await release.Task; Interlocked.Decrement(ref active);});
        await connector.StartAsync(handler,default);
        // Submit one event at a time so all four workers are deterministically occupied.
        for(var i=1;i<=4;i++){inbound.Events=[Event(clock,"busy"+i)];await Receive(connector);await Until(()=>handler.Calls==i);}
        using var abort = new CancellationTokenSource(); inbound.AfterParse=abort.Cancel; inbound.Events=Enumerable.Range(1,5).Select(i=>Event(clock,"new"+i)).ToArray();
        Assert.Equal(200,(await Receive(connector,abort.Token)).StatusCode); Assert.Contains(logger.Messages,x=>x.Contains("3")); Assert.Equal(4,handler.Calls);
        release.SetResult(); await Until(()=>handler.Calls==6); inbound.AfterParse=null; inbound.Events=[Event(clock,"new3")];await Receive(connector);await Until(()=>handler.Calls==7);
        await connector.StopAsync(default); Assert.Equal(4,max); Assert.Contains("fake:message:new1",seen);Assert.Contains("fake:message:new2",seen);Assert.DoesNotContain("fake:message:new4",seen);Assert.DoesNotContain("fake:message:new5",seen);
    }
    [Theory][InlineData(31, false)][InlineData(20, true)]
    public async Task Queue_age_retains_registration_and_budget_starts_with_worker(int age, bool shouldProcess)
    {
        var clock = new FakeTimeProvider(); var inbound = new FakeInbound(); var connector=Create(clock,inbound,new FakePlatform(),new CaptureLogger(),workers:1);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var processed=false;var finished=false;
        var handler=new FakeHandler(async(e,ct)=>{if(e.Message.Equals(Key("busy")))await release.Task;else {var delay=Task.Delay(TimeSpan.FromSeconds(25),clock,ct);processed=true;await delay;finished=true;}});
        await connector.StartAsync(handler,default);inbound.Events=[Event(clock,"busy")];await Receive(connector);await Until(()=>handler.Calls==1);
        inbound.Events=[Event(clock,"pending")];await Receive(connector);clock.Advance(TimeSpan.FromSeconds(age));release.SetResult();
        if(shouldProcess){await Until(()=>processed);clock.Advance(TimeSpan.FromSeconds(25));await Until(()=>finished);}else await Until(()=>((dynamic)connector).PendingCount==0);
        await Receive(connector);await connector.StopAsync(default);Assert.Equal(shouldProcess,processed);Assert.Equal(shouldProcess?2:1,handler.Calls);
    }
    [Fact] public async Task Ten_events_never_exceed_four_handlers()
    {
        var clock=new FakeTimeProvider();var inbound=new FakeInbound{Events=Enumerable.Range(0,10).Select(i=>Event(clock,"E"+i)).ToArray()};
        var connector=Create(clock,inbound,new FakePlatform(),new CaptureLogger());var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var active=0;var max=0;var gate=new object();
        var handler=new FakeHandler(async(_,_)=>{var count=Interlocked.Increment(ref active);lock(gate)max=Math.Max(max,count);await release.Task;Interlocked.Decrement(ref active);});
        await connector.StartAsync(handler,default);await Receive(connector);await Until(()=>Volatile.Read(ref active)==4);Assert.Equal(4,max);release.SetResult();await Until(()=>handler.Calls==10);await connector.StopAsync(default);Assert.Equal(4,max);
    }
    [Theory][InlineData(true)][InlineData(false)] public async Task Delivery_failure_is_once_and_capability_controls_push(bool supportsPush)
    {
        var clock=new FakeTimeProvider();var platform=new FakePlatform{Capabilities=new(false,supportsPush,false,TimeSpan.Zero),Push=(_,_,_)=>Task.FromException(new Exception("TOKEN-SECRET"))};
        var logger=new CaptureLogger();var connector=Create(clock,new FakeInbound(),platform,logger);await connector.StartAsync(new FakeHandler((_,_)=>Task.CompletedTask),default);
        Assert.Equal(DeliveryAcceptance.Accepted,await connector.DeliverAsync(new("fake","main",Key("U1","user"),Key("M1"),new("TEXT-SECRET")),default));
        Assert.Equal(supportsPush?1:0,platform.PushCalls);Assert.Equal(0,platform.ReplyCalls);Assert.DoesNotContain(logger.Messages,x=>x.Contains("SECRET"));await connector.StopAsync(default);
    }
    [Fact] public async Task Empty_parse_returns_success_without_work()
    {
        var clock=new FakeTimeProvider();var connector=Create(clock,new FakeInbound(),new FakePlatform(),new CaptureLogger());var handler=new FakeHandler((_,_)=>Task.CompletedTask);await connector.StartAsync(handler,default);
        Assert.Equal(200,(await Receive(connector)).StatusCode);await connector.StopAsync(default);Assert.Equal(0,handler.Calls);
    }
    private static WebhookResult ReceiveNow(IConnector connector) => Receive(connector).GetAwaiter().GetResult();
    [Fact] public async Task Worker_waits_on_registration_gate()
    {
        var clock=new FakeTimeProvider();var inbound=new FakeInbound();var connector=Create(clock,inbound,new FakePlatform(),new CaptureLogger());var handler=new FakeHandler((_,_)=>Task.CompletedTask);
        await connector.StartAsync(handler,default);var registry=connector.GetType().GetField("_registry",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(connector)!;
        var gate=registry.GetType().GetProperty("Gate",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(registry)!;
        lock(gate){inbound.Events=[Event(clock,"E1")];Assert.Equal(200,ReceiveNow(connector).StatusCode);Assert.Equal(0,handler.Calls);}
        await Until(()=>handler.Calls==1);await connector.StopAsync(default);
    }
}
internal sealed class FakeHandler(Func<InboundEnvelope,CancellationToken,Task> handle):IInboundMessageHandler
{
    private int _calls; public int Calls=>Volatile.Read(ref _calls);
    public Task HandleAsync(InboundEnvelope envelope,CancellationToken ct){Interlocked.Increment(ref _calls);return handle(envelope,ct);}
}
internal sealed class FakeInbound:IWebhookInbound
{
    public IReadOnlyList<PlatformInboundEvent> Events {get;set;}=[];public bool VerifyFailure;public bool ParseFailure;public Action? AfterParse;
    public void Verify(WebhookRequest request){if(VerifyFailure)throw new WebhookVerificationException();}
    public IReadOnlyList<PlatformInboundEvent> Parse(WebhookRequest request){if(ParseFailure)throw new ConnectorPayloadException();AfterParse?.Invoke();return Events;}
}
internal sealed class FakePlatform:IMessagingPlatform
{
    public PlatformCapabilities Capabilities {get;set;}=new(false,true,false,TimeSpan.FromSeconds(50));
    private int _activityCalls;public int ActivityCalls=>Volatile.Read(ref _activityCalls);
    public Func<CancellationToken,Task<IAsyncDisposable?>>? Activity {get;set;}
    private int _pushCalls;public int PushCalls=>Volatile.Read(ref _pushCalls);private int _replyCalls;public int ReplyCalls=>Volatile.Read(ref _replyCalls);
    public Func<ExternalKey,MessageContent,CancellationToken,Task>? Push {get;set;}
    public Func<string,MessageContent,CancellationToken,Task>? Reply {get;set;}
    public Task ReplyAsync(string token,MessageContent content,CancellationToken ct){Interlocked.Increment(ref _replyCalls);return Reply?.Invoke(token,content,ct)??Task.CompletedTask;}
    public Task PushAsync(ExternalKey chat,MessageContent content,CancellationToken ct){Interlocked.Increment(ref _pushCalls);return Push?.Invoke(chat,content,ct)??Task.CompletedTask;}
    public Task<IAsyncDisposable?> StartActivityAsync(ExternalKey chat,CancellationToken ct){Interlocked.Increment(ref _activityCalls);return Activity?.Invoke(ct)??Task.FromResult<IAsyncDisposable?>(null);}
}
