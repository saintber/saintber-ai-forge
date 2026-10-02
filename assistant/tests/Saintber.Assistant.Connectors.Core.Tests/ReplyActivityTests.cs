using Microsoft.Extensions.Time.Testing;
using Saintber.Assistant.Abstractions;
namespace Saintber.Assistant.Connectors.Core.Tests;
public class ReplyActivityTests
{
    private static readonly WebhookRequest Request=new(new Dictionary<string,string>(),ReadOnlyMemory<byte>.Empty);
    private static OutboundEnvelope Out(string message="E1", string chat="U1")=>new("fake","main",PipelineTests.Key(chat,"user"),PipelineTests.Key(message),new("hello"));
    private static WebhookConnector Create(FakeTimeProvider clock,FakeInbound inbound,FakePlatform platform,CaptureLogger logger,WebhookConnectorOptions? options=null)=>new("fake","main",inbound,platform,clock,logger,options??new());
    [Theory][InlineData(10,0,1,0)][InlineData(51,0,0,1)][InlineData(1,55,0,1)][InlineData(0,55,0,1)]
    public async Task Reply_uses_conservative_event_time_and_local_expiry(int elapsed,int eventAge,int replies,int pushes)
    {
        var clock=new FakeTimeProvider();var item=PipelineTests.Event(clock,"E1") with{EventTime=clock.GetUtcNow()-TimeSpan.FromSeconds(eventAge)};var inbound=new FakeInbound{Events=[item]};var platform=new FakePlatform{Capabilities=new(true,true,false,TimeSpan.FromSeconds(50))};var connector=Create(clock,inbound,platform,new());
        await connector.StartAsync(new FakeHandler((_,_)=>Task.CompletedTask),default);await connector.ReceiveAsync(Request,default);clock.Advance(TimeSpan.FromSeconds(elapsed));await connector.DeliverAsync(Out(),default);await connector.StopAsync(default);Assert.Equal(replies,platform.ReplyCalls);Assert.Equal(pushes,platform.PushCalls);
    }
    [Theory][InlineData(0)][InlineData(51)] public async Task Mismatched_chat_pushes_without_consuming_and_logs(int age)
    {
        var clock=new FakeTimeProvider();var inbound=new FakeInbound{Events=[PipelineTests.Event(clock,"E1")]};ExternalKey? destination=null;var logger=new CaptureLogger();var platform=new FakePlatform{Capabilities=new(true,true,false,TimeSpan.FromSeconds(50)),Push=(chat,_,_)=>{destination=chat;return Task.CompletedTask;}};var connector=Create(clock,inbound,platform,logger);
        await connector.StartAsync(new FakeHandler((_,_)=>Task.CompletedTask),default);await connector.ReceiveAsync(Request,default);clock.Advance(TimeSpan.FromSeconds(age));await connector.DeliverAsync(Out(chat:"U2"),default);Assert.Equal(PipelineTests.Key("U2","user"),destination);Assert.Contains(logger.Messages,x=>x.Contains("mismatch"));
        await connector.DeliverAsync(Out(),default);Assert.Equal(age==0?1:0,platform.ReplyCalls);await connector.StopAsync(default);
    }
    [Fact] public async Task Shared_context_parallel_consumption_and_retained_reference_survive_eviction()
    {
        var clock=new FakeTimeProvider();var first=PipelineTests.Event(clock,"E1");var inbound=new FakeInbound{Events=[first,first with{EventId="E2"}]};var platform=new FakePlatform{Capabilities=new(true,true,false,TimeSpan.FromSeconds(50))};var connector=Create(clock,inbound,platform,new(),new(){DedupMaxEntries=2});
        await connector.StartAsync(new FakeHandler((_,_)=>Task.CompletedTask),default);await connector.ReceiveAsync(Request,default);await Task.WhenAll(connector.DeliverAsync(Out(),default),connector.DeliverAsync(Out(),default));Assert.Equal(1,platform.ReplyCalls);Assert.Equal(1,platform.PushCalls);
        inbound.Events=[PipelineTests.Event(clock,"E3")];await connector.ReceiveAsync(Request,default);await connector.DeliverAsync(Out(),default);Assert.Equal(1,platform.ReplyCalls);Assert.Equal(2,platform.PushCalls);
        inbound.Events=[PipelineTests.Event(clock,"E4")];await connector.ReceiveAsync(Request,default);inbound.Events=[first with{EventId="E5"}];await connector.ReceiveAsync(Request,default);platform.Reply=(_,_,_)=>Task.FromException(new Exception("TOKEN-SECRET"));Assert.Equal(DeliveryAcceptance.Accepted,await connector.DeliverAsync(Out(),default));Assert.Equal(2,platform.ReplyCalls);Assert.Equal(2,platform.PushCalls);await connector.StopAsync(default);
    }
    [Fact] public async Task Missing_in_reply_to_pushes_and_unavailable_routes_are_logged()
    {
        var clock=new FakeTimeProvider();var platform=new FakePlatform();var logger=new CaptureLogger();var connector=Create(clock,new(),platform,logger);await connector.StartAsync(new FakeHandler((_,_)=>Task.CompletedTask),default);
        await connector.DeliverAsync(Out() with{InReplyTo=null},default);Assert.Equal(1,platform.PushCalls);platform.Capabilities=new(false,false,false,TimeSpan.Zero);Assert.Equal(DeliveryAcceptance.Accepted,await connector.DeliverAsync(Out(),default));Assert.Contains(logger.Messages,x=>x.Contains("undeliverable"));await connector.StopAsync(default);
    }
    [Theory][InlineData(true)][InlineData(false)] public async Task Activity_disposed_once_by_reply_or_duration(bool reply)
    {
        var clock=new FakeTimeProvider();var resource=new CountedActivity();var platform=new FakePlatform{Capabilities=new(true,true,true,TimeSpan.FromSeconds(50)),Activity=_=>Task.FromResult<IAsyncDisposable?>(resource)};var inbound=new FakeInbound{Events=[PipelineTests.Event(clock,"E1")]};var connector=Create(clock,inbound,platform,new());var handler=new FakeHandler((_,_)=>Task.CompletedTask);
        await connector.StartAsync(handler,default);await connector.ReceiveAsync(Request,default);await PipelineTests.Until(()=>handler.Calls==1);Assert.Equal(0,resource.Disposals);
        if(reply)await connector.DeliverAsync(Out(),default);else clock.Advance(TimeSpan.FromMinutes(2));await PipelineTests.Until(()=>resource.Disposals==1);await connector.StopAsync(default);Assert.Equal(1,resource.Disposals);
    }
    [Fact] public async Task Late_activity_is_disposed_without_registration_and_repeating_timer_stops()
    {
        var clock=new FakeTimeProvider();var call=new TaskCompletionSource<IAsyncDisposable?>(TaskCreationOptions.RunContinuationsAsynchronously);var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var platform=new FakePlatform{Capabilities=new(true,true,true,TimeSpan.FromSeconds(50)),Activity=_=>{started.SetResult();return call.Task;}};var connector=Create(clock,new(){Events=[PipelineTests.Event(clock,"E1")]},platform,new());var handler=new FakeHandler((_,_)=>Task.CompletedTask);
        await connector.StartAsync(handler,default);await connector.ReceiveAsync(Request,default);await started.Task.WaitAsync(TimeSpan.FromSeconds(5));clock.Advance(TimeSpan.FromSeconds(2));await PipelineTests.Until(()=>handler.Calls==1);await connector.DeliverAsync(Out(),default);
        clock.Advance(TimeSpan.FromSeconds(1));var late=new CountedActivity(clock);call.SetResult(late);await PipelineTests.Until(()=>late.Disposals==1);clock.Advance(TimeSpan.FromMinutes(3));Assert.Equal(0,late.Ticks);await connector.StopAsync(default);Assert.Equal(1,late.Disposals);
    }
    [Fact] public async Task Immediate_activity_failure_calls_handler_once_and_records_message_key()
    {
        var clock=new FakeTimeProvider();var platform=new FakePlatform{Capabilities=new(false,true,true,TimeSpan.Zero),Activity=_=>Task.FromException<IAsyncDisposable?>(new Exception("TOKEN-SECRET"))};var logger=new CaptureLogger();var connector=Create(clock,new(){Events=[PipelineTests.Event(clock,"E1")]},platform,logger);var handler=new FakeHandler((_,_)=>Task.CompletedTask);
        await connector.StartAsync(handler,default);await connector.ReceiveAsync(Request,default);await PipelineTests.Until(()=>handler.Calls==1);await connector.StopAsync(default);Assert.Equal(1,platform.ActivityCalls);Assert.Contains(logger.Messages,x=>x.Contains("Activity")&&x.Contains("fake:message:E1"));Assert.DoesNotContain(logger.Messages,x=>x.Contains("SECRET"));
    }
    [Fact] public async Task Activity_failure_and_late_failure_are_observed_without_sensitive_text()
    {
        var clock=new FakeTimeProvider();var call=new TaskCompletionSource<IAsyncDisposable?>(TaskCreationOptions.RunContinuationsAsynchronously);var platform=new FakePlatform{Capabilities=new(false,true,true,TimeSpan.Zero),Activity=_=>call.Task};var logger=new CaptureLogger();var connector=Create(clock,new(){Events=[PipelineTests.Event(clock,"E1")]},platform,logger);var handler=new FakeHandler((_,_)=>Task.CompletedTask);
        await connector.StartAsync(handler,default);await connector.ReceiveAsync(Request,default);await PipelineTests.Until(()=>platform.ActivityCalls==1);clock.Advance(TimeSpan.FromSeconds(2));await PipelineTests.Until(()=>handler.Calls==1);call.SetException(new Exception("TOKEN-SECRET"));await PipelineTests.Until(()=>logger.Messages.Any(x=>x.Contains("Late activity")));await connector.StopAsync(default);Assert.DoesNotContain(logger.Messages,x=>x.Contains("SECRET"));
    }
}
internal sealed class CountedActivity:IAsyncDisposable
{
    private int _disposals;public int Disposals=>Volatile.Read(ref _disposals);private readonly ITimer? _timer;private int _ticks;public int Ticks=>Volatile.Read(ref _ticks);
    public CountedActivity() { }
    public CountedActivity(TimeProvider clock){_timer=clock.CreateTimer(_=>Interlocked.Increment(ref _ticks),null,TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(1));}
    public ValueTask DisposeAsync(){Interlocked.Increment(ref _disposals);_timer?.Dispose();return ValueTask.CompletedTask;}
}
