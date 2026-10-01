## Purpose

The connector framework provides a generic webhook-style connector that implements the shared inbound and outbound flow once, so a new messaging platform only supplies its own API calls. It keeps platform credentials, redelivery handling, reply-versus-push routing, activity indicators, and event processing out of the host.

## ADDED Requirements

### Requirement: Platform adapter contract

The framework SHALL define a send-side interface `IMessagingPlatform` exposing declared capabilities (Reply, Push, Activity, and a reply validity duration), a reply call, a push call, and an activity-start call, and a receive-side interface `IWebhookInbound` exposing verification and parsing. A platform SHALL declare which capabilities it supports, and the framework MUST NOT call an operation whose capability the platform did not declare. The receive-side interface SHALL return, for each accepted event, an event id used for deduplication, the platform's event time, an optional reply token, and an inbound envelope.

#### Scenario: Unsupported capability is not called

- **WHEN** a platform declares only the Push capability and a reply-eligible outbound message is delivered
- **THEN** the framework uses push and never calls the platform's reply operation

#### Scenario: Platform declares no activity support

- **WHEN** a platform does not declare the Activity capability
- **THEN** the framework starts no activity indicator and raises no error

#### Scenario: Events the platform skips

- **WHEN** parsing yields no events for a request (for example only unsupported event types)
- **THEN** the framework returns success without invoking the host handler

### Requirement: Webhook connector pipeline

The framework SHALL process each webhook request by verifying it, parsing it, and then admitting each parsed event in original order before responding. It SHALL map a verification failure to status 401, a payload failure to status 400, a request received while the connector is stopping to status 503, and every other outcome after successful verification to status 200. Admission of an event SHALL happen inside one short state gate that checks the connector is Running and the event id is not already registered (a duplicate is discarded), then calls a non-blocking write of the event's work to a bounded in-memory channel exactly once; only when that write succeeds SHALL the framework register the event and create its reply context before releasing the gate, and when the write fails the framework SHALL NOT register the event, create a reply context, or evict any existing registration. Once parsing has completed, cancellation of the HTTP request MUST NOT interrupt admission, so no event is ever registered without its work being enqueued.

#### Scenario: Events admitted in order

- **WHEN** a verified request contains three events
- **THEN** the three events are admitted in the original order

#### Scenario: Verification failure

- **WHEN** the platform's verification raises a webhook verification error
- **THEN** the status is 401 and neither parsing nor admission occurs

#### Scenario: Payload failure

- **WHEN** the platform's parsing raises a connector payload error
- **THEN** the status is 400 and nothing is admitted

#### Scenario: Request cancelled after parsing

- **WHEN** the HTTP request is cancelled after parsing completed
- **THEN** every parsed event is either discarded as a duplicate, or admitted (enqueued and registered), or discarded for overload (not enqueued and not registered), and none is registered without enqueued work

#### Scenario: Work cannot start before registration commits

- **WHEN** a worker is woken by an enqueued work item while the admission gate is still held for its registration
- **THEN** the worker waits for the gate and starts the work only after the registration and reply context are committed

### Requirement: Early acknowledgement and background event work

The framework SHALL return the webhook response without awaiting any activity-indicator call, host handler invocation, or platform API call; the response path MUST perform only verification, parsing, admission, and the response. Admitted events SHALL be processed by connector-owned in-memory work: a fixed number of workers (default 4) takes work from a bounded channel whose capacity is the pending limit (default 100), obtains the admission gate to confirm the registration is committed and the connector is not stopping, runs the activity indicator, and then invokes the host handler. This work is not cancelled by cancellation of the originating HTTP request and is cancelled only by the connector stopping or by the event budget. When the pending limit is reached the new event SHALL be discarded without registration, a warning with the discarded count SHALL be logged, and the success status SHALL still be returned. Each work item SHALL record the time it was enqueued; when a worker takes an item older than the maximum queue age (`Work:MaxQueueAge`, default 30 seconds) the framework SHALL log and discard it without starting an activity indicator or invoking the handler, and the item's registration SHALL be kept. Events of one request SHALL be admitted in order, and the framework SHALL NOT guarantee processing order across requests. A failure while processing one event MUST be logged and MUST NOT affect other events or the response status.

#### Scenario: Response does not wait for processing

- **WHEN** a fake platform delays its activity call by 3 seconds and the handler by 5 seconds
- **THEN** the webhook response is returned before either completes, and both complete afterwards

#### Scenario: Request abort does not cancel work

- **WHEN** the HTTP request is aborted after the response was produced
- **THEN** the already enqueued event work continues and completes

#### Scenario: Event failure is isolated

- **WHEN** the handler throws for the second of three events of one request
- **THEN** the first and third events are still processed, the exception is logged, and the response status was 200

#### Scenario: Concurrency limit

- **WHEN** 10 events are admitted while the worker count is 4 and each handler blocks until released
- **THEN** at most 4 handlers run at the same time and the remaining events wait

#### Scenario: Overload discards without registering

- **WHEN** the pending limit is 2, all workers are busy, and 5 new events arrive in one request whose cancellation token is cancelled right after parsing
- **THEN** the first 2 events are enqueued and registered, the other 3 are discarded and not registered, a warning logs 3 discarded events, and the status is 200

#### Scenario: Redelivery of an overload-discarded event

- **WHEN** an event discarded for overload is redelivered later while capacity is available
- **THEN** it is admitted and processed as a new event

#### Scenario: Queue age exceeded

- **WHEN** a worker takes an item that was enqueued 31 seconds earlier with a maximum queue age of 30 seconds
- **THEN** the item is discarded and logged, no activity indicator is started, the handler is not invoked, and its registration is kept so a redelivery is discarded as a duplicate

#### Scenario: Budget starts when processing starts

- **WHEN** an item waits 20 seconds in the queue and then its handler runs for 25 seconds with an event budget of 30 seconds
- **THEN** the handler is not cancelled because the budget is measured from the moment the worker started

### Requirement: Redelivery deduplication

The framework SHALL discard any event whose event id is still registered by the same connector instance, where a registration exists only for an event whose work was successfully enqueued and is retained for the configured time-to-live (default 10 minutes) unless it is evicted earlier for capacity. Checking and registering an event id MUST be performed inside the admission gate so that checking and registering is a single atomic operation. A discarded event SHALL NOT invoke the handler, start an activity indicator, or call any platform API, and SHALL NOT change the success status. The time-to-live MUST be positive; a zero or negative value SHALL fail connector creation with an error naming the setting. The registry SHALL hold at most the configured maximum number of entries (default 10000) and SHALL evict the oldest entry and log a warning when full; an evicted event is processed again if it is redelivered, and this is an accepted limitation. An event whose work was enqueued and later discarded (queue age exceeded or connector stopping) keeps its registration. Expired entries SHALL be removed on each registration and by a periodic background sweep, so the entry count returns below the maximum when no new events arrive. Deduplication state is held in memory and is lost on restart.

#### Scenario: Redelivered event is discarded

- **WHEN** the same event id arrives twice within the time-to-live and before eviction
- **THEN** the handler is invoked exactly once and both requests return 200

#### Scenario: Concurrent duplicates

- **WHEN** the same event id arrives on several concurrent requests
- **THEN** the handler is invoked exactly once

#### Scenario: Different events are independent

- **WHEN** two events have different event ids
- **THEN** both are handled

#### Scenario: Expiry after the time-to-live

- **WHEN** the same event id arrives again after the time-to-live elapsed
- **THEN** the handler is invoked again

##### Example: time-to-live boundaries

| Elapsed since first acceptance | Time-to-live | Handler invoked again |
| ------------------------------ | ------------ | --------------------- |
| 9 minutes 59 seconds | 10 minutes | no |
| 10 minutes 1 second | 10 minutes | yes |

#### Scenario: Eviction ends protection early

- **WHEN** the maximum is 2 entries, events E1, E2, E3 are admitted in that order, and E1 is redelivered one minute later
- **THEN** E1 was evicted when E3 was admitted, the redelivered E1 is processed again, a warning about eviction was logged, and the response status is 200

#### Scenario: Eviction while the first processing still runs

- **WHEN** E1 is still being processed when it is evicted and E1 is then redelivered
- **THEN** the redelivery is processed concurrently as a new event and no error is raised

#### Scenario: Registry stays bounded

- **WHEN** 20000 unique events are admitted with a maximum of 10000 entries and the clock then advances past the time-to-live
- **THEN** the entry count never exceeds 10000 during admission and is zero after the next sweep

#### Scenario: Invalid time-to-live

- **WHEN** the time-to-live is configured as 0 or negative
- **THEN** connector creation fails with an error naming the setting

### Requirement: Reply context and delivery routing

The framework SHALL keep, for each admitted event, a reply context in connector-private memory keyed by the inbound message key and containing the reply token, the original chat key, the validity start, and a used flag, and SHALL NOT place the token in any envelope handed to the host. The reply context for a message key SHALL be a single object shared by every retained event registration for that message key: registering the same message key again while the context is retained MUST reuse it and MUST NOT reset its used flag or validity start, and the context SHALL be removed only when no retained registration references it, so the number of contexts never exceeds the number of registrations. The validity start MUST be the earlier of the time the event was received and the platform's event time, and the context is valid for the platform's declared reply validity. When delivering an outbound envelope, the destination SHALL always be the envelope's chat. The framework SHALL use reply only when the envelope names an in-reply-to message whose context exists, is unexpired, is unused, has an original chat equal to the envelope's chat, and the platform supports Reply; checking these conditions and marking the context used MUST be one atomic operation. Otherwise it SHALL use push if the platform supports Push, and otherwise SHALL log that the message was undeliverable. When the chat differs from the original chat the framework SHALL push to the envelope's chat without consuming the token and SHALL log a warning. The used guarantee holds only while the context is retained; if all registrations referencing a context were evicted, a later registration creates a new context and a later reply attempt for the same token is made, with the platform's result handled as below and no unbounded record kept. If a reply attempt fails, including a platform rejection of the token, the framework MUST log the failure and MUST NOT fall back to push.

#### Scenario: Fresh token uses reply

- **WHEN** an outbound envelope replies to a message received 10 seconds ago in the same chat with reply validity 50 seconds
- **THEN** the platform reply operation is called with that message's token and push is not called

#### Scenario: Locally expired token uses push

- **WHEN** an outbound envelope replies to a message received 51 seconds ago with reply validity 50 seconds
- **THEN** the platform push operation is called to the envelope's chat and reply is not called

#### Scenario: Late event time shortens validity

- **WHEN** an event received 1 second ago carries a platform event time 55 seconds in the past and the validity is 50 seconds
- **THEN** its context is treated as expired and delivery uses push

#### Scenario: No in-reply-to uses push

- **WHEN** an outbound envelope has no in-reply-to message
- **THEN** the platform push operation is called

#### Scenario: Token is single use

- **WHEN** two outbound envelopes reply to the same message
- **THEN** the first uses reply and the second uses push

#### Scenario: Parallel outbound consume the token once

- **WHEN** two outbound envelopes for the same message and chat are delivered concurrently
- **THEN** the reply operation is called at most once in total and the other envelope is pushed

#### Scenario: Chat mismatch with a valid token

- **WHEN** message M1 was received in chat A and an outbound envelope with in-reply-to M1 targets chat B while the token is valid
- **THEN** the message is pushed to chat B, the token is not consumed, the reply operation is not called, and a warning is logged

#### Scenario: Chat mismatch with an expired token

- **WHEN** the same mismatching envelope is delivered after the context expired
- **THEN** the message is pushed to chat B

#### Scenario: Two registrations share one context

- **WHEN** events E1 and E2 with different event ids but the same message key and token are both admitted without eviction, and two outbound envelopes reply to that message
- **THEN** the reply operation is called at most once in total and the other envelope is pushed

#### Scenario: Used state survives while any registration remains

- **WHEN** E1 and E2 share a context that was consumed and E1 is evicted while E2 is retained
- **THEN** the context is still marked used and a later outbound for that message uses push

#### Scenario: Used state ends with the last registration

- **WHEN** a context was consumed, every registration referencing it is evicted, the same message key is registered again, and an outbound envelope replies to it
- **THEN** a new context is created, the reply operation is attempted once more, a platform rejection is logged without calling push, and no error reaches the host

#### Scenario: Platform rejection does not fall back to push

- **WHEN** the reply call fails because the platform rejects the token
- **THEN** the failure is logged and push is not called

#### Scenario: Neither route available

- **WHEN** the context is locally expired and the platform does not support Push
- **THEN** the framework logs that the message is undeliverable and raises no error to the host

#### Scenario: Token never reaches the host

- **WHEN** the host handler receives an inbound envelope
- **THEN** no envelope field contains the reply token

### Requirement: Activity indicator lifecycle

The framework SHALL call the platform's activity-start operation, when the platform declares Activity, as the first step of each event's work and before invoking the host handler, and SHALL keep the returned disposable until the reply to that message is delivered, the maximum activity duration (default 2 minutes) elapses, or the connector stops, then dispose it exactly once. Failure or timeout of the activity operation MUST be logged as a warning and MUST NOT prevent the handler from running. The host SHALL NOT be required to start or stop indicators.

#### Scenario: Indicator started before handler

- **WHEN** an accepted event's work starts on a platform declaring Activity
- **THEN** the activity-start operation completes or times out before the handler is invoked

#### Scenario: Indicator stopped on reply

- **WHEN** an outbound envelope replying to that message is delivered
- **THEN** the stored disposable is disposed exactly once

#### Scenario: Indicator stopped on timeout

- **WHEN** no reply is delivered within the maximum activity duration
- **THEN** the stored disposable is disposed

#### Scenario: Indicator failure

- **WHEN** the activity-start operation fails
- **THEN** a warning is logged and the handler is still invoked

#### Scenario: Platform declines the indicator

- **WHEN** the platform's activity-start operation returns null for the chat
- **THEN** nothing is stored and no error is raised

### Requirement: Timeouts and event work budget

The framework SHALL apply per-call timeouts of 2 seconds to the activity operation and 10 seconds to reply and push by default, and a processing budget of 30 seconds to each event, each overridable by connector settings. The processing budget SHALL start when a worker starts processing the event, not when it was admitted. The remaining event budget SHALL be exposed to the host handler through its cancellation token. A timeout or cancellation of one call MUST NOT be reported as a platform failure to the host.

#### Scenario: Activity call hangs

- **WHEN** the activity operation does not complete within its timeout
- **THEN** a warning is logged and the handler is invoked after the timeout

#### Scenario: Reply call hangs

- **WHEN** the reply call does not complete within its timeout
- **THEN** the call is cancelled, an error is logged, and the next event's work is unaffected

#### Scenario: Event budget exhausted

- **WHEN** an event's handler runs longer than the processing budget
- **THEN** the handler's cancellation token is cancelled, the failure is logged, and other events are unaffected

### Requirement: Connector stop and cleanup

The connector SHALL have the states Running, Stopping, and Stopped, and admission of events and the transition to Stopping SHALL be serialized by the same state gate. On stop the framework SHALL enter Stopping, close admission and respond 503 to new webhook requests, discard each work item not yet started with a log entry (keeping its registration), and let executing work continue until the configured stop grace period (default 10 seconds) without draining the queue. While Stopping, a delivery SHALL be accepted only from a caller holding a valid work lease: a connector-private, revocable shared reference created by the framework around each handler invocation, validated against its connector instance, the work's validity, and the stop state, and propagated through the normal asynchronous execution context so that a task started by the handler inherits it; the delivery check and in-flight delivery registration SHALL be ordered by the state gate, and a delivery without a valid lease SHALL be rejected and logged. A lease is revoked when its handler returns, its event budget is cancelled, or the grace period ends. When the grace period ends the framework SHALL revoke all leases, close delivery permission, cancel executing work and in-flight deliveries, and then wait up to the configured join timeout (default 5 seconds) for workers, background timers, and activity cleanup to exit before entering Stopped; every activity disposable not yet disposed SHALL be disposed exactly once. After Stopped, every delivery entry point, including calls from the host, SHALL be rejected without calling the platform. Cancellation is cooperative and the framework SHALL NOT promise to terminate operations that ignore cancellation. A stop whose join completes normally leaves no framework-owned worker, timer, or cleanup running. When the join timeout expires the framework SHALL log the abandoned work and isolate its continuations: after Stopped no new event processing, activity indicator, or platform call SHALL start and no timer SHALL restart, and a late continuation SHALL only observe its result or exception and release resources, and MUST NOT register an activity indicator or send through the gateway.

#### Scenario: Stop waits for work

- **WHEN** stop is requested while an executing event finishes within the grace period
- **THEN** that work completes, including its delivery, before stop returns

#### Scenario: Pending work is dropped

- **WHEN** stop is requested with 2 executing events and 3 queued events
- **THEN** the 3 queued events are logged as discarded without starting, and the 2 executing events continue until the grace period

#### Scenario: Stop cancels work after the grace period

- **WHEN** an executing event is still running when the grace period ends
- **THEN** its cancellation token and its in-flight delivery are cancelled

#### Scenario: Cooperative handler finishes cleanup

- **WHEN** a handler observes cancellation and takes 200 milliseconds to finish its cleanup
- **THEN** stop waits for it before entering Stopped

#### Scenario: Handler ignores cancellation

- **WHEN** a handler ignores cancellation and the join timeout of 5 seconds expires
- **THEN** the abandoned work is logged, stop completes, and the framework does not claim to have terminated the handler

#### Scenario: Late continuation cannot send

- **WHEN** an abandoned handler resumes after stop returned and calls the gateway to send
- **THEN** no platform API is called and the rejection is logged

#### Scenario: Late activity result is released once

- **WHEN** the activity-start operation returns a disposable after stop completed
- **THEN** the disposable is disposed exactly once and is not added to the activity registry

#### Scenario: Activity disposed on stop

- **WHEN** stop is requested with two activity indicators still active
- **THEN** each is disposed exactly once

#### Scenario: Lease allows delivery while stopping

- **WHEN** a handler running under a valid lease starts a task with Task.Run during Stopping and that task delivers a message with no in-reply-to message
- **THEN** the delivery is accepted

#### Scenario: Revoked lease cannot deliver

- **WHEN** a task that inherited a lease delivers after the handler returned or the grace period ended
- **THEN** the delivery is rejected and logged

#### Scenario: No lease while stopping

- **WHEN** a caller without a lease delivers during Stopping
- **THEN** the delivery is rejected and logged

#### Scenario: Admission races with stop

- **WHEN** admission of an event and the transition to Stopping happen concurrently
- **THEN** the event is either fully admitted before Stopping or rejected with no registration, never partially admitted

#### Scenario: Nothing runs after a clean stop

- **WHEN** stop completed with a normal join and the clock advances past all sweep intervals
- **THEN** no sweep, callback, or platform call occurs

#### Scenario: Requests after stop

- **WHEN** a webhook request arrives after stop was requested
- **THEN** the response status is 503 and nothing is admitted

### Requirement: Best-effort delivery

The framework SHALL attempt each step of handling an event at most once and SHALL NOT retry a failed activity, handler, reply, or push call. A message whose handling failed, whose work was discarded after enqueue (queue age exceeded or connector stopping), or that was lost at stop or crash is not answered later, and a message discarded for overload before enqueue is not registered so that a redelivery can be processed. The framework MUST log each failure or discard with the event id or message key and the failing step.

#### Scenario: Failed reply is not retried

- **WHEN** the reply call returns a server error
- **THEN** exactly one reply call is made and the error is logged

#### Scenario: Redelivery after failure is discarded

- **WHEN** handling event E1 failed and the platform redelivers E1 while its registration is retained
- **THEN** the redelivery is discarded

### Requirement: Framework logging hygiene

The framework SHALL NOT write reply tokens, platform credentials, request bodies, signatures, or message text to logs or to exception messages.

#### Scenario: Failure log content

- **WHEN** a send failure is logged
- **THEN** the log entry contains the message key or event id and the failing step but none of the values listed above
