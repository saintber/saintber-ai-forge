using System.Buffers;
using Saintber.Assistant.Abstractions;
namespace Saintber.Assistant.Host;
public static class WebhookEndpoints
{
    private const int MaximumBodyBytes = 1024 * 1024;
    public static IEndpointRouteBuilder MapAssistantWebhooks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/webhook/{instanceId}", async (string instanceId, HttpContext context) =>
        {
            var connector = context.RequestServices.GetRequiredService<IReadOnlyList<IConnector>>()
                .FirstOrDefault(item => string.Equals(item.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
            if (connector is not IWebhookReceiver receiver) return Results.StatusCode(StatusCodes.Status404NotFound);
            var request = context.Request;
            if (request.ContentLength > MaximumBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            using var body = new MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                int count;
                while ((count = await request.Body.ReadAsync(buffer.AsMemory(), request.HttpContext.RequestAborted)) != 0)
                {
                    if (body.Length + count > MaximumBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                    body.Write(buffer, 0, count);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            var headers = request.Headers.ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            var result = await receiver.ReceiveAsync(new(headers, body.ToArray()), context.RequestAborted);
            return Results.StatusCode(result.StatusCode);
        });
        return endpoints;
    }
}
