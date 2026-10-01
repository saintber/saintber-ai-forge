## Purpose

The Assistant host is the long-running HTTP process that loads connectors from configuration, exposes a generic webhook route, and connects connectors to the message handler through two interfaces: inbound handling and outbound sending. It owns transport, composition, and configuration, and holds no platform-specific or business logic.

## ADDED Requirements

### Requirement: Connector loading from configuration

The host SHALL read a list of connector instances from configuration, each with a type, an instance id, an assembly path relative to the configured connectors directory (default `connectors`), and string settings. At startup the host SHALL load each assembly in its own load context, SHALL share the `Saintber.Assistant.Abstractions` and `Microsoft.Extensions.Logging.Abstractions` assemblies from the host, SHALL locate the `IConnectorFactory` whose connector type matches, create the connector, and start it. The host SHALL refuse to load any path that resolves outside the connectors directory. The host SHALL check that the `Saintber.Assistant.Abstractions` version referenced by each connector assembly has the same major version as the host. The host SHALL fail startup, with a non-zero exit status, when an assembly cannot be loaded, the major version differs, no matching factory exists, an instance id is duplicated, or connector creation fails, and the error MUST name the file, instance id, versions, or missing setting keys and MUST NOT print any setting value. Loading and unloading while the process runs is not supported. A connector's load unit is its published output folder (assembly, `.deps.json`, and its own dependencies).

#### Scenario: Connector loaded from configuration

- **WHEN** the configuration lists a `line` connector whose assembly is present under the connectors directory
- **THEN** the host creates and starts it and the instance accepts requests on its webhook route

#### Scenario: Shared types

- **WHEN** a loaded connector hands an inbound envelope to the host's handler
- **THEN** the envelope type is identical to the host's own type and the call succeeds without conversion

#### Scenario: Path escape refused

- **WHEN** a configured assembly path is `../outside.dll`
- **THEN** startup fails with an error naming the path and nothing is loaded from outside the connectors directory

#### Scenario: Duplicate instance ids

- **WHEN** two configured instances share the same instance id
- **THEN** startup fails with an error naming the instance id

#### Scenario: Missing assembly

- **WHEN** the configured assembly file does not exist
- **THEN** startup fails with an error naming the file and no setting values

#### Scenario: Loaded from a clean published folder

- **WHEN** the connector is loaded from the output of `dotnet publish` for the LINE connector, without any test-project references available
- **THEN** loading succeeds and the connector accepts requests

#### Scenario: Incompatible contract version

- **WHEN** a connector assembly references a different major version of the abstractions assembly than the host
- **THEN** startup fails with an error naming the file and both versions

#### Scenario: Connector stopped on shutdown

- **WHEN** the host shuts down gracefully
- **THEN** each started connector's stop operation is awaited once before the process exits

### Requirement: Webhook route

The host SHALL expose `POST /webhook/{instanceId}` that passes the raw request body bytes and headers to the connector instance named in the route, provided it implements the webhook receiver interface, and SHALL respond with the status code the connector returns. The host SHALL respond 404 when the instance id is unknown or the connector is not a webhook receiver, and 413 without invoking the connector when the body exceeds 1 MiB. The host MUST NOT inspect or interpret signatures or platform payloads.

#### Scenario: Valid request

- **WHEN** a correctly signed LINE webhook is posted to `/webhook/line`
- **THEN** the response is 200 without waiting for any handler or platform call to finish

#### Scenario: Bad signature

- **WHEN** a webhook with an incorrect signature is posted
- **THEN** the response is 401 and the handler is not invoked

#### Scenario: Malformed payload

- **WHEN** a correctly signed body that is not valid JSON is posted
- **THEN** the response is 400

#### Scenario: Unknown instance

- **WHEN** a request is posted to `/webhook/unknown`
- **THEN** the response is 404

#### Scenario: Oversized body

- **WHEN** a body larger than 1 MiB is posted
- **THEN** the response is 413 and the connector is not invoked

### Requirement: Inbound message handler seam

The host SHALL register one implementation of `IInboundMessageHandler` and SHALL pass it to every connector at start. The handler contract SHALL state that the handler must return within the cancellation deadline it is given, and that long-running work must be queued rather than run inline.

#### Scenario: Handler receives envelopes

- **WHEN** a connector delivers an inbound envelope
- **THEN** the host's handler is invoked with that envelope

#### Scenario: Handler honors the deadline

- **WHEN** the cancellation token passed to the handler is cancelled
- **THEN** the echo handler returns promptly without sending

### Requirement: Outbound gateway

The host SHALL provide `IOutboundGateway.SendAsync` that routes an outbound envelope to the connector identified by its connector type and instance id by calling that connector's delivery operation. The result SHALL mean the message was accepted for delivery, not that it was delivered: the method SHALL return `Accepted` when the target connector exists and `UnknownConnector` otherwise, and SHALL NOT throw because of a platform delivery failure, which is logged.

#### Scenario: Routed to the right connector

- **WHEN** two connector instances are loaded and an envelope targets the second
- **THEN** only the second connector's delivery operation is called

#### Scenario: Unknown target

- **WHEN** an envelope targets an instance id that is not loaded
- **THEN** the result is `UnknownConnector` and no connector is called

#### Scenario: Delivery failure is contained

- **WHEN** the target connector's delivery operation throws
- **THEN** the exception is logged, the result is `Accepted`, and no exception reaches the caller

### Requirement: Echo handler

The host SHALL register an echo handler as the default `IInboundMessageHandler` that, for each text envelope, sends through the outbound gateway an envelope addressed to the same connector instance and chat, in reply to the received message, with the text `Echo: ` followed by the original text. The echo handler SHALL read `Assistant:Echo:Mode`, whose value is `reply` (default) or `push`; in `push` mode the envelope carries no in-reply-to message so that delivery uses the platform push path. Any other mode value SHALL fail startup with an error naming the setting. The echo handler SHALL NOT apply any platform length limit.

#### Scenario: Echo reply

- **WHEN** a user sends the text `hi`
- **THEN** the gateway receives one envelope with text `Echo: hi`, the same chat, and in-reply-to equal to the received message key

#### Scenario: Push mode

- **WHEN** `Assistant:Echo:Mode` is `push` and a user sends the text `hi`
- **THEN** the gateway receives an envelope with text `Echo: hi`, the same chat, and no in-reply-to message

#### Scenario: Invalid mode

- **WHEN** `Assistant:Echo:Mode` is `broadcast`
- **THEN** startup fails with an error naming `Assistant:Echo:Mode`

#### Scenario: Echo of long text

- **WHEN** the original text is 5000 characters
- **THEN** the envelope text is `Echo: ` followed by all 5000 characters

### Requirement: Configuration and secret handling

The host SHALL read the connectors directory from `Assistant:ConnectorsPath` (default `connectors`), the echo mode from `Assistant:Echo:Mode`, and the connector list from the `Connectors` configuration section, including environment variables in the form `Connectors__0__Settings__ChannelSecret`. Required settings are validated by each connector's factory. A startup error caused by configuration SHALL name the missing keys and MUST NOT print any configuration value.

#### Scenario: Missing secret

- **WHEN** the host starts with a `line` connector lacking `ChannelSecret`
- **THEN** startup fails with an error naming `ChannelSecret` and the process exits with a non-zero status

#### Scenario: Values are never printed

- **WHEN** startup fails because the access token is missing while the channel secret is set
- **THEN** the output does not contain the channel secret value

#### Scenario: Environment variable form

- **WHEN** the connector settings are supplied only as `Connectors__0__...` environment variables
- **THEN** the connector is created with those values

### Requirement: Health endpoint

The host SHALL expose `GET /healthz` that responds 200 with a fixed body and no configuration values, without contacting any external service.

#### Scenario: Healthy

- **WHEN** `GET /healthz` is requested while the host is running
- **THEN** the response status is 200 and the body contains no secret, token, or configuration value

### Requirement: Dependency direction

The `Abstractions` assembly SHALL reference no other Assistant assembly and no external package except `Microsoft.Extensions.Logging.Abstractions`. The `Connectors.Core` assembly SHALL reference only `Abstractions` among Assistant assemblies. The `Connectors.Line` assembly SHALL reference only `Abstractions` and `Connectors.Core` among Assistant assemblies. The `Host` assembly SHALL reference only `Abstractions` among Assistant assemblies. The rules SHALL be enforced by tests that inspect both project files (project and package references) and compiled assembly references.

#### Scenario: Rules hold

- **WHEN** the architecture tests run against the solution
- **THEN** they pass

#### Scenario: Forbidden project reference is detected

- **WHEN** a compilable fixture adds a project reference from `Connectors.Line` to a project other than `Abstractions` and `Connectors.Core`
- **THEN** the project-file rule fails

#### Scenario: Forbidden package reference is detected

- **WHEN** a compilable fixture adds a package reference to `Abstractions` other than `Microsoft.Extensions.Logging.Abstractions`
- **THEN** the project-file rule fails

#### Scenario: Host does not reference connector assemblies

- **WHEN** the compiled `Host` assembly's references are inspected
- **THEN** it references neither `Connectors.Core` nor `Connectors.Line`
