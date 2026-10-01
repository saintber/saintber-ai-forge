## Purpose

The LINE connector is the LINE implementation of the connector framework's platform adapter. It verifies LINE webhook signatures, translates LINE events into platform-neutral inbound envelopes with stable external keys, and performs the LINE Messaging API calls for reply, push, and loading. It contains no deduplication, routing, or timing policy; those belong to the framework.

## ADDED Requirements

### Requirement: Webhook signature verification

The LINE platform SHALL verify every webhook request by computing HMAC-SHA256 over the raw request body bytes using the configured channel secret, Base64-encoding the result, and comparing it to the `X-Line-Signature` header with a constant-time comparison. The platform MUST raise a webhook verification error when the header is missing, is not valid Base64, or does not match.

#### Scenario: Valid signature is accepted

- **WHEN** a request body is signed with the configured channel secret and the matching signature is supplied
- **THEN** verification succeeds

#### Scenario: Tampered body is rejected

- **WHEN** one byte of the body is altered after the signature was computed
- **THEN** verification raises a webhook verification error

##### Example: known signature vector

- **GIVEN** channel secret `test-secret`, body `{"destination":"U0","events":[]}`, and the fixed expected signature `AQQTPDN0VEjXZIlgGdat3T+SL3wHGAG+cHc684p/XqU=` computed independently with an external tool
- **WHEN** that signature is supplied in `X-Line-Signature`
- **THEN** verification succeeds, and the same request with the last signature character altered fails

#### Scenario: Missing or malformed signature is rejected

- **WHEN** the `X-Line-Signature` header is absent, empty, or not valid Base64
- **THEN** verification raises a webhook verification error

#### Scenario: Signature is computed over raw bytes

- **WHEN** the body contains non-ASCII characters and JSON escape sequences and is signed as those exact bytes
- **THEN** verification succeeds without re-serializing the body

### Requirement: Text message event parsing

The LINE platform SHALL translate each LINE text message event into one parsed event containing the event id taken from `webhookEventId`, the event time taken from the event `timestamp`, the reply token, and an inbound envelope with connector type `line`, the configured connector instance id, the actor key, the chat key, a null thread key, the message key, the text content, and the event timestamp as a UTC-aware time. The platform SHALL NOT produce a parsed event for events that are not text messages, whose `mode` is not `active`, that lack a user id, or that lack a `webhookEventId`, and SHALL log only the skipped event type.

#### Scenario: One-to-one text message

- **WHEN** a webhook contains a text message event whose source type is `user`
- **THEN** one event is produced whose actor key and chat key are the same user key and whose thread key is null

#### Scenario: Group text message

- **WHEN** a text message event has source type `group` with both a group id and a user id
- **THEN** one event is produced whose actor key is the user key and whose chat key is the group key

#### Scenario: Room text message

- **WHEN** a text message event has source type `room`
- **THEN** one event is produced whose chat key is the room key

#### Scenario: Multiple events in one webhook

- **WHEN** a webhook contains three text message events
- **THEN** three parsed events are returned in the original order

#### Scenario: Skipped events

- **WHEN** an event is an image message, a sticker message, a follow event, a standby-mode event, an event without a user id, or an event without a `webhookEventId`
- **THEN** no parsed event is produced for it and the remaining events are still processed

#### Scenario: Malformed payload

- **WHEN** the body has a valid signature but is not valid JSON or has no `events` array
- **THEN** parsing raises a connector payload error

### Requirement: Deterministic external keys

The LINE platform SHALL produce external keys whose canonical value is deterministic and independent of JSON property order, in the format `line:<kind>:<id>` where kind is one of `user`, `group`, `room`, or `message`. The connector instance id SHALL NOT be part of the canonical value, and the event id SHALL NOT be encoded as an external key. The properties of an external key SHALL be an independent copy (a cloned `JsonElement`) that stays readable after the parser that produced it was released, and the canonical value and the properties SHALL be produced together from the same kind and identifier by one factory so they cannot disagree.

#### Scenario: Canonical values

- **WHEN** keys are produced for user `U123`, group `C987`, room `R555`, and message `M42`
- **THEN** the canonical values are `line:user:U123`, `line:group:C987`, `line:room:R555`, and `line:message:M42`

#### Scenario: Key outlives the parser

- **WHEN** a key is read after the request body and parser were released
- **THEN** both the canonical value and the properties are readable and unchanged

#### Scenario: Properties carry the original identifier

- **WHEN** a user key is produced for user `U123`
- **THEN** its properties contain `userId` equal to `U123`

### Requirement: Sanitized raw metadata

The LINE platform SHALL provide each envelope's raw metadata as an independent copy of the original event with every transport-level property removed (`replyToken`, `webhookEventId`, and `deliveryContext`), and the copy MUST remain readable after the request body has been released. The host-facing envelope MUST NOT expose the event id or any redelivery information.

#### Scenario: Transport fields removed

- **WHEN** an event carrying reply token `token-1`, `webhookEventId` `01ABC`, and a `deliveryContext` is parsed
- **THEN** the envelope's raw metadata contains none of those properties and no value equal to `token-1` or `01ABC`, while the parsed event still carries the event id and reply token separately for the framework

#### Scenario: Metadata outlives the request

- **WHEN** the envelope's raw metadata is read after parsing has returned and the source buffer was released
- **THEN** the read succeeds and returns the remaining business fields such as the source, the message, and the timestamp

### Requirement: Reply message sending

The LINE platform SHALL perform a reply by calling the LINE reply endpoint with the reply token and a single text message, authorized with the configured channel access token as a bearer token. When a LINE API call returns a non-success status, the platform SHALL raise an error that contains the HTTP status but not the access token, reply token, or message text.

#### Scenario: Reply request shape

- **WHEN** a reply with token `token-1` and text `hello` is performed
- **THEN** one POST is made to `/v2/bot/message/reply` with header `Authorization: Bearer <access token>` and JSON body `{"replyToken":"token-1","messages":[{"type":"text","text":"hello"}]}`

#### Scenario: LINE rejects the reply

- **WHEN** the LINE API responds with status 400
- **THEN** the raised error mentions status 400 and contains none of the access token, reply token, or message text

### Requirement: Push message sending

The LINE platform SHALL perform a push by calling the LINE push endpoint with the destination identifier taken from the chat key's properties (user id, group id, or room id) and a single text message, authorized with the channel access token as a bearer token. Before sending, the platform SHALL verify that the chat key's canonical value and its properties name the same identifier, and when they differ it SHALL NOT send and SHALL report the message as undeliverable without disclosing the identifiers.

#### Scenario: Push to a user

- **WHEN** a push to chat key `line:user:U123` with text `hello` is performed
- **THEN** one POST is made to `/v2/bot/message/push` with JSON body `{"to":"U123","messages":[{"type":"text","text":"hello"}]}`

#### Scenario: Push to a group

- **WHEN** a push to chat key `line:group:C987` is performed
- **THEN** the JSON body's `to` is `C987`

#### Scenario: Contradictory key is not sent

- **WHEN** a push targets a chat key whose canonical value is `line:user:U123` but whose properties contain `userId` equal to `U999`
- **THEN** no HTTP request is made and the message is reported as undeliverable

### Requirement: Text length limit on UTF-16 boundary

The LINE platform SHALL limit message text to 5000 UTF-16 code units. When the text is longer, it SHALL be truncated to at most 5000 code units and, if the cut would fall between the two halves of a surrogate pair, SHALL cut before that pair so the result is valid text.

#### Scenario: Plain text truncation

- **WHEN** the text is 6000 ASCII characters
- **THEN** the sent text is exactly the first 5000 characters

#### Scenario: Emoji at the boundary

- **WHEN** the text is 4999 ASCII characters followed by one emoji that occupies two UTF-16 code units, followed by more text
- **THEN** the sent text is the 4999 ASCII characters only and contains no unpaired surrogate

#### Scenario: Text within the limit

- **WHEN** the text is exactly 5000 code units
- **THEN** it is sent unchanged

### Requirement: Loading indicator for one-to-one chats

The LINE platform SHALL declare the Activity capability and, for a one-to-one chat, SHALL start the LINE loading animation by calling the loading endpoint with the user id as chat id and the configured number of seconds, returning a disposable that performs no action when disposed. For group and room chats the platform SHALL make no HTTP request and SHALL return null. The configured seconds MUST be a multiple of five between 5 and 60 inclusive, with a default of 5, and any other value SHALL fail connector creation with an error naming the setting.

#### Scenario: Loading started for a user chat

- **WHEN** activity is started for chat key `line:user:U123` with default settings
- **THEN** one POST is made to `/v2/bot/chat/loading/start` with JSON body `{"chatId":"U123","loadingSeconds":5}`

#### Scenario: No loading for group or room chats

- **WHEN** activity is started for chat key `line:group:C987` or `line:room:R555`
- **THEN** no HTTP request is made and null is returned

#### Scenario: Invalid loading seconds

- **WHEN** the configured loading seconds is 7, which is within the range but not a multiple of 5
- **THEN** connector creation fails with an error naming the setting

#### Scenario: Loading seconds out of range

- **WHEN** the configured loading seconds is 65
- **THEN** the host's range check fails before the factory is called, naming the setting

#### Scenario: Late loading result is a no-op

- **WHEN** a loading call returns after the framework discarded it as late
- **THEN** disposing the returned object performs no action, and the documentation states that an animation that already started ends only when its configured seconds elapse or a new message arrives

### Requirement: LINE capability declaration and settings

The LINE platform SHALL declare the Reply, Push, and Activity capabilities with a reply validity of 50 seconds, and its connector factory SHALL publish a settings schema made of setting descriptors: `ChannelSecret` (string, required, secret), `ChannelAccessToken` (string, required, secret), `ApiBaseUrl` (string, default `https://api.line.me`), and `LoadingSeconds` (integer, default 5, range 5 to 60), together with the framework tuning parameters of the connector framework; the rule that `LoadingSeconds` is a multiple of 5 SHALL be validated by the connector factory when it creates the connector and not by the descriptor. Connector creation SHALL fail when `ChannelSecret` or `ChannelAccessToken` is missing or empty, and the error MUST name the missing keys and MUST NOT contain any setting value.

#### Scenario: Capabilities

- **WHEN** the LINE platform's capabilities are read
- **THEN** Reply, Push, and Activity are declared and the reply validity is 50 seconds

#### Scenario: Settings schema

- **WHEN** the LINE factory lists its settings schema
- **THEN** it includes `ChannelSecret` and `ChannelAccessToken` marked required and secret, `ApiBaseUrl` with its default, `LoadingSeconds` with default 5 and range 5 to 60, and every framework tuning parameter

#### Scenario: Missing secret

- **WHEN** a LINE connector is created without `ChannelSecret`
- **THEN** creation fails with an error naming `ChannelSecret` and containing no value of any other setting

#### Scenario: Defaults applied

- **WHEN** only the channel secret and access token are provided
- **THEN** the API base URL is `https://api.line.me` and loading seconds is 5

### Requirement: Sensitive data is not disclosed

The LINE platform SHALL NOT write the channel secret, the channel access token, the reply token, the `X-Line-Signature` value, the full request body, or message text to logs or error messages.

#### Scenario: Verification failure log

- **WHEN** signature verification fails
- **THEN** the logged output and the raised error message contain neither the supplied signature nor the request body
