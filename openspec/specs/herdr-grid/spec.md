# herdr-grid Specification

## Purpose

TBD - created by archiving change 'herdr-grid-layout'. Update Purpose after archive.

## Requirements

### Requirement: One pane per invocation following the grid sequence

`split` SHALL create exactly one new pane per invocation. The resulting column:row structure SHALL follow the sequence `1 → 1:1 → 2:1 → 2:2 → 2:2:1 → 2:2:2 → 3:2:2 → 3:3:2 → 3:3:3 → 3:3:3:1 → …`, where each number is the row count of a column. The next step SHALL be chosen between adding a column and adding a row to the column with the fewest rows (leftmost on ties), preferring the option with the smaller absolute difference between column count and maximum row count, and preferring adding a column on a tie.

#### Scenario: Sequence through nine panes

- **WHEN** `split` is invoked eight times on a tab that has one pane
- **THEN** the layout after each invocation SHALL be `1:1`, `2:1`, `2:2`, `2:2:1`, `2:2:2`, `3:2:2`, `3:3:2`, `3:3:3` in that order

##### Example: next step selection

| Current | Add column result | Add row result | Chosen |
| --- | --- | --- | --- |
| `1` | 2 cols, 1 row (diff 1) | 1 col, 2 rows (diff 1) | add column |
| `1:1` | 3 cols, 1 row (diff 2) | 2 cols, 2 rows (diff 0) | add row |
| `2:2` | 3 cols, 2 rows (diff 1) | 2 cols, 3 rows (diff 1) | add column |
| `3:3:3` | 4 cols, 3 rows (diff 1) | 3 cols, 4 rows (diff 1) | add column |


<!-- @trace
source: herdr-grid-layout
updated: 2026-10-02
code:
  - assistant/src/Saintber.Assistant.Connectors.Core/AssemblyInfo.cs
  - assistant/src/Saintber.Assistant.Abstractions/AssemblyInfo.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineWebhookInbound.cs
  - assistant/src/Saintber.Assistant.Host/EchoHandler.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/WebhookConnector.cs
  - docs/tools/herdr/grid.md
  - tools/herdr/README.md
  - assistant/src/Saintber.Assistant.Connectors.Line/LinePlatform.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorLoader.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/FrameworkSettings.cs
  - scripts/herdr-grid.cjs
  - assistant/src/Saintber.Assistant.Host/ConnectorSettingsValidator.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineConnectorFactory.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/PlatformContracts.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineExternalKeys.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorLifecycleService.cs
  - assistant/src/Saintber.Assistant.Host/appsettings.json
  - assistant/src/Saintber.Assistant.Host/OutboundGateway.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/Saintber.Assistant.Connectors.Core.csproj
  - assistant/src/Saintber.Assistant.Host/WebhookEndpoints.cs
  - assistant/src/Saintber.Assistant.Host/AssistantConfiguration.cs
  - assistant/global.json
  - assistant/src/Saintber.Assistant.Connectors.Line/Saintber.Assistant.Connectors.Line.csproj
  - scripts/herdr-grid-simulate.cjs
  - tools/herdr/grid-simulate.cjs
  - assistant/src/Saintber.Assistant.Abstractions/Saintber.Assistant.Abstractions.csproj
  - assistant/src/Saintber.Assistant.Host/Saintber.Assistant.Host.csproj
  - assistant/src/Saintber.Assistant.Abstractions/ExternalKey.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/AssemblyInfo.cs
  - assistant/Directory.Build.props
  - assistant/src/Saintber.Assistant.Host/Program.cs
  - tools/herdr/grid.cjs
  - assistant/src/Saintber.Assistant.Host/AssistantComposition.cs
  - CHANGELOG.md
  - assistant/src/Saintber.Assistant.Host/HealthEndpoints.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineApiClient.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/EventRegistry.cs
  - assistant/Assistant.sln
  - assistant/src/Saintber.Assistant.Abstractions/Contracts.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorComposition.cs
tests:
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLifecycleTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/Saintber.Assistant.Connectors.Core.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostWebhookTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/Saintber.Assistant.Connectors.Line.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostConfigurationTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/LineOutboundTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/TestConnector/TestConnector.csproj
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/ReplyActivityTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/LeaseOverrunTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/TestConnector/Connector.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/UnitTest1.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/FixtureDependency/FixtureDependency.csproj
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/CapabilityTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Saintber.Assistant.Host.Tests.csproj
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/FixtureDependency/FixtureText.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AdapterContractTests.cs
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/ExternalKeyTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostMessagingTests.cs
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/Saintber.Assistant.Abstractions.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongConnector/Connector.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AdditionalScenarioTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/PipelineTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/FrameworkSettingsTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/EventRegistryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongAbstractions/Marker.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongAbstractions/WrongAbstractions.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLinePublishTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostStartupProcessTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/LineWebhookInboundTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/FactoryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostHealthTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/StopCleanupTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLoadingTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongConnector/WrongConnector.csproj
-->

---
### Requirement: New columns grow one row at a time

When a new column is added, the system SHALL first split only the first row of the last column to the right (`add-column`). Panes in the remaining rows of that column SHALL be completed by later invocations (`fill`) rather than within the same invocation. While any pane spans more than one column, the next invocation SHALL be `fill`.

#### Scenario: Column added from a 2:2 layout

- **WHEN** `split` is invoked on a `2:2` layout
- **THEN** the layout SHALL become `2:2:1` and the second-row pane of the former last column SHALL span the former last column and the new column

#### Scenario: Wide pane completed on next invocation

- **WHEN** `split` is invoked on a `2:2:1` layout that contains a pane spanning two columns
- **THEN** the layout SHALL become `2:2:2` and no pane SHALL span more than one column


<!-- @trace
source: herdr-grid-layout
updated: 2026-10-02
code:
  - assistant/src/Saintber.Assistant.Connectors.Core/AssemblyInfo.cs
  - assistant/src/Saintber.Assistant.Abstractions/AssemblyInfo.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineWebhookInbound.cs
  - assistant/src/Saintber.Assistant.Host/EchoHandler.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/WebhookConnector.cs
  - docs/tools/herdr/grid.md
  - tools/herdr/README.md
  - assistant/src/Saintber.Assistant.Connectors.Line/LinePlatform.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorLoader.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/FrameworkSettings.cs
  - scripts/herdr-grid.cjs
  - assistant/src/Saintber.Assistant.Host/ConnectorSettingsValidator.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineConnectorFactory.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/PlatformContracts.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineExternalKeys.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorLifecycleService.cs
  - assistant/src/Saintber.Assistant.Host/appsettings.json
  - assistant/src/Saintber.Assistant.Host/OutboundGateway.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/Saintber.Assistant.Connectors.Core.csproj
  - assistant/src/Saintber.Assistant.Host/WebhookEndpoints.cs
  - assistant/src/Saintber.Assistant.Host/AssistantConfiguration.cs
  - assistant/global.json
  - assistant/src/Saintber.Assistant.Connectors.Line/Saintber.Assistant.Connectors.Line.csproj
  - scripts/herdr-grid-simulate.cjs
  - tools/herdr/grid-simulate.cjs
  - assistant/src/Saintber.Assistant.Abstractions/Saintber.Assistant.Abstractions.csproj
  - assistant/src/Saintber.Assistant.Host/Saintber.Assistant.Host.csproj
  - assistant/src/Saintber.Assistant.Abstractions/ExternalKey.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/AssemblyInfo.cs
  - assistant/Directory.Build.props
  - assistant/src/Saintber.Assistant.Host/Program.cs
  - tools/herdr/grid.cjs
  - assistant/src/Saintber.Assistant.Host/AssistantComposition.cs
  - CHANGELOG.md
  - assistant/src/Saintber.Assistant.Host/HealthEndpoints.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineApiClient.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/EventRegistry.cs
  - assistant/Assistant.sln
  - assistant/src/Saintber.Assistant.Abstractions/Contracts.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorComposition.cs
tests:
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLifecycleTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/Saintber.Assistant.Connectors.Core.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostWebhookTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/Saintber.Assistant.Connectors.Line.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostConfigurationTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/LineOutboundTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/TestConnector/TestConnector.csproj
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/ReplyActivityTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/LeaseOverrunTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/TestConnector/Connector.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/UnitTest1.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/FixtureDependency/FixtureDependency.csproj
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/CapabilityTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Saintber.Assistant.Host.Tests.csproj
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/FixtureDependency/FixtureText.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AdapterContractTests.cs
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/ExternalKeyTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostMessagingTests.cs
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/Saintber.Assistant.Abstractions.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongConnector/Connector.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AdditionalScenarioTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/PipelineTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/FrameworkSettingsTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/EventRegistryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongAbstractions/Marker.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongAbstractions/WrongAbstractions.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLinePublishTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostStartupProcessTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/LineWebhookInboundTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/FactoryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostHealthTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/StopCleanupTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLoadingTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongConnector/WrongConnector.csproj
-->

---
### Requirement: Row heights stay independent per column

After `fill`, every down-split node of the layout SHALL cover the width of exactly one column. Resizing a pane's row height in one column SHALL NOT change the row heights of any other column.

#### Scenario: Resizing one column after the layout is complete

- **WHEN** the layout is `2:2:2` and the row height of the second column is resized
- **THEN** the heights of panes in the first and third columns SHALL be unchanged


<!-- @trace
source: herdr-grid-layout
updated: 2026-10-02
code:
  - assistant/src/Saintber.Assistant.Connectors.Core/AssemblyInfo.cs
  - assistant/src/Saintber.Assistant.Abstractions/AssemblyInfo.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineWebhookInbound.cs
  - assistant/src/Saintber.Assistant.Host/EchoHandler.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/WebhookConnector.cs
  - docs/tools/herdr/grid.md
  - tools/herdr/README.md
  - assistant/src/Saintber.Assistant.Connectors.Line/LinePlatform.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorLoader.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/FrameworkSettings.cs
  - scripts/herdr-grid.cjs
  - assistant/src/Saintber.Assistant.Host/ConnectorSettingsValidator.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineConnectorFactory.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/PlatformContracts.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineExternalKeys.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorLifecycleService.cs
  - assistant/src/Saintber.Assistant.Host/appsettings.json
  - assistant/src/Saintber.Assistant.Host/OutboundGateway.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/Saintber.Assistant.Connectors.Core.csproj
  - assistant/src/Saintber.Assistant.Host/WebhookEndpoints.cs
  - assistant/src/Saintber.Assistant.Host/AssistantConfiguration.cs
  - assistant/global.json
  - assistant/src/Saintber.Assistant.Connectors.Line/Saintber.Assistant.Connectors.Line.csproj
  - scripts/herdr-grid-simulate.cjs
  - tools/herdr/grid-simulate.cjs
  - assistant/src/Saintber.Assistant.Abstractions/Saintber.Assistant.Abstractions.csproj
  - assistant/src/Saintber.Assistant.Host/Saintber.Assistant.Host.csproj
  - assistant/src/Saintber.Assistant.Abstractions/ExternalKey.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/AssemblyInfo.cs
  - assistant/Directory.Build.props
  - assistant/src/Saintber.Assistant.Host/Program.cs
  - tools/herdr/grid.cjs
  - assistant/src/Saintber.Assistant.Host/AssistantComposition.cs
  - CHANGELOG.md
  - assistant/src/Saintber.Assistant.Host/HealthEndpoints.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineApiClient.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/EventRegistry.cs
  - assistant/Assistant.sln
  - assistant/src/Saintber.Assistant.Abstractions/Contracts.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorComposition.cs
tests:
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLifecycleTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/Saintber.Assistant.Connectors.Core.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostWebhookTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/Saintber.Assistant.Connectors.Line.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostConfigurationTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/LineOutboundTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/TestConnector/TestConnector.csproj
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/ReplyActivityTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/LeaseOverrunTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/TestConnector/Connector.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/UnitTest1.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/FixtureDependency/FixtureDependency.csproj
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/CapabilityTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Saintber.Assistant.Host.Tests.csproj
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/FixtureDependency/FixtureText.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AdapterContractTests.cs
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/ExternalKeyTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostMessagingTests.cs
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/Saintber.Assistant.Abstractions.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongConnector/Connector.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AdditionalScenarioTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/PipelineTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/FrameworkSettingsTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/EventRegistryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongAbstractions/Marker.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongAbstractions/WrongAbstractions.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLinePublishTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostStartupProcessTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/LineWebhookInboundTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/FactoryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostHealthTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/StopCleanupTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLoadingTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongConnector/WrongConnector.csproj
-->

---
### Requirement: Fill uses a temporary tab

Because `herdr pane move` does nothing when source and target are in the same tab, `fill` SHALL move the wide pane to a temporary tab labeled `grid-tmp`, move it back below the pane directly above it in its column, split the last pane of the new column downward to create the new pane, and close the temporary tab.

#### Scenario: Move back fails

- **WHEN** moving the pane back from the temporary tab fails
- **THEN** the system SHALL keep the temporary tab, SHALL NOT create a new pane, and SHALL report an error that includes a `herdr pane move` command the user can run to restore the pane

#### Scenario: Move out fails

- **WHEN** moving the wide pane to the temporary tab fails
- **THEN** the system SHALL report the failure reason and the layout SHALL be unchanged


<!-- @trace
source: herdr-grid-layout
updated: 2026-10-02
code:
  - assistant/src/Saintber.Assistant.Connectors.Core/AssemblyInfo.cs
  - assistant/src/Saintber.Assistant.Abstractions/AssemblyInfo.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineWebhookInbound.cs
  - assistant/src/Saintber.Assistant.Host/EchoHandler.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/WebhookConnector.cs
  - docs/tools/herdr/grid.md
  - tools/herdr/README.md
  - assistant/src/Saintber.Assistant.Connectors.Line/LinePlatform.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorLoader.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/FrameworkSettings.cs
  - scripts/herdr-grid.cjs
  - assistant/src/Saintber.Assistant.Host/ConnectorSettingsValidator.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineConnectorFactory.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/PlatformContracts.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineExternalKeys.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorLifecycleService.cs
  - assistant/src/Saintber.Assistant.Host/appsettings.json
  - assistant/src/Saintber.Assistant.Host/OutboundGateway.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/Saintber.Assistant.Connectors.Core.csproj
  - assistant/src/Saintber.Assistant.Host/WebhookEndpoints.cs
  - assistant/src/Saintber.Assistant.Host/AssistantConfiguration.cs
  - assistant/global.json
  - assistant/src/Saintber.Assistant.Connectors.Line/Saintber.Assistant.Connectors.Line.csproj
  - scripts/herdr-grid-simulate.cjs
  - tools/herdr/grid-simulate.cjs
  - assistant/src/Saintber.Assistant.Abstractions/Saintber.Assistant.Abstractions.csproj
  - assistant/src/Saintber.Assistant.Host/Saintber.Assistant.Host.csproj
  - assistant/src/Saintber.Assistant.Abstractions/ExternalKey.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/AssemblyInfo.cs
  - assistant/Directory.Build.props
  - assistant/src/Saintber.Assistant.Host/Program.cs
  - tools/herdr/grid.cjs
  - assistant/src/Saintber.Assistant.Host/AssistantComposition.cs
  - CHANGELOG.md
  - assistant/src/Saintber.Assistant.Host/HealthEndpoints.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineApiClient.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/EventRegistry.cs
  - assistant/Assistant.sln
  - assistant/src/Saintber.Assistant.Abstractions/Contracts.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorComposition.cs
tests:
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLifecycleTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/Saintber.Assistant.Connectors.Core.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostWebhookTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/Saintber.Assistant.Connectors.Line.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostConfigurationTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/LineOutboundTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/TestConnector/TestConnector.csproj
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/ReplyActivityTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/LeaseOverrunTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/TestConnector/Connector.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/UnitTest1.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/FixtureDependency/FixtureDependency.csproj
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/CapabilityTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Saintber.Assistant.Host.Tests.csproj
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/FixtureDependency/FixtureText.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AdapterContractTests.cs
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/ExternalKeyTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostMessagingTests.cs
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/Saintber.Assistant.Abstractions.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongConnector/Connector.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AdditionalScenarioTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/PipelineTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/FrameworkSettingsTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/EventRegistryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongAbstractions/Marker.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongAbstractions/WrongAbstractions.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLinePublishTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostStartupProcessTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/LineWebhookInboundTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/FactoryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostHealthTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/StopCleanupTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLoadingTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongConnector/WrongConnector.csproj
-->

---
### Requirement: Panes are reordered in row-major order

When the existing layout is compatible, the system SHALL place the new pane last and rearrange all panes with `herdr pane swap` into row-major order (first row left to right, then second row, and so on). A layout is compatible when its column:row string equals the shape the sequence produces for the current pane count.

#### Scenario: Fifth pane opened

- **WHEN** a compatible `2:2` layout with panes ordered 1,2 / 3,4 receives a fifth pane
- **THEN** the final layout SHALL be `2:2:1` with the first row ordered 1,2,3 and the second row ordered 4,5

#### Scenario: Incompatible layout

- **WHEN** the existing layout does not equal the sequence shape for its pane count
- **THEN** the system SHALL skip reordering and report the reason in `reorder_skipped`

#### Scenario: Reordering disabled

- **WHEN** `--no-reorder` is supplied
- **THEN** the system SHALL NOT call `herdr pane swap`


<!-- @trace
source: herdr-grid-layout
updated: 2026-10-02
code:
  - assistant/src/Saintber.Assistant.Connectors.Core/AssemblyInfo.cs
  - assistant/src/Saintber.Assistant.Abstractions/AssemblyInfo.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineWebhookInbound.cs
  - assistant/src/Saintber.Assistant.Host/EchoHandler.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/WebhookConnector.cs
  - docs/tools/herdr/grid.md
  - tools/herdr/README.md
  - assistant/src/Saintber.Assistant.Connectors.Line/LinePlatform.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorLoader.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/FrameworkSettings.cs
  - scripts/herdr-grid.cjs
  - assistant/src/Saintber.Assistant.Host/ConnectorSettingsValidator.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineConnectorFactory.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/PlatformContracts.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineExternalKeys.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorLifecycleService.cs
  - assistant/src/Saintber.Assistant.Host/appsettings.json
  - assistant/src/Saintber.Assistant.Host/OutboundGateway.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/Saintber.Assistant.Connectors.Core.csproj
  - assistant/src/Saintber.Assistant.Host/WebhookEndpoints.cs
  - assistant/src/Saintber.Assistant.Host/AssistantConfiguration.cs
  - assistant/global.json
  - assistant/src/Saintber.Assistant.Connectors.Line/Saintber.Assistant.Connectors.Line.csproj
  - scripts/herdr-grid-simulate.cjs
  - tools/herdr/grid-simulate.cjs
  - assistant/src/Saintber.Assistant.Abstractions/Saintber.Assistant.Abstractions.csproj
  - assistant/src/Saintber.Assistant.Host/Saintber.Assistant.Host.csproj
  - assistant/src/Saintber.Assistant.Abstractions/ExternalKey.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/AssemblyInfo.cs
  - assistant/Directory.Build.props
  - assistant/src/Saintber.Assistant.Host/Program.cs
  - tools/herdr/grid.cjs
  - assistant/src/Saintber.Assistant.Host/AssistantComposition.cs
  - CHANGELOG.md
  - assistant/src/Saintber.Assistant.Host/HealthEndpoints.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineApiClient.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/EventRegistry.cs
  - assistant/Assistant.sln
  - assistant/src/Saintber.Assistant.Abstractions/Contracts.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorComposition.cs
tests:
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLifecycleTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/Saintber.Assistant.Connectors.Core.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostWebhookTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/Saintber.Assistant.Connectors.Line.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostConfigurationTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/LineOutboundTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/TestConnector/TestConnector.csproj
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/ReplyActivityTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/LeaseOverrunTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/TestConnector/Connector.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/UnitTest1.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/FixtureDependency/FixtureDependency.csproj
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/CapabilityTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Saintber.Assistant.Host.Tests.csproj
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/FixtureDependency/FixtureText.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AdapterContractTests.cs
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/ExternalKeyTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostMessagingTests.cs
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/Saintber.Assistant.Abstractions.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongConnector/Connector.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AdditionalScenarioTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/PipelineTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/FrameworkSettingsTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/EventRegistryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongAbstractions/Marker.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongAbstractions/WrongAbstractions.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLinePublishTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostStartupProcessTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/LineWebhookInboundTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/FactoryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostHealthTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/StopCleanupTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLoadingTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongConnector/WrongConnector.csproj
-->

---
### Requirement: Optional pane name

`split` SHALL accept an optional `--name <name>`. When supplied, the system SHALL rename the new pane to that name after it is created. When omitted, the pane SHALL keep its default name.

#### Scenario: Name supplied

- **WHEN** `split --name reviewer` creates pane `w3:p9`
- **THEN** the system SHALL run `herdr pane rename w3:p9 reviewer` and include `name` in its JSON output


<!-- @trace
source: herdr-grid-layout
updated: 2026-10-02
code:
  - assistant/src/Saintber.Assistant.Connectors.Core/AssemblyInfo.cs
  - assistant/src/Saintber.Assistant.Abstractions/AssemblyInfo.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineWebhookInbound.cs
  - assistant/src/Saintber.Assistant.Host/EchoHandler.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/WebhookConnector.cs
  - docs/tools/herdr/grid.md
  - tools/herdr/README.md
  - assistant/src/Saintber.Assistant.Connectors.Line/LinePlatform.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorLoader.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/FrameworkSettings.cs
  - scripts/herdr-grid.cjs
  - assistant/src/Saintber.Assistant.Host/ConnectorSettingsValidator.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineConnectorFactory.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/PlatformContracts.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineExternalKeys.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorLifecycleService.cs
  - assistant/src/Saintber.Assistant.Host/appsettings.json
  - assistant/src/Saintber.Assistant.Host/OutboundGateway.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/Saintber.Assistant.Connectors.Core.csproj
  - assistant/src/Saintber.Assistant.Host/WebhookEndpoints.cs
  - assistant/src/Saintber.Assistant.Host/AssistantConfiguration.cs
  - assistant/global.json
  - assistant/src/Saintber.Assistant.Connectors.Line/Saintber.Assistant.Connectors.Line.csproj
  - scripts/herdr-grid-simulate.cjs
  - tools/herdr/grid-simulate.cjs
  - assistant/src/Saintber.Assistant.Abstractions/Saintber.Assistant.Abstractions.csproj
  - assistant/src/Saintber.Assistant.Host/Saintber.Assistant.Host.csproj
  - assistant/src/Saintber.Assistant.Abstractions/ExternalKey.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/AssemblyInfo.cs
  - assistant/Directory.Build.props
  - assistant/src/Saintber.Assistant.Host/Program.cs
  - tools/herdr/grid.cjs
  - assistant/src/Saintber.Assistant.Host/AssistantComposition.cs
  - CHANGELOG.md
  - assistant/src/Saintber.Assistant.Host/HealthEndpoints.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineApiClient.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/EventRegistry.cs
  - assistant/Assistant.sln
  - assistant/src/Saintber.Assistant.Abstractions/Contracts.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorComposition.cs
tests:
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLifecycleTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/Saintber.Assistant.Connectors.Core.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostWebhookTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/Saintber.Assistant.Connectors.Line.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostConfigurationTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/LineOutboundTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/TestConnector/TestConnector.csproj
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/ReplyActivityTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/LeaseOverrunTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/TestConnector/Connector.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/UnitTest1.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/FixtureDependency/FixtureDependency.csproj
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/CapabilityTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Saintber.Assistant.Host.Tests.csproj
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/FixtureDependency/FixtureText.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AdapterContractTests.cs
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/ExternalKeyTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostMessagingTests.cs
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/Saintber.Assistant.Abstractions.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongConnector/Connector.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AdditionalScenarioTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/PipelineTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/FrameworkSettingsTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/EventRegistryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongAbstractions/Marker.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongAbstractions/WrongAbstractions.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLinePublishTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostStartupProcessTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/LineWebhookInboundTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/FactoryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostHealthTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/StopCleanupTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLoadingTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongConnector/WrongConnector.csproj
-->

---
### Requirement: Equal distribution after creation

After a new pane is created and reordered, the system SHALL resize all split lines so that columns share the width equally and rows within each column share the height equally, unless `--no-equalize` is supplied. The `equalize` subcommand SHALL perform the same adjustment on demand, and with `--dry-run` SHALL print the planned resize steps without applying them.

#### Scenario: Equalize skipped

- **WHEN** `--no-equalize` is supplied
- **THEN** the system SHALL NOT call `herdr pane resize`


<!-- @trace
source: herdr-grid-layout
updated: 2026-10-02
code:
  - assistant/src/Saintber.Assistant.Connectors.Core/AssemblyInfo.cs
  - assistant/src/Saintber.Assistant.Abstractions/AssemblyInfo.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineWebhookInbound.cs
  - assistant/src/Saintber.Assistant.Host/EchoHandler.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/WebhookConnector.cs
  - docs/tools/herdr/grid.md
  - tools/herdr/README.md
  - assistant/src/Saintber.Assistant.Connectors.Line/LinePlatform.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorLoader.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/FrameworkSettings.cs
  - scripts/herdr-grid.cjs
  - assistant/src/Saintber.Assistant.Host/ConnectorSettingsValidator.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineConnectorFactory.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/PlatformContracts.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineExternalKeys.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorLifecycleService.cs
  - assistant/src/Saintber.Assistant.Host/appsettings.json
  - assistant/src/Saintber.Assistant.Host/OutboundGateway.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/Saintber.Assistant.Connectors.Core.csproj
  - assistant/src/Saintber.Assistant.Host/WebhookEndpoints.cs
  - assistant/src/Saintber.Assistant.Host/AssistantConfiguration.cs
  - assistant/global.json
  - assistant/src/Saintber.Assistant.Connectors.Line/Saintber.Assistant.Connectors.Line.csproj
  - scripts/herdr-grid-simulate.cjs
  - tools/herdr/grid-simulate.cjs
  - assistant/src/Saintber.Assistant.Abstractions/Saintber.Assistant.Abstractions.csproj
  - assistant/src/Saintber.Assistant.Host/Saintber.Assistant.Host.csproj
  - assistant/src/Saintber.Assistant.Abstractions/ExternalKey.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/AssemblyInfo.cs
  - assistant/Directory.Build.props
  - assistant/src/Saintber.Assistant.Host/Program.cs
  - tools/herdr/grid.cjs
  - assistant/src/Saintber.Assistant.Host/AssistantComposition.cs
  - CHANGELOG.md
  - assistant/src/Saintber.Assistant.Host/HealthEndpoints.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineApiClient.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/EventRegistry.cs
  - assistant/Assistant.sln
  - assistant/src/Saintber.Assistant.Abstractions/Contracts.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorComposition.cs
tests:
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLifecycleTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/Saintber.Assistant.Connectors.Core.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostWebhookTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/Saintber.Assistant.Connectors.Line.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostConfigurationTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/LineOutboundTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/TestConnector/TestConnector.csproj
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/ReplyActivityTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/LeaseOverrunTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/TestConnector/Connector.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/UnitTest1.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/FixtureDependency/FixtureDependency.csproj
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/CapabilityTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Saintber.Assistant.Host.Tests.csproj
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/FixtureDependency/FixtureText.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AdapterContractTests.cs
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/ExternalKeyTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostMessagingTests.cs
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/Saintber.Assistant.Abstractions.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongConnector/Connector.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AdditionalScenarioTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/PipelineTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/FrameworkSettingsTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/EventRegistryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongAbstractions/Marker.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongAbstractions/WrongAbstractions.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLinePublishTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostStartupProcessTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/LineWebhookInboundTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/FactoryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostHealthTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/StopCleanupTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLoadingTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongConnector/WrongConnector.csproj
-->

---
### Requirement: Workspace and tab resolution

The system SHALL accept `--workspace` as a workspace id or label, and `--tab` as `<workspace_id>:<tab_id>`, `<workspace id or label>:<tab id or label>`, or a bare tab id, label, or number when `--workspace` is also given. Matching SHALL try id first, then label (case-insensitive), then tab number. When both are given they SHALL resolve to the same workspace. With only `--workspace`, the active tab SHALL be used, falling back to the first tab. Resolution failures SHALL name whether the workspace or the tab was not found and list the available candidates.

#### Scenario: Tab by label

- **WHEN** `--workspace Personal --tab 2` is supplied and workspace `Personal` has a tab numbered 2
- **THEN** the system SHALL operate on that tab

#### Scenario: Conflicting workspace

- **WHEN** `--tab Main:Company` and `--workspace Personal` resolve to different workspaces
- **THEN** the system SHALL fail with an error naming both workspaces


<!-- @trace
source: herdr-grid-layout
updated: 2026-10-02
code:
  - assistant/src/Saintber.Assistant.Connectors.Core/AssemblyInfo.cs
  - assistant/src/Saintber.Assistant.Abstractions/AssemblyInfo.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineWebhookInbound.cs
  - assistant/src/Saintber.Assistant.Host/EchoHandler.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/WebhookConnector.cs
  - docs/tools/herdr/grid.md
  - tools/herdr/README.md
  - assistant/src/Saintber.Assistant.Connectors.Line/LinePlatform.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorLoader.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/FrameworkSettings.cs
  - scripts/herdr-grid.cjs
  - assistant/src/Saintber.Assistant.Host/ConnectorSettingsValidator.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineConnectorFactory.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/PlatformContracts.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineExternalKeys.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorLifecycleService.cs
  - assistant/src/Saintber.Assistant.Host/appsettings.json
  - assistant/src/Saintber.Assistant.Host/OutboundGateway.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/Saintber.Assistant.Connectors.Core.csproj
  - assistant/src/Saintber.Assistant.Host/WebhookEndpoints.cs
  - assistant/src/Saintber.Assistant.Host/AssistantConfiguration.cs
  - assistant/global.json
  - assistant/src/Saintber.Assistant.Connectors.Line/Saintber.Assistant.Connectors.Line.csproj
  - scripts/herdr-grid-simulate.cjs
  - tools/herdr/grid-simulate.cjs
  - assistant/src/Saintber.Assistant.Abstractions/Saintber.Assistant.Abstractions.csproj
  - assistant/src/Saintber.Assistant.Host/Saintber.Assistant.Host.csproj
  - assistant/src/Saintber.Assistant.Abstractions/ExternalKey.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/AssemblyInfo.cs
  - assistant/Directory.Build.props
  - assistant/src/Saintber.Assistant.Host/Program.cs
  - tools/herdr/grid.cjs
  - assistant/src/Saintber.Assistant.Host/AssistantComposition.cs
  - CHANGELOG.md
  - assistant/src/Saintber.Assistant.Host/HealthEndpoints.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineApiClient.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/EventRegistry.cs
  - assistant/Assistant.sln
  - assistant/src/Saintber.Assistant.Abstractions/Contracts.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorComposition.cs
tests:
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLifecycleTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/Saintber.Assistant.Connectors.Core.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostWebhookTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/Saintber.Assistant.Connectors.Line.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostConfigurationTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/LineOutboundTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/TestConnector/TestConnector.csproj
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/ReplyActivityTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/LeaseOverrunTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/TestConnector/Connector.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/UnitTest1.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/FixtureDependency/FixtureDependency.csproj
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/CapabilityTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Saintber.Assistant.Host.Tests.csproj
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/FixtureDependency/FixtureText.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AdapterContractTests.cs
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/ExternalKeyTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostMessagingTests.cs
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/Saintber.Assistant.Abstractions.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongConnector/Connector.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AdditionalScenarioTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/PipelineTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/FrameworkSettingsTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/EventRegistryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongAbstractions/Marker.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongAbstractions/WrongAbstractions.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLinePublishTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostStartupProcessTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/LineWebhookInboundTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/FactoryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostHealthTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/StopCleanupTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLoadingTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongConnector/WrongConnector.csproj
-->

---
### Requirement: Inspection and dry-run subcommands

`layout` SHALL print the current column:row structure, the pane ids per column, and whether the layout is reorder-compatible. `next` SHALL print the planned step (mode, target pane, direction, ratio, and for `fill` the pane to move and its anchor) without changing anything. `split --dry-run` SHALL print the plan and SHALL NOT call any mutating Herdr command.

#### Scenario: Dry-run split

- **WHEN** `split --dry-run` is invoked
- **THEN** the layout of the tab SHALL be unchanged and no pane SHALL be created


<!-- @trace
source: herdr-grid-layout
updated: 2026-10-02
code:
  - assistant/src/Saintber.Assistant.Connectors.Core/AssemblyInfo.cs
  - assistant/src/Saintber.Assistant.Abstractions/AssemblyInfo.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineWebhookInbound.cs
  - assistant/src/Saintber.Assistant.Host/EchoHandler.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/WebhookConnector.cs
  - docs/tools/herdr/grid.md
  - tools/herdr/README.md
  - assistant/src/Saintber.Assistant.Connectors.Line/LinePlatform.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorLoader.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/FrameworkSettings.cs
  - scripts/herdr-grid.cjs
  - assistant/src/Saintber.Assistant.Host/ConnectorSettingsValidator.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineConnectorFactory.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/PlatformContracts.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineExternalKeys.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorLifecycleService.cs
  - assistant/src/Saintber.Assistant.Host/appsettings.json
  - assistant/src/Saintber.Assistant.Host/OutboundGateway.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/Saintber.Assistant.Connectors.Core.csproj
  - assistant/src/Saintber.Assistant.Host/WebhookEndpoints.cs
  - assistant/src/Saintber.Assistant.Host/AssistantConfiguration.cs
  - assistant/global.json
  - assistant/src/Saintber.Assistant.Connectors.Line/Saintber.Assistant.Connectors.Line.csproj
  - scripts/herdr-grid-simulate.cjs
  - tools/herdr/grid-simulate.cjs
  - assistant/src/Saintber.Assistant.Abstractions/Saintber.Assistant.Abstractions.csproj
  - assistant/src/Saintber.Assistant.Host/Saintber.Assistant.Host.csproj
  - assistant/src/Saintber.Assistant.Abstractions/ExternalKey.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/AssemblyInfo.cs
  - assistant/Directory.Build.props
  - assistant/src/Saintber.Assistant.Host/Program.cs
  - tools/herdr/grid.cjs
  - assistant/src/Saintber.Assistant.Host/AssistantComposition.cs
  - CHANGELOG.md
  - assistant/src/Saintber.Assistant.Host/HealthEndpoints.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineApiClient.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/EventRegistry.cs
  - assistant/Assistant.sln
  - assistant/src/Saintber.Assistant.Abstractions/Contracts.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorComposition.cs
tests:
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLifecycleTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/Saintber.Assistant.Connectors.Core.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostWebhookTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/Saintber.Assistant.Connectors.Line.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostConfigurationTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/LineOutboundTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/TestConnector/TestConnector.csproj
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/ReplyActivityTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/LeaseOverrunTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/TestConnector/Connector.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/UnitTest1.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/FixtureDependency/FixtureDependency.csproj
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/CapabilityTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Saintber.Assistant.Host.Tests.csproj
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/FixtureDependency/FixtureText.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AdapterContractTests.cs
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/ExternalKeyTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostMessagingTests.cs
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/Saintber.Assistant.Abstractions.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongConnector/Connector.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AdditionalScenarioTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/PipelineTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/FrameworkSettingsTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/EventRegistryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongAbstractions/Marker.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongAbstractions/WrongAbstractions.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLinePublishTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostStartupProcessTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/LineWebhookInboundTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/FactoryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostHealthTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/StopCleanupTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLoadingTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongConnector/WrongConnector.csproj
-->

---
### Requirement: Required working directory and focus behavior

`split` SHALL require `--cwd` and SHALL fail with a message naming it when missing. New panes SHALL NOT take focus unless `--focus` is supplied.

#### Scenario: Missing cwd

- **WHEN** `split` is invoked without `--cwd`
- **THEN** the system SHALL fail with an error that mentions `--cwd`

<!-- @trace
source: herdr-grid-layout
updated: 2026-10-02
code:
  - assistant/src/Saintber.Assistant.Connectors.Core/AssemblyInfo.cs
  - assistant/src/Saintber.Assistant.Abstractions/AssemblyInfo.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineWebhookInbound.cs
  - assistant/src/Saintber.Assistant.Host/EchoHandler.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/WebhookConnector.cs
  - docs/tools/herdr/grid.md
  - tools/herdr/README.md
  - assistant/src/Saintber.Assistant.Connectors.Line/LinePlatform.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorLoader.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/FrameworkSettings.cs
  - scripts/herdr-grid.cjs
  - assistant/src/Saintber.Assistant.Host/ConnectorSettingsValidator.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineConnectorFactory.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/PlatformContracts.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineExternalKeys.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorLifecycleService.cs
  - assistant/src/Saintber.Assistant.Host/appsettings.json
  - assistant/src/Saintber.Assistant.Host/OutboundGateway.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/Saintber.Assistant.Connectors.Core.csproj
  - assistant/src/Saintber.Assistant.Host/WebhookEndpoints.cs
  - assistant/src/Saintber.Assistant.Host/AssistantConfiguration.cs
  - assistant/global.json
  - assistant/src/Saintber.Assistant.Connectors.Line/Saintber.Assistant.Connectors.Line.csproj
  - scripts/herdr-grid-simulate.cjs
  - tools/herdr/grid-simulate.cjs
  - assistant/src/Saintber.Assistant.Abstractions/Saintber.Assistant.Abstractions.csproj
  - assistant/src/Saintber.Assistant.Host/Saintber.Assistant.Host.csproj
  - assistant/src/Saintber.Assistant.Abstractions/ExternalKey.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/AssemblyInfo.cs
  - assistant/Directory.Build.props
  - assistant/src/Saintber.Assistant.Host/Program.cs
  - tools/herdr/grid.cjs
  - assistant/src/Saintber.Assistant.Host/AssistantComposition.cs
  - CHANGELOG.md
  - assistant/src/Saintber.Assistant.Host/HealthEndpoints.cs
  - assistant/src/Saintber.Assistant.Connectors.Line/LineApiClient.cs
  - assistant/src/Saintber.Assistant.Connectors.Core/EventRegistry.cs
  - assistant/Assistant.sln
  - assistant/src/Saintber.Assistant.Abstractions/Contracts.cs
  - assistant/src/Saintber.Assistant.Host/ConnectorComposition.cs
tests:
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLifecycleTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/Saintber.Assistant.Connectors.Core.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostWebhookTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/Saintber.Assistant.Connectors.Line.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostConfigurationTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/LineOutboundTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/TestConnector/TestConnector.csproj
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/ReplyActivityTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/LeaseOverrunTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/TestConnector/Connector.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/UnitTest1.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/FixtureDependency/FixtureDependency.csproj
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/CapabilityTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Saintber.Assistant.Host.Tests.csproj
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/AssemblyBoundaryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/FixtureDependency/FixtureText.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AdapterContractTests.cs
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/ExternalKeyTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostMessagingTests.cs
  - assistant/tests/Saintber.Assistant.Abstractions.Tests/Saintber.Assistant.Abstractions.Tests.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongConnector/Connector.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/AdditionalScenarioTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/PipelineTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/FrameworkSettingsTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/EventRegistryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongAbstractions/Marker.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongAbstractions/WrongAbstractions.csproj
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLinePublishTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostStartupProcessTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/LineWebhookInboundTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Line.Tests/FactoryTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostHealthTests.cs
  - assistant/tests/Saintber.Assistant.Connectors.Core.Tests/StopCleanupTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/HostLoadingTests.cs
  - assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/WrongConnector/WrongConnector.csproj
-->