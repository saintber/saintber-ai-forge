#:property PublishAot=false
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fake-webhook.cs"));
// File-based build outputs live in temp; locate the checked-out source explicitly.
if (args.Length != 1) throw new Exception("Supply the fake-webhook.cs path.");
script = Path.GetFullPath(args[0]);
var passed = 0;
var failures = 0;
const string body = "{\"destination\":\"U0\",\"events\":[]}";
const string signature = "AQQTPDN0VEjXZIlgGdat3T+SL3wHGAG+cHc684p/XqU=";
var temp = Path.Combine(Path.GetTempPath(), "assistant-tool-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
var payload = Path.Combine(temp, "payload.json");
await File.WriteAllTextAsync(payload, body, new UTF8Encoding(false));
try
{
    await Check("fixed independent signature and HTTP 200", async () =>
    {
        var result = await Send(["test-secret", "--payload", payload], 200);
        Equal(body, result.Body); Equal(signature, result.Signature); Equal("POST", result.Method);
        Equal("/webhook/line", result.Path); Equal("application/json", result.ContentType);
        Equal(0, result.ExitCode); Equal("HTTP 200", result.Output.Trim()); Safe(result);
    });
    await Check("different secret yields HTTP 401", async () =>
    {
        var result = await Send(["wrong-secret", "--payload", payload], 401);
        Require(result.Signature != signature, "wrong secret used the valid signature");
        Equal(1, result.ExitCode); Equal("HTTP 401", result.Output.Trim()); Safe(result);
    });
    await Check("default LINE payload", async () =>
    {
        var result = await Send(["test-secret"], 200);
        using var json = JsonDocument.Parse(result.Body);
        var ev = json.RootElement.GetProperty("events")[0];
        Equal("message", ev.GetProperty("type").GetString()); Equal("active", ev.GetProperty("mode").GetString());
        Equal("user", ev.GetProperty("source").GetProperty("type").GetString());
        Equal("Uoffline", ev.GetProperty("source").GetProperty("userId").GetString());
        Equal("text", ev.GetProperty("message").GetProperty("type").GetString());
        Equal("offline test", ev.GetProperty("message").GetProperty("text").GetString());
        Require(ev.GetProperty("timestamp").GetInt64() > 0, "missing timestamp");
        foreach (var key in new[] { "webhookEventId", "replyToken" }) Require(!string.IsNullOrEmpty(ev.GetProperty(key).GetString()), "missing " + key);
        Require(!string.IsNullOrEmpty(ev.GetProperty("message").GetProperty("id").GetString()), "missing message id");
        Equal(0, result.ExitCode); Equal("HTTP 200", result.Output.Trim()); Safe(result);
    });
    await Check("custom Chinese text stays in request only", async () =>
    {
        var result = await Send(["test-secret", "測試 TEXT-SECRET"], 200);
        using var json = JsonDocument.Parse(result.Body);
        Equal("測試 TEXT-SECRET", json.RootElement.GetProperty("events")[0].GetProperty("message").GetProperty("text").GetString());
        Safe(result);
    });
    foreach (var invalid in new[] { Array.Empty<string>(), new[] { "URL-SECRET" }, new[] { "relative-SECRET", "test-secret" }, new[] { "ftp://URL-SECRET", "test-secret" }, new[] { "http://localhost:1", "" }, new[] { "http://localhost:1", "test-secret", "bad-option-SECRET", payload }, new[] { "http://localhost:1", "test-secret", "a", "b", "c" } })
        await Check("invalid CLI input is safe", async () => { var result = await Run(invalid); Equal(2, result.ExitCode); Require(result.Error.Contains("Usage:"), "no usage"); Safe(result); });
    await Check("missing payload file error is safe", async () => { var result = await Run(["http://localhost:1/webhook/line", "test-secret", "--payload", Path.Combine(temp, "PATH-SECRET")]); Equal(1, result.ExitCode); Equal("Webhook request failed.", result.Error.Trim()); Safe(result); });
    await Check("network error is safe", async () => { var result = await Run(["http://127.0.0.1:" + Port() + "/webhook/line", "test-secret", "TEXT-SECRET"]); Equal(1, result.ExitCode); Equal("Webhook request failed.", result.Error.Trim()); Safe(result); });
}
finally { Directory.Delete(temp, true); }
Console.WriteLine($"Tool tests: {passed} passed, {failures} failed.");
return failures == 0 ? 0 : 1;

async Task Check(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
async Task<Result> Send(string[] trailing, int status)
{
    var port = Port();
    using var listener = new HttpListener(); listener.Prefixes.Add($"http://localhost:{port}/"); listener.Start();
    var request = listener.GetContextAsync();
    var run = Run([$"http://localhost:{port}/webhook/line", .. trailing]);
    if (await Task.WhenAny(request, run).WaitAsync(TimeSpan.FromSeconds(45)) == run)
        throw new Exception("Tool exited before issuing HTTP: " + (await run).ExitCode);
    var context = await request;
    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
    var raw = await reader.ReadToEndAsync();
    var sig = context.Request.Headers["X-Line-Signature"] ?? "";
    var method = context.Request.HttpMethod; var path = context.Request.Url!.AbsolutePath;
    var type = context.Request.ContentType?.Split(';')[0] ?? "";
    context.Response.StatusCode = status;
    var response = Encoding.UTF8.GetBytes("RESPONSE-BODY-SECRET"); await context.Response.OutputStream.WriteAsync(response); context.Response.Close();
    var result = await run;
    return result with { Body = raw, Signature = sig, Method = method, Path = path, ContentType = type };
}
async Task<Result> Run(string[] values)
{
    var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var value in new[] { "run", "--file", script, "--" }.Concat(values)) start.ArgumentList.Add(value);
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
    catch { process.Kill(true); throw; }
    return new(process.ExitCode, await output, await error);
}
static int Port() { var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop(); return port; }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("unexpected result"); }
static void Require(bool value, string message) { if (!value) throw new Exception(message); }
static void Safe(Result result) { var printed = result.Output + result.Error; foreach (var secret in new[] { "test-secret", "wrong-secret", "TEXT-SECRET", "URL-SECRET", "PATH-SECRET", "RESPONSE-BODY-SECRET", "destination", "X-Line-Signature" }) Require(!printed.Contains(secret), "sensitive output"); }
record Result(int ExitCode, string Output, string Error, string Body = "", string Signature = "", string Method = "", string Path = "", string ContentType = "");
