using System.Reflection;
using Microsoft.Extensions.Time.Testing;
using Saintber.Assistant.Abstractions;
namespace Saintber.Assistant.Connectors.Core.Tests;
public class FrameworkSettingsTests
{
    private static Type SettingsType=>Assembly.Load("Saintber.Assistant.Connectors.Core").GetType("Saintber.Assistant.Connectors.Core.FrameworkSettings",true)!;
    private static dynamic Parse(Dictionary<string,string> values)=>SettingsType.GetMethod("Parse")!.Invoke(null,[values])!;
    [Fact] public void Descriptors_declare_all_defaults_and_bounds()
    {
        var descriptors=(IReadOnlyList<SettingDescriptor>)SettingsType.GetProperty("Descriptors")!.GetValue(null)!;
        var defaults=new Dictionary<string,string>{{"Dedup:Ttl","00:10:00"},{"Dedup:MaxEntries","10000"},{"Timeouts:Activity","00:00:02"},{"Timeouts:Send","00:00:10"},{"Timeouts:Event","00:00:30"},{"Work:MaxConcurrency","4"},{"Work:MaxPending","100"},{"Work:MaxQueueAge","00:00:30"},{"Work:OverrunGrace","00:00:05"},{"Stop:Grace","00:00:10"},{"Stop:JoinTimeout","00:00:05"},{"Activity:MaxDuration","00:02:00"}};
        Assert.Equal(defaults.Count,descriptors.Count);foreach(var descriptor in descriptors){Assert.Equal(defaults[descriptor.Key],descriptor.DefaultValue);Assert.NotNull(descriptor.Minimum);}
        dynamic options=Parse([]);Assert.Equal(4,(int)options.MaxConcurrency);Assert.Equal(TimeSpan.FromSeconds(30),(TimeSpan)options.EventTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10),(TimeSpan)options.DedupTtl);Assert.Equal(10000,(int)options.DedupMaxEntries);
        Assert.Equal(TimeSpan.FromSeconds(2),(TimeSpan)options.ActivityTimeout);Assert.Equal(TimeSpan.FromSeconds(10),(TimeSpan)options.SendTimeout);
        Assert.Equal(100,(int)options.MaxPending);Assert.Equal(TimeSpan.FromSeconds(30),(TimeSpan)options.MaxQueueAge);Assert.Equal(TimeSpan.FromSeconds(5),(TimeSpan)options.OverrunGrace);
        Assert.Equal(TimeSpan.FromSeconds(10),(TimeSpan)options.StopGrace);Assert.Equal(TimeSpan.FromSeconds(5),(TimeSpan)options.JoinTimeout);Assert.Equal(TimeSpan.FromMinutes(2),(TimeSpan)options.ActivityMaxDuration);
        options=Parse(new(){{"Work:MaxConcurrency","8"},{"Timeouts:Event","00:01:00"}});Assert.Equal(8,(int)options.MaxConcurrency);Assert.Equal(TimeSpan.FromSeconds(60),(TimeSpan)options.EventTimeout);
    }
    [Theory][InlineData("Work:MaxConcurrency","0")][InlineData("Timeouts:Send","-00:00:07")][InlineData("Timeouts:Event","60")]
    public void Invalid_values_name_only_key(string key,string value)
    {
        var error=Assert.Throws<TargetInvocationException>(()=>Parse(new(){{key,value}}));Assert.Equal(key,error.InnerException!.Message);
    }
    private static IConnector Create(FakeTimeProvider clock,FakePlatform platform,object options)
        =>(IConnector)SettingsType.Assembly.GetType("Saintber.Assistant.Connectors.Core.WebhookConnector")!.GetConstructors().Single(c=>c.GetParameters().Length==7).Invoke(["fake","main",new FakeInbound{Events=[PipelineTests.Event(clock,"E1")]},platform,clock,new CaptureLogger(),options]);
    [Fact] public async Task Activity_timeout_continues_to_handler_and_event_budget_cancels_cooperative_work()
    {
        var clock=new FakeTimeProvider();var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var platform=new FakePlatform{Capabilities=new(false,true,true,TimeSpan.FromSeconds(50)),Activity=async ct=>{var delay=Task.Delay(Timeout.InfiniteTimeSpan,clock,ct);started.SetResult();await delay;return null;}};
        var options=(object)Parse([]);var connector=Create(clock,platform,options);var cancelled=false;var handlerStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler=new FakeHandler(async(_,ct)=>{var delay=Task.Delay(Timeout.InfiniteTimeSpan,clock,ct);handlerStarted.SetResult();try{await delay;}catch(OperationCanceledException){cancelled=ct.IsCancellationRequested;}});
        await connector.StartAsync(handler,default);await ((IWebhookReceiver)connector).ReceiveAsync(new(new Dictionary<string,string>(),ReadOnlyMemory<byte>.Empty),default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));clock.Advance(TimeSpan.FromSeconds(2));await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(cancelled);
        clock.Advance(TimeSpan.FromSeconds(28));await PipelineTests.Until(()=>cancelled);await connector.StopAsync(default);
    }
    [Fact] public async Task Hanging_reply_is_cancelled_and_does_not_block_other_events()
    {
        var clock=new FakeTimeProvider();var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var cancelled=false;
        var platform=new FakePlatform{Capabilities=new(true,true,false,TimeSpan.FromSeconds(50)),Reply=async(_,_,ct)=>{var delay=Task.Delay(Timeout.InfiniteTimeSpan,clock,ct);started.SetResult();try{await delay;}catch(OperationCanceledException){cancelled=true;throw;}}};
        var connector=Create(clock,platform,(object)Parse([]));var handler=new FakeHandler((_,_)=>Task.CompletedTask);await connector.StartAsync(handler,default);
        await ((IWebhookReceiver)connector).ReceiveAsync(new(new Dictionary<string,string>(),ReadOnlyMemory<byte>.Empty),default);await PipelineTests.Until(()=>handler.Calls==1);
        var send=connector.DeliverAsync(new("fake","main",PipelineTests.Key("U1","user"),PipelineTests.Key("E1"),new("hi")),default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));clock.Advance(TimeSpan.FromSeconds(10));Assert.Equal(DeliveryAcceptance.Accepted,await send.WaitAsync(TimeSpan.FromSeconds(5)));await PipelineTests.Until(()=>cancelled);Assert.Equal(1,platform.ReplyCalls);Assert.Equal(0,platform.PushCalls);await connector.StopAsync(default);
    }
    [Fact] public async Task Hanging_send_is_cancelled_then_next_call_is_unaffected()
    {
        var clock=new FakeTimeProvider();var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var cancelled=false;var platform=new FakePlatform{Push=async(_,_,ct)=>{var delay=Task.Delay(Timeout.InfiniteTimeSpan,clock,ct);started.SetResult();try{await delay;}catch(OperationCanceledException){cancelled=ct.IsCancellationRequested;throw;}}};
        var connector=Create(clock,platform,(object)Parse([]));await connector.StartAsync(new FakeHandler((_,_)=>Task.CompletedTask),default);
        var envelope=new OutboundEnvelope("fake","main",PipelineTests.Key("U1","user"),null,new("hi"));var send=connector.DeliverAsync(envelope,default);await started.Task.WaitAsync(TimeSpan.FromSeconds(5));clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(DeliveryAcceptance.Accepted,await send.WaitAsync(TimeSpan.FromSeconds(5)));await PipelineTests.Until(()=>cancelled);Assert.True(cancelled);platform.Push=null;Assert.Equal(DeliveryAcceptance.Accepted,await connector.DeliverAsync(envelope,default));Assert.Equal(2,platform.PushCalls);await connector.StopAsync(default);
    }
}
