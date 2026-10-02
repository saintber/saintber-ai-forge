using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Saintber.Assistant.Abstractions;
using Saintber.Assistant.Host;
namespace Saintber.Assistant.Host.Tests;
public class HostLifecycleTests {
 [Fact] public async Task Three_eight_second_stops_run_in_eight_seconds_once() {
  var clock=new ManualClock(); var connectors=Enumerable.Range(0,3).Select(i=>new FakeConnector("line_"+i,clock)).ToArray();
  var lifecycle=Service(connectors);await lifecycle.StartAsync(default);
  var stop=lifecycle.StopAsync(default);Assert.All(connectors,c=>Assert.Equal(1,c.StopCount));clock.Advance(TimeSpan.FromSeconds(7));Assert.False(stop.IsCompleted);
  clock.Advance(TimeSpan.FromSeconds(1));await stop;await lifecycle.StopAsync(default);Assert.All(connectors,c=>Assert.Equal(1,c.StopCount));Assert.All(connectors,c=>Assert.True(c.Finished));
 }
 [Fact] public async Task One_throw_does_not_stop_other_cleanup_and_logs_only_its_id() {
  var clock=new ManualClock();var connectors=new[]{new FakeConnector("line_a",clock){Throw=true},new FakeConnector("line_b",clock),new FakeConnector("line_c",clock)};var log=new RecordingLogger();
  var lifecycle=Service(connectors,log:log);await lifecycle.StartAsync(default);var stop=lifecycle.StopAsync(default);Assert.All(connectors,c=>Assert.Equal(1,c.StopCount));clock.Advance(TimeSpan.FromSeconds(8));await stop;
  Assert.True(connectors[1].Finished);Assert.True(connectors[2].Finished);var errors=log.Entries.Where(e=>e.Level==LogLevel.Error).ToArray();Assert.Single(errors);Assert.Contains("line_a",errors[0].Message);Assert.DoesNotContain("secret-value",string.Join(" ",log.Entries.Select(e=>e.Message)));
 }
 [Fact] public async Task Host_token_is_passed_and_interrupts_immediately() {
  var clock=new ManualClock();var connector=new FakeConnector("line_a",clock);var lifecycle=Service([connector]);await lifecycle.StartAsync(default);
  using var source=new CancellationTokenSource();var stop=lifecycle.StopAsync(source.Token);Assert.Equal(source.Token,connector.Token);source.Cancel();await stop;Assert.True(connector.Interrupted);Assert.False(connector.Finished);
 }
 [Fact] public async Task Fifteen_and_twenty_five_with_default_margin_sets_thirty_and_logs_it() {
  var clock=new ManualClock();var options=Options.Create(new HostOptions {ShutdownTimeout=TimeSpan.FromSeconds(1)});var log=new RecordingLogger();
  var lifecycle=Service([new FakeConnector("line_a",clock){StopBudget=TimeSpan.FromSeconds(15)},new FakeConnector("line_b",clock){StopBudget=TimeSpan.FromSeconds(25)}],options:options,log:log);
  await lifecycle.StartAsync(default);Assert.Equal(TimeSpan.FromSeconds(30),options.Value.ShutdownTimeout);Assert.Contains(log.Entries,e=>e.Message.Contains("00:00:30"));
 }
 [Theory] [InlineData(0)] [InlineData(-1)] public void Invalid_margin_fails_without_values(int seconds) {
  var e=Assert.Throws<ConnectorStartupException>(()=>Service([],margin:TimeSpan.FromSeconds(seconds)));Assert.Contains("Assistant:Shutdown:Margin",e.Message);
 }
 [Fact] public async Task Started_connectors_only_stop_after_start_failure() {
  var clock=new ManualClock();var first=new FakeConnector("line_a",clock){Delay=TimeSpan.Zero};var failed=new FakeConnector("line_b",clock){StartThrows=true};var last=new FakeConnector("line_c",clock);
  var lifecycle=Service([first,failed,last]);var e=await Assert.ThrowsAsync<ConnectorStartupException>(()=>lifecycle.StartAsync(default));Assert.DoesNotContain("secret-value",e.ToString());Assert.Equal(1,first.StopCount);Assert.Equal(0,failed.StopCount);Assert.Equal(0,last.StartCount);
 }
 static ConnectorLifecycleService Service(IReadOnlyList<IConnector> connectors,IOptions<HostOptions>? options=null,TimeSpan? margin=null,RecordingLogger? log=null)=>new(connectors,new Handler(),options??Options.Create(new HostOptions()),margin??TimeSpan.FromSeconds(5),log??new RecordingLogger());
 sealed class Handler:IInboundMessageHandler {public Task HandleAsync(InboundEnvelope e,CancellationToken ct)=>Task.CompletedTask;}
 sealed class FakeConnector(string id,TimeProvider clock):IConnector {
  public string ConnectorType=>"fixture";public string InstanceId=>id;public TimeSpan StopBudget{get;set;}=TimeSpan.FromSeconds(15);public TimeSpan Delay{get;set;}=TimeSpan.FromSeconds(8);
  public bool Throw;public bool StartThrows;public int StartCount;public int StopCount;public bool Finished;public bool Interrupted;public CancellationToken Token;
  public Task StartAsync(IInboundMessageHandler h,CancellationToken ct){StartCount++;if(StartThrows)throw new Exception("secret-value");return Task.CompletedTask;}
  public async Task StopAsync(CancellationToken ct){StopCount++;Token=ct;if(Throw)throw new Exception("secret-value");try{await Task.Delay(Delay,clock,ct);Finished=true;}catch(OperationCanceledException){Interrupted=true;throw;}}
  public Task<DeliveryAcceptance> DeliverAsync(OutboundEnvelope e,CancellationToken ct)=>Task.FromResult(DeliveryAcceptance.Accepted);
 }
 public sealed class RecordingLogger:ILogger<ConnectorLifecycleService> {
  public List<(LogLevel Level,string Message)> Entries=[];public bool IsEnabled(LogLevel l)=>true;public IDisposable? BeginScope<TState>(TState state)where TState:notnull=>null;
  public void Log<TState>(LogLevel l,EventId id,TState state,Exception? exception,Func<TState,Exception?,string> format){lock(Entries)Entries.Add((l,format(state,exception)));}
 }
 sealed class ManualClock:TimeProvider {
  DateTimeOffset now=DateTimeOffset.UnixEpoch;readonly List<Timer> timers=[];public override DateTimeOffset GetUtcNow()=>now;
  public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan due,TimeSpan period){var timer=new Timer(this,callback,state,now+due);timers.Add(timer);return timer;}
  public void Advance(TimeSpan time){now+=time;foreach(var timer in timers.ToArray())if(!timer.Disposed&&timer.Due<=now){timer.Disposed=true;timer.Callback(timer.State);}}
  sealed class Timer(ManualClock clock,TimerCallback callback,object? state,DateTimeOffset due):ITimer {
   public TimerCallback Callback=callback;public object? State=state;public DateTimeOffset Due=due;public bool Disposed;
   public bool Change(TimeSpan due,TimeSpan period){Due=clock.now+due;return !Disposed;}public void Dispose()=>Disposed=true;public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
  }
 }
}