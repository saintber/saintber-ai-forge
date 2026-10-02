#:sdk Microsoft.NET.Sdk.Web
#:property PublishAot=false
using System.Collections.Concurrent;
using System.Text.Json;

// Offline fixture only: GET /calls intentionally exposes fake request bodies.
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
var app = builder.Build();
var calls = new ConcurrentQueue<Call>();
app.MapGet("/calls", () => Results.Json(calls.ToArray()));
foreach (var path in new[] { "/v2/bot/message/reply", "/v2/bot/message/push", "/v2/bot/chat/loading/start" })
{
    app.MapPost(path, async (HttpRequest request) =>
    {
        try
        {
            using var json = await JsonDocument.ParseAsync(request.Body);
            calls.Enqueue(new Call(request.Path.Value!, json.RootElement.Clone()));
            return Results.Ok();
        }
        catch (JsonException) { return Results.BadRequest(); }
    });
}
Console.WriteLine("Offline fake LINE API started; GET /calls returns fake requests.");
await app.RunAsync();
record Call(string Path, JsonElement Body);
