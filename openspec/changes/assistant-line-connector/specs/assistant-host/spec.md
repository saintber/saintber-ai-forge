## Purpose

The Assistant host is the long-running HTTP process that loads connectors from configuration, exposes a generic webhook route, and connects connectors to the message handler through two interfaces: inbound handling and outbound sending. It owns transport, composition, and configuration, and holds no platform-specific or business logic.

## ADDED Requirements

### Requirement: Connector loading from configuration

The host SHALL read a list of connector instances from configuration, each with the common fields described by the connector instance list requirement. At startup the host SHALL load, for each instance whose `Enabled` value is true, its assembly in the load context belonging to the normalized full path of that assembly, SHALL share the `Saintber.Assistant.Abstractions` and `Microsoft.Extensions.Logging.Abstractions` assemblies from the host, SHALL locate the `IConnectorFactory` whose connector type matches, create the connector, and start it. The host SHALL refuse to load any path that resolves outside the connectors directory. The host SHALL check that the `Saintber.Assistant.Abstractions` version referenced by each connector assembly has the same major version as the host. The host SHALL fail startup, with a non-zero exit status, when an assembly cannot be loaded, the major version differs, no matching factory exists, an instance id is invalid, or connector creation fails, and the error MUST name the file, instance id, versions, or missing setting keys and MUST NOT print any setting value. Loading and unloading while the process runs is not supported. A connector's load unit is its published output folder (assembly, `.deps.json`, and its own dependencies). Instances whose assemblies resolve to the same normalized path SHALL share one load context and one connector factory, with each instance created separately by calling the factory, and instances with different normalized paths SHALL use separate load contexts. The path SHALL be normalized to a full path with symbolic links resolved and compared without regard to case on Windows and with regard to case elsewhere. A connector MUST NOT keep instance state in static fields, so the instances sharing a load context stay isolated from one another.

#### Scenario: Connector loaded from configuration

- **WHEN** the configuration lists a `line` connector whose assembly is present under the connectors directory
- **THEN** the host creates and starts it and the instance accepts requests on its webhook route

#### Scenario: Same path loaded once

- **WHEN** instances `line_a` and `line_b` both name the same assembly path
- **THEN** the assembly is loaded once into one load context, the factory is discovered once, and the factory creates two separate instances

#### Scenario: Instances sharing an assembly stay isolated

- **WHEN** `line_a` and `line_b` share one load context
- **THEN** their deduplication registrations, reply contexts, workers, and leases are independent, and stopping `line_a` does not stop or affect `line_b`

#### Scenario: Different paths use separate load contexts

- **WHEN** two instances name assemblies with different normalized paths
- **THEN** each is loaded into its own load context

#### Scenario: Path normalization

- **WHEN** two instances name the same file through a symbolic link and a direct path
- **THEN** they resolve to the same normalized path and share one load context

#### Scenario: Shared types

- **WHEN** a loaded connector hands an inbound envelope to the host's handler
- **THEN** the envelope type is identical to the host's own type and the call succeeds without conversion

#### Scenario: Path escape refused

- **WHEN** a configured assembly path is `../outside.dll`
- **THEN** startup fails with an error naming the path and nothing is loaded from outside the connectors directory

#### Scenario: Invalid instance id

- **WHEN** a configured instance id is `Line-Main`, `1line`, or `line__main`
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

The host SHALL expose `POST /webhook/{instanceId}` that passes the raw request body bytes and headers to the connector instance named in the route, provided it implements the webhook receiver interface, and SHALL respond with the status code the connector returns. The host SHALL respond 404 when the instance id is unknown, the instance is disabled, or the connector is not a webhook receiver, and 413 without invoking the connector when the body exceeds 1 MiB. The host MUST NOT inspect or interpret signatures or platform payloads.

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

#### Scenario: Disabled instance

- **WHEN** a request is posted to the route of an instance whose `Enabled` value is false
- **THEN** the response is 404 and no assembly was loaded for it

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

The host SHALL provide `IOutboundGateway.SendAsync` that routes an outbound envelope to the connector identified by its connector type and instance id by calling that connector's delivery operation, and SHALL return one of three results: `Accepted` when the connector exists and its delivery operation returned accepted, meaning the connector was permitted to attempt the delivery (a later platform delivery failure is logged and does not change the result); `UnknownConnector` when no loaded, enabled connector matches; and `ConnectorUnavailable` when the connector exists but refused the delivery without calling the platform. The method SHALL NOT throw because of a platform delivery failure, which is logged. The result SHALL NOT indicate that the message was delivered.

#### Scenario: Routed to the right connector

- **WHEN** two connector instances are loaded and an envelope targets the second
- **THEN** only the second connector's delivery operation is called

#### Scenario: Unknown target

- **WHEN** an envelope targets an instance id that is not loaded or is disabled
- **THEN** the result is `UnknownConnector` and no connector is called

#### Scenario: Delivery failure is contained

- **WHEN** the target connector's delivery operation is accepted and the platform call then fails or throws
- **THEN** the exception is logged, the result is `Accepted`, and no exception reaches the caller

#### Scenario: Known connector refuses while stopping

- **WHEN** the target connector is Stopping and the caller has no valid lease
- **THEN** the result is `ConnectorUnavailable` and the platform is not called

#### Scenario: Known connector refuses after stop

- **WHEN** the target connector is Stopped
- **THEN** the result is `ConnectorUnavailable` and the platform is not called

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

### Requirement: Connector instance list and settings schema

The host SHALL treat the `Connectors` configuration as an object keyed by instance id, where each entry is one connector instance with common fields and custom fields. An instance id MUST start with a lowercase letter, contain only lowercase letters, digits, and single underscores (no consecutive underscores and no hyphens), and be at most 32 characters; ids SHALL be compared without regard to case and normalized to lowercase. The common fields are `Type`, `Enabled` (default true), `Assembly`, and an optional `DisplayName` used only for logs and the startup summary. Each entry SHALL contain only these fields and `Settings`, compared without regard to case; an unknown top-level field, a scalar where an object is required (such as `Settings`), an object where a scalar is required, or an `Enabled` value that is not a boolean MUST fail startup with an error that names the instance id and the key and not the value. The custom fields are the string, integer, duration, or boolean values under `Settings`, which hold platform-specific parameters, framework tuning parameters such as time limits and count limits, and secrets, and which are defined by the connector type through its factory's setting descriptors (key, kind, required, default, secret, minimum, maximum, description). The host SHALL read `Settings` by walking every leaf node recursively and converting it to a relative colon-separated key (for example `Work:MaxConcurrency`), and SHALL NOT bind `Settings` to a flat string dictionary. Setting keys SHALL be compared without regard to case and normalized to the descriptor's casing, and a key that is both a value and a parent of other keys MUST fail startup naming the key. A duration value SHALL be written with colons in the form `hh:mm:ss` (optionally with a day prefix or fractional seconds) and parsed with the invariant culture, and a value consisting only of digits MUST be rejected. Before creating an instance the host SHALL validate its settings against the descriptors and SHALL fail startup, with an error that names the instance id and the key but never the value, for an unknown key, a value of the wrong kind, a value outside its range, or a missing required key; rules specific to a platform SHALL be validated by that connector's factory when it creates the instance, with errors that likewise name only the key. A setting marked secret SHALL never be written to any log or error. An instance whose `Enabled` value is false SHALL NOT have its assembly loaded and SHALL NOT receive a webhook route. The configuration sources SHALL be layered with increasing precedence as the `appsettings.json` shipped with the host, an optional external JSON file named by `Assistant:ConfigFile`, and environment variables, and layering SHALL only add to and override instances and settings and SHALL NOT remove them; an instance is removed from effect only by setting `Enabled` to false. An entry that has no `Type` after all sources are merged MUST fail startup naming its instance id. `Assistant:ConfigFile` SHALL be ignored when it is not set, and startup MUST fail naming the setting key when it is set but the file does not exist or cannot be read. The host SHALL support changing the list and every setting through configuration alone, without code changes, taking effect at the next start, and SHALL log at startup a summary of each instance (type, instance id, enabled state, display name, webhook path) that contains no setting values. The host SHALL NOT expose a runtime HTTP endpoint that lists instances.

#### Scenario: Common and custom fields

- **WHEN** the configuration has an entry `line` with `Type` `line`, `Enabled` true, a display name, and settings for the channel secret, the access token, and `Work:MaxConcurrency`
- **THEN** the instance is created with those values and the startup summary lists its type, instance id, enabled state, display name, and webhook path without any setting value

#### Scenario: Misspelled common field

- **WHEN** an entry contains `Enabeld` set to false or `Asssembly` naming another file
- **THEN** startup fails with an error naming the instance id and the key and not the value, and the instance does not start with `Enabled` defaulting to true or an older assembly path

#### Scenario: Wrong shape

- **WHEN** an entry has `Settings` set to a scalar, or `Enabled` set to `maybe`
- **THEN** startup fails with an error naming the instance id and the key and not the value

#### Scenario: Nested JSON and environment variables are equivalent

- **WHEN** one source writes `{"Connectors":{"line":{"Settings":{"Work":{"MaxConcurrency":8}}}}}` and another supplies `Connectors__line__Settings__Work__MaxConcurrency=8` using the real JSON and environment variable providers
- **THEN** both yield the setting `Work:MaxConcurrency` with value 8 for the factory

#### Scenario: Misspelled leaf is rejected

- **WHEN** the settings contain the leaf `Work:MaxConcurency` that no descriptor declares
- **THEN** startup fails with an error naming the instance id and that key and containing no value

#### Scenario: Branch and leaf conflict

- **WHEN** the settings contain both `Work` with a value and `Work:MaxConcurrency`
- **THEN** startup fails with an error naming the key `Work`

#### Scenario: Keys are case-insensitive

- **WHEN** the settings use the key `work:maxconcurrency`
- **THEN** it is accepted as `Work:MaxConcurrency`

#### Scenario: Wrong kind or out of range

- **WHEN** `Work:MaxConcurrency` is `abc` or `LoadingSeconds` is 65
- **THEN** startup fails with an error naming the instance id and the key and not the value

#### Scenario: Duration written as a bare number

- **WHEN** `Timeouts:Event` is `60`
- **THEN** startup fails naming the key and not the value, and `00:01:00` is accepted

#### Scenario: Platform rule validated by the factory

- **WHEN** `LoadingSeconds` is 7
- **THEN** the host's range check passes and the factory fails creation with an error naming `LoadingSeconds`

#### Scenario: Disabled instance

- **WHEN** an instance has `Enabled` false
- **THEN** its assembly is not loaded, its webhook route does not exist, and the startup summary lists it as disabled

#### Scenario: Secret never printed

- **WHEN** startup fails for any reason while a secret setting is configured
- **THEN** neither the log nor the error contains the secret value

#### Scenario: Precedence of sources

- **WHEN** `Work:MaxConcurrency` is 4 in `appsettings.json`, 6 in the external JSON file, and 8 in an environment variable
- **THEN** the instance uses 8; and without the environment variable it uses 6; and without the external file it uses 4

#### Scenario: Layering merges by instance id

- **WHEN** `appsettings.json` defines instances `line_a` and `line_b` and the external JSON file defines only `line_a` with a changed display name
- **THEN** both instances exist and `line_b` is unchanged

#### Scenario: Reordering cannot misassign credentials

- **WHEN** the external JSON file lists `line_b` before `line_a` and an environment variable sets the secret of `line_a`
- **THEN** the secret applies to `line_a` only

#### Scenario: Removal requires disable

- **WHEN** an instance defined in `appsettings.json` is absent from the external JSON file
- **THEN** it still exists, and it is removed from effect only when `Enabled` is set to false

#### Scenario: Fragment without a type

- **WHEN** an environment variable sets `Connectors__lne__Settings__ChannelSecret` for an instance id that no source defines with a `Type`
- **THEN** startup fails with an error naming the instance id `lne`

#### Scenario: Config file not set

- **WHEN** `Assistant:ConfigFile` is not set
- **THEN** the host starts using the remaining sources

#### Scenario: Config file named but missing

- **WHEN** `Assistant:ConfigFile` is set to a file that does not exist
- **THEN** startup fails naming the key `Assistant:ConfigFile`

#### Scenario: No listing endpoint

- **WHEN** the host is running
- **THEN** it exposes no HTTP endpoint that returns the instance list or any setting

### Requirement: Configuration and secret handling

The host SHALL read the connectors directory from `Assistant:ConnectorsPath` (default `connectors`), the optional external settings file from `Assistant:ConfigFile`, the echo mode from `Assistant:Echo:Mode`, and the connector list from the `Connectors` configuration section, including environment variables in the form `Connectors__line__Settings__ChannelSecret`. Required settings are validated against each connector's setting descriptors. A startup error caused by configuration SHALL name the missing or invalid keys and MUST NOT print any configuration value.

#### Scenario: Missing secret

- **WHEN** the host starts with a `line` connector lacking `ChannelSecret`
- **THEN** startup fails with an error naming `ChannelSecret` and the process exits with a non-zero status

#### Scenario: Values are never printed

- **WHEN** startup fails because the access token is missing while the channel secret is set
- **THEN** the output does not contain the channel secret value

#### Scenario: Environment variable form

- **WHEN** the connector settings are supplied only as `Connectors__line__...` environment variables
- **THEN** the connector is created with those values

### Requirement: Parallel connector shutdown and shutdown budget

On shutdown the host SHALL call the stop operation of every started connector instance concurrently and exactly once, SHALL observe and log the failure of each stop separately, and a failure of one stop MUST NOT prevent or delay the others. Each connector SHALL report its worst-case stop duration as a stop budget through the connector contract, so the host does not depend on the connector framework assembly. The host SHALL set its shutdown timeout to the largest reported stop budget plus the configured shutdown margin (`Assistant:Shutdown:Margin`, a positive duration with a default of 5 seconds) and SHALL log the computed shutdown budget at startup. The host SHALL pass its shutdown cancellation token to each stop operation. The stop budget SHALL be smaller than the host shutdown budget, and the host shutdown budget SHALL be smaller than the container stop grace period configured by the project.

#### Scenario: Instances stop in parallel

- **WHEN** three instances each need 8 seconds to stop
- **THEN** shutdown completes in about 8 seconds, not 24, and each stop operation was called once

#### Scenario: One stop fails

- **WHEN** the stop operation of one of three instances throws
- **THEN** the other two still complete their stop, and the failure is logged for that instance only

#### Scenario: Shutdown budget from reported budgets

- **WHEN** the instances report stop budgets of 15 seconds and 25 seconds and the margin is 5 seconds
- **THEN** the host shutdown timeout is 30 seconds and the startup log states it

#### Scenario: Invalid margin

- **WHEN** `Assistant:Shutdown:Margin` is 0 or negative
- **THEN** startup fails naming the setting

#### Scenario: Host token cancels the stop

- **WHEN** the host shutdown timeout elapses while a connector is still within its grace period
- **THEN** the cancellation token given to its stop operation is cancelled and the connector interrupts at once

#### Scenario: Host does not reference the framework

- **WHEN** the compiled host assembly's references are inspected
- **THEN** it references neither the connector framework nor any connector assembly

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
