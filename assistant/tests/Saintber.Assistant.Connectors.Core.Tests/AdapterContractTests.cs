using System.Reflection;
namespace Saintber.Assistant.Connectors.Core.Tests;
public class AdapterContractTests
{
    [Theory][InlineData("IMessagingPlatform")][InlineData("IWebhookInbound")][InlineData("PlatformInboundEvent")][InlineData("PlatformCapabilities")][InlineData("WebhookVerificationException")][InlineData("ConnectorPayloadException")]
    public void Framework_exposes_platform_contract(string name) => Assert.NotNull(Assembly.Load("Saintber.Assistant.Connectors.Core").GetType("Saintber.Assistant.Connectors.Core." + name));
}
