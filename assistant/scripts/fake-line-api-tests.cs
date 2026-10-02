#:property PublishAot=false
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

if (args.Length != 1) throw new Exception("Supply fake-line-api.cs path.");
var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
foreach (var argument in new[] { "run", "--file", Path.GetFullPath(args[0]), "--", "--urls", $"http://127.0.0.1:{port}" }) start.ArgumentList.Add(argument);
using var process = Process.Start(start)!;
var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(2) };
var ready = false;
try
{
    for (var attempt = 0; attempt < 200; attempt++)
    {
        if (process.HasExited) throw new Exception("Fake API exited before listening.");
        try { using var check = await client.GetAsync("/calls"); if (check.IsSuccessStatusCode) { ready = true; break; } } catch (HttpRequestException) { } catch (TaskCanceledException) { }
        await Task.Delay(100);
    }
    if (!ready) throw new Exception("Fake API did not become ready.");
    foreach (var path in new[] { "/v2/bot/message/reply", "/v2/bot/message/push", "/v2/bot/chat/loading/start" })
    {
        using var response = await client.PostAsync(path, new StringContent("{\"fake\":\"BODY-SECRET\"}", Encoding.UTF8, "application/json"));
        Equal(HttpStatusCode.OK, response.StatusCode);
    }
    using (var malformed = await client.PostAsync("/v2/bot/message/reply", new StringContent("{"))) Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    using (var unknown = await client.PostAsync("/unknown", new StringContent("{}"))) Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    using var records = JsonDocument.Parse(await client.GetStringAsync("/calls"));
    Equal(3, records.RootElement.GetArrayLength());
    var expected = new[] { "/v2/bot/message/reply", "/v2/bot/message/push", "/v2/bot/chat/loading/start" };
    for (var index = 0; index < 3; index++)
    {
        Equal(expected[index], records.RootElement[index].GetProperty("path").GetString());
        Equal("BODY-SECRET", records.RootElement[index].GetProperty("body").GetProperty("fake").GetString());
    }
    Console.WriteLine("Fake API tests: endpoints, records, invalid JSON and 404 passed.");
}
finally
{
    if (!process.HasExited) process.Kill(true);
    await process.WaitForExitAsync();
    var printed = await stdout + await stderr;
    if (printed.Contains("BODY-SECRET")) throw new Exception("Fake API logged request body.");
}
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Unexpected fake API result."); }
