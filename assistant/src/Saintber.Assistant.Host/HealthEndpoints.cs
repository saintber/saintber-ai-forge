namespace Saintber.Assistant.Host;
public static class HealthEndpoints
{
    public static void MapAssistantHealth(this WebApplication app) =>
        app.MapGet("/healthz", () => Results.Text("healthy", "text/plain"));
}
