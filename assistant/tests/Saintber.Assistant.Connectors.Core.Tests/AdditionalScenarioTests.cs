using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Saintber.Assistant.Abstractions;
namespace Saintber.Assistant.Connectors.Core.Tests;
public class AdditionalScenarioTests
{
    private static readonly WebhookRequest Request = new(new Dictionary<string,string>(), ReadOnlyMemory<byte>.Empty);
    [Fact]
    public async Task Three_events_are_admitted_in_original_order()
    {
        var clock = new FakeTimeProvider();
        var inbound = new FakeInbound { Events = [PipelineTests.Event(clock,"E1"), PipelineTests.Event(clock,"E2"), PipelineTests.Event(clock,"E3")] };
        var seen = new ConcurrentQueue<string>();
        var connector = new WebhookConnector("fake","main",inbound,new FakePlatform(),clock,new CaptureLogger(),new(){MaxConcurrency=1});
        await connector.StartAsync(new FakeHandler((e,_)=>{seen.Enqueue(e.Message.CanonicalValue);return Task.CompletedTask;}),default);
        Assert.Equal(200,(await connector.ReceiveAsync(Request,default)).StatusCode);
        await PipelineTests.Until(()=>seen.Count==3);
        await connector.StopAsync(default);
        Assert.Equal(new[]{"fake:message:E1","fake:message:E2","fake:message:E3"},seen.ToArray());
    }
    [Fact]
    public async Task Evicted_event_can_run_again_while_original_handler_is_still_running()
    {
        var clock = new FakeTimeProvider(); var inbound = new FakeInbound(); var logger = new CaptureLogger();
        var connector = new WebhookConnector("fake","main",inbound,new FakePlatform(),clock,logger,new(){DedupMaxEntries=2,MaxConcurrency=4});
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var activeFirst = 0;
        var handler = new FakeHandler(async(e,_)=>{if(e.Message.Equals(PipelineTests.Key("E1"))){Interlocked.Increment(ref activeFirst);await release.Task;Interlocked.Decrement(ref activeFirst);}});
        await connector.StartAsync(handler,default);
        inbound.Events=[PipelineTests.Event(clock,"E1")];await connector.ReceiveAsync(Request,default);await PipelineTests.Until(()=>Volatile.Read(ref activeFirst)==1);
        inbound.Events=[PipelineTests.Event(clock,"E2"),PipelineTests.Event(clock,"E3")];await connector.ReceiveAsync(Request,default);
        inbound.Events=[PipelineTests.Event(clock,"E1")];Assert.Equal(200,(await connector.ReceiveAsync(Request,default)).StatusCode);
        await PipelineTests.Until(()=>Volatile.Read(ref activeFirst)==2);release.SetResult();await PipelineTests.Until(()=>Volatile.Read(ref activeFirst)==0);
        await connector.StopAsync(default);Assert.Equal(4,handler.Calls);Assert.Contains(logger.Messages,m=>m.Contains("evict"));Assert.DoesNotContain(logger.Messages,m=>m.Contains("failed"));
    }
    [Fact]
    public async Task Expired_reply_without_push_is_logged_as_undeliverable()
    {
        var clock = new FakeTimeProvider(); var logger = new CaptureLogger();
        var platform = new FakePlatform { Capabilities = new(true,false,false,TimeSpan.FromSeconds(50)) };
        var connector = new WebhookConnector("fake","main",new FakeInbound{Events=[PipelineTests.Event(clock,"E1")]},platform,clock,logger,new());
        await connector.StartAsync(new FakeHandler((_,_)=>Task.CompletedTask),default);
        await connector.ReceiveAsync(Request,default); clock.Advance(TimeSpan.FromSeconds(51));
        Assert.Equal(DeliveryAcceptance.Accepted,await connector.DeliverAsync(new("fake","main",PipelineTests.Key("U1","user"),PipelineTests.Key("E1"),new("hi")),default));
        await connector.StopAsync(default);
        Assert.Equal(0,platform.ReplyCalls); Assert.Equal(0,platform.PushCalls);
        Assert.Contains(logger.Messages,m=>m.Contains("undeliverable"));
    }
    [Fact]
    public async Task Settings_override_runs_eight_workers_and_cancels_at_sixty_seconds()
    {
        var clock = new FakeTimeProvider(); var inbound = new FakeInbound{Events=Enumerable.Range(1,9).Select(i=>PipelineTests.Event(clock,"E"+i)).ToArray()};
        var options = FrameworkSettings.Parse(new Dictionary<string,string>{{"Work:MaxConcurrency","8"},{"Timeouts:Event","00:01:00"},{"Work:MaxQueueAge","00:02:00"}});
        var connector = new WebhookConnector("fake","main",inbound,new FakePlatform(),clock,new CaptureLogger(),options);
        var armed = 0; var cancelled = 0; var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler(async(_,ct)=>{
            var delay = Task.Delay(Timeout.InfiniteTimeSpan,clock,ct);
            Interlocked.Increment(ref armed);
            try { await Task.WhenAny(delay,release.Task); await (release.Task.IsCompleted ? release.Task : delay); }
            catch(OperationCanceledException) { Interlocked.Increment(ref cancelled); }
        });
        await connector.StartAsync(handler,default); await connector.ReceiveAsync(Request,default);
        await PipelineTests.Until(()=>Volatile.Read(ref armed)==8);
        Assert.Equal(8,handler.Calls); Assert.Equal(1,connector.PendingCount);
        clock.Advance(TimeSpan.FromSeconds(59)); Assert.Equal(0,Volatile.Read(ref cancelled)); Assert.Equal(8,handler.Calls);
        clock.Advance(TimeSpan.FromSeconds(1)); await PipelineTests.Until(()=>Volatile.Read(ref cancelled)==8);
        await PipelineTests.Until(()=>Volatile.Read(ref armed)==9); release.SetResult();
        await connector.StopAsync(default); Assert.Equal(9,handler.Calls);
    }
}
