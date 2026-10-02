using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging.Abstractions;
using Saintber.Assistant.Abstractions;
using Saintber.Assistant.Host;
namespace Saintber.Assistant.Host.Tests;
public class HostLoadingTests {
 static string Repo { get { var d=new DirectoryInfo(AppContext.BaseDirectory); while(d is not null && !File.Exists(Path.Combine(d.FullName,"assistant","Assistant.sln"))) d=d.Parent; return d!.FullName; } }
 static string Published => Path.Combine(Repo,"assistant","tests","Saintber.Assistant.Host.Tests","Fixtures","published");
 static ConnectorInstanceConfig Config(string id="line_a", string assembly="TestConnector.dll",bool enabled=true,string type="fixture")=>new(type,id,enabled,assembly,null,new Dictionary<string,string>{{"Secret","secret-value"}});
 static ConnectorLoader Loader(string? path=null)=>new(path??Published,NullLoggerFactory.Instance,TimeProvider.System,()=>new HttpClient());
 [Fact] public async Task Clean_publish_same_path_factory_once_shared_type_and_independent_instances() {
  var loader=Loader(); var connectors=loader.Load([Config(),Config("line_b")]);
  Assert.Equal(2,connectors.Count); Assert.NotSame(connectors[0],connectors[1]);
  var assembly=connectors[0].GetType().Assembly; Assert.Same(assembly,connectors[1].GetType().Assembly);
  Assert.NotSame(AssemblyLoadContext.Default,AssemblyLoadContext.GetLoadContext(assembly));
  Assert.Equal(1,(int)assembly.GetType("FixtureFactory")!.GetField("Discoveries")!.GetValue(null)!);
  Assert.Same(typeof(Microsoft.Extensions.Logging.ILogger).Assembly,assembly.GetType("FixtureFactory")!.GetField("LoggingAssembly")!.GetValue(null));
  var handler=new CapturingHandler(); await connectors[0].StartAsync(handler,default);
  Assert.IsType<InboundEnvelope>(handler.Envelope); Assert.Equal("hello",handler.Envelope!.Content.Text); Assert.Equal("line_a",handler.Envelope!.ConnectorInstanceId);
  await connectors[0].StopAsync(default);
  Assert.Equal(DeliveryAcceptance.Accepted,await connectors[1].DeliverAsync(null!,default));
 }
 [Fact] public void A_single_trailing_underscore_is_valid()=>Assert.Single(Loader().Load([Config("line_")]));
 [Fact] public void File_path_comparison_follows_the_operating_system() {
  if(OperatingSystem.IsWindows()){var connectors=Loader().Load([Config(),Config("line_b","TESTCONNECTOR.DLL")]);Assert.Same(connectors[0].GetType().Assembly,connectors[1].GetType().Assembly);}
  else Assert.Throws<ConnectorStartupException>(()=>Loader().Load([Config(assembly:"TESTCONNECTOR.DLL")]));
 }
 [Fact] public void An_assembly_with_no_factory_fails()=>Assert.Throws<ConnectorStartupException>(()=>Loader().Load([Config(assembly:"Saintber.Assistant.Abstractions.dll")]));
 [Fact] public void Disabled_does_not_load_missing_dll()=>Assert.Empty(Loader().Load([Config(assembly:"absent.dll",enabled:false)]));
 [Theory] [InlineData("Line-Main")] [InlineData("1line")] [InlineData("line__main")] [InlineData("abcdefghijklmnopqrstuvwxyzabcdefg")]
 public void Invalid_id_fails(string id) {var e=Assert.Throws<ConnectorStartupException>(()=>Loader().Load([Config(id)])); Assert.Contains(id,e.Message); Assert.DoesNotContain("secret-value",e.ToString());}
 [Theory] [InlineData("../outside.dll")] [InlineData("absent.dll")]
 public void Missing_and_escaped_path_fail_safely(string path) {var e=Assert.Throws<ConnectorStartupException>(()=>Loader().Load([Config(assembly:path)])); Assert.Contains(Path.GetFileName(path),e.Message); Assert.DoesNotContain("secret-value",e.ToString());}
 [Fact] public void Duplicate_ids_fail()=>Assert.Throws<ConnectorStartupException>(()=>Loader().Load([Config(),Config()]));
 [Fact] public void No_factory_fails_safely() {var e=Assert.Throws<ConnectorStartupException>(()=>Loader().Load([Config(type:"unknown")])); Assert.DoesNotContain("secret-value",e.ToString());}
 [Fact] public void Creation_failure_is_sanitized() {var cfg=Config() with {Settings=new Dictionary<string,string>{{"Throw","secret-value"}}}; var e=Assert.Throws<ConnectorStartupException>(()=>Loader().Load([cfg])); Assert.Contains("line_a",e.Message); Assert.DoesNotContain("secret-value",e.ToString());}
 [Fact] public void Wrong_major_fails_before_loading_with_both_versions() {var e=Assert.Throws<ConnectorStartupException>(()=>Loader(Path.Combine(Published,"wrong")).Load([Config(assembly:"WrongConnector.dll")])); Assert.Contains("WrongConnector.dll",e.Message); Assert.Contains("2.0.0.0",e.Message); Assert.Contains("1.0.0.0",e.Message);}
 [Fact] public void Different_paths_have_different_contexts() {var connectors=Loader().Load([Config(),Config("line_b","copy/TestConnector.dll")]); Assert.NotSame(AssemblyLoadContext.GetLoadContext(connectors[0].GetType().Assembly),AssemblyLoadContext.GetLoadContext(connectors[1].GetType().Assembly));}
 [Fact] public void Parent_links_resolve_to_same_context_and_escapes_are_refused() {
  var temporary=Path.Combine(Published,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temporary);
  try {
   CreateDirectoryLink(Path.Combine(temporary,"parent"),Published);
   var connectors=Loader().Load([Config(),Config("line_b",Path.GetRelativePath(Published,Path.Combine(temporary,"parent","TestConnector.dll")))]);
   Assert.Same(connectors[0].GetType().Assembly,connectors[1].GetType().Assembly);
   CreateDirectoryLink(Path.Combine(temporary,"outside"),Path.GetDirectoryName(typeof(IConnector).Assembly.Location)!);
   Assert.Throws<ConnectorStartupException>(()=>Loader().Load([Config(assembly:Path.GetRelativePath(Published,Path.Combine(temporary,"outside","Saintber.Assistant.Abstractions.dll")))]));
  } finally {Directory.Delete(Path.Combine(temporary,"parent"));if(Directory.Exists(Path.Combine(temporary,"outside")))Directory.Delete(Path.Combine(temporary,"outside"));Directory.Delete(temporary);}
 }
 [UnixFileLinkFact] public void File_links_resolve_to_same_context_on_unix() {
  var link=Path.Combine(Published,Guid.NewGuid().ToString("N")+".dll");File.CreateSymbolicLink(link,Path.Combine(Published,"TestConnector.dll"));
  try {var connectors=Loader().Load([Config(),Config("line_b",Path.GetFileName(link))]);Assert.Same(connectors[0].GetType().Assembly,connectors[1].GetType().Assembly);}finally{File.Delete(link);}
 }
 static void CreateDirectoryLink(string link,string target) {
  if(!OperatingSystem.IsWindows()) {Directory.CreateSymbolicLink(link,target);return;}
  var start=new System.Diagnostics.ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
  start.ArgumentList.Add("/c");start.ArgumentList.Add("mklink");start.ArgumentList.Add("/J");start.ArgumentList.Add(link);start.ArgumentList.Add(target);
  using var process=System.Diagnostics.Process.Start(start)!;process.WaitForExit();Assert.Equal(0,process.ExitCode);
 }
 [Fact] public void Host_has_no_connector_or_core_references()=>Assert.DoesNotContain(typeof(ConnectorLoader).Assembly.GetReferencedAssemblies(),a=>a.Name is "Saintber.Assistant.Connectors.Core" or "Saintber.Assistant.Connectors.Line");
 sealed class CapturingHandler:IInboundMessageHandler {public InboundEnvelope? Envelope; public Task HandleAsync(InboundEnvelope e,CancellationToken ct){Envelope=e;return Task.CompletedTask;}}
}
public sealed class UnixFileLinkFactAttribute:FactAttribute {public UnixFileLinkFactAttribute(){if(OperatingSystem.IsWindows())Skip="Windows 缺少檔案 symlink 權限；父目錄 junction 已另測，檔案 symlink 未驗證。";}}
