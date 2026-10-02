using Saintber.Assistant.Host;
try
{
    var builder = WebApplication.CreateBuilder(args);
    using var settings = AssistantConfiguration.Load(builder.Environment.ContentRootPath);
    builder.Configuration.Sources.Clear();
    builder.Configuration.AddConfiguration(settings.Configuration);
    builder.WebHost.UseUrls(settings.Configuration["ASPNETCORE_URLS"] ?? "http://0.0.0.0:8080");
    builder.Services.AddAssistant(settings);
    var app = builder.Build();
    app.MapAssistantHealth();
    app.MapAssistantWebhooks();
    app.Run();
}
catch (HostAbortedException) { throw; }
catch (ConnectorStartupException error)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 1;
}
catch (Exception)
{
    Console.Error.WriteLine("Assistant startup failed: appsettings.json Assistant");
    Environment.ExitCode = 1;
}
public partial class Program;
