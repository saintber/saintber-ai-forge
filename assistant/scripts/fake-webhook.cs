#:property PublishAot=false
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length < 2 || args.Length > 4
    || !Uri.TryCreate(args[0], UriKind.Absolute, out var url)
    || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
    || string.IsNullOrEmpty(args[1]) || (args.Length == 4 && args[2] != "--payload"))
{
    Console.Error.WriteLine("Usage: dotnet run --file fake-webhook.cs -- <url> <secret> [text | --payload <json-file>]");
    return 2;
}

try
{
    var body = args.Length == 4 ? await File.ReadAllBytesAsync(args[3]) : JsonSerializer.SerializeToUtf8Bytes(new
    {
        destination = "U0",
        events = new[]
        {
            new
            {
                type = "message", mode = "active", webhookEventId = Guid.NewGuid().ToString("N"),
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), replyToken = "offline-" + Guid.NewGuid().ToString("N"),
                source = new { type = "user", userId = "Uoffline" },
                message = new { type = "text", id = "M" + Guid.NewGuid().ToString("N"), text = args.Length == 3 ? args[2] : "offline test" }
            }
        }
    });
    var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(args[1]), body));
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    using var request = new HttpRequestMessage(HttpMethod.Post, url);
    request.Headers.Add("X-Line-Signature", signature);
    request.Content = new ByteArrayContent(body);
    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    Console.WriteLine($"HTTP {(int)response.StatusCode}");
    return response.IsSuccessStatusCode ? 0 : 1;
}
catch (Exception)
{
    Console.Error.WriteLine("Webhook request failed.");
    return 1;
}
