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

- **WHEN** the activity timeout is overridden to 10 seconds and a fake platform delays its activity call by 3 seconds and the handler by 5 seconds
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

#### Scenario: Usable redelivered token is still pushed when locally expired

- **WHEN** a redelivered event whose platform event time is 55 seconds old is first received now with an unused token and the validity is 50 seconds
- **THEN** delivery uses push because the conservative local rule treats the context as expired, and this limitation is accepted

#### Scenario: Token never reaches the host

- **WHEN** the host handler receives an inbound envelope
- **THEN** no envelope field contains the reply token

### Requirement: Activity indicator lifecycle

The framework SHALL call the platform's activity-start operation, when the platform declares Activity, as the first step of each event's work and before invoking the host handler, and SHALL keep the returned disposable until the reply to that message is delivered, the maximum activity duration (default 2 minutes) elapses, or the connector stops, then dispose it exactly once. Failure or timeout of the activity operation MUST be logged as a warning and MUST NOT prevent the handler from running. A result that the activity operation returns after its timeout elapsed, in any connector state, SHALL NOT be registered: the framework SHALL dispose it exactly once when it arrives and SHALL observe any exception it raises. The framework's commitment SHALL be limited to state it owns: no registration is kept, the disposable is disposed once, and no local timer or resend is started; effects the platform already produced end on the platform's own schedule. The host SHALL NOT be required to start or stop indicators.

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

#### Scenario: Late result while running

- **WHEN** the activity operation ignores cancellation, times out at 2 seconds, the handler replies at 2.1 seconds, and the operation returns a disposable at 3 seconds
- **THEN** the disposable is disposed exactly once at 3 seconds, it is never registered, and the framework holds no registration, timer, or resend for it

#### Scenario: Late result with a local timer

- **WHEN** a platform returns, after the activity timeout, a disposable that owns a repeating local timer
- **THEN** disposing it stops the timer immediately and the framework never registers it

#### Scenario: Late failure is observed

- **WHEN** the activity operation faults after its timeout elapsed
- **THEN** the exception is observed and logged and no unobserved task exception occurs

### Requirement: Timeouts and event work budget

The framework SHALL apply per-call timeouts of 2 seconds to the activity operation and 10 seconds to reply and push by default, and a processing budget of 30 seconds to each event, each overridable by connector settings. The processing budget SHALL start when a worker starts processing the event, not when it was admitted. The remaining event budget SHALL be exposed to the host handler through its cancellation token. A timeout or cancellation of one call MUST NOT be reported as a platform failure to the host. Cancellation is cooperative: the framework SHALL guarantee that the budget frees a worker and leaves other events unaffected only for handlers that honor the cancellation token, and the handler contract SHALL state this.

#### Scenario: Activity call hangs

- **WHEN** the activity operation does not complete within its timeout
- **THEN** a warning is logged and the handler is invoked after the timeout

#### Scenario: Reply call hangs

- **WHEN** the reply call does not complete within its timeout
- **THEN** the call is cancelled, an error is logged, and the next event's work is unaffected

#### Scenario: Event budget exhausted

- **WHEN** a cooperative handler runs longer than the processing budget
- **THEN** the handler's cancellation token is cancelled, the handler returns, the failure is logged, and other events are unaffected

### Requirement: Worker overrun and degraded admission

When a handler has not returned within the overrun grace (`Work:OverrunGrace`, default 5 seconds) after its event budget was cancelled, the framework SHALL mark its worker as overrun, SHALL log an error once, and SHALL revoke the work lease of that event. The framework SHALL NOT replace an overrun worker, so the number of handlers running at the same time never exceeds the configured maximum concurrency. While every worker is overrun the connector SHALL be Degraded. Degraded SHALL be a flag that exists only while the connector lifecycle is Running and SHALL NOT be a fourth lifecycle state: while Degraded the connector SHALL discard each new event without registering it, log the discard, and still return the success status, and it SHALL leave Degraded automatically when any overrun handler returns, but only if the lifecycle is still Running. While the lifecycle is Stopping or Stopped, a returning handler SHALL only update the overrun count and the log and SHALL NOT reopen admission, process queued work, or lift the 503 response. Work items admitted before the connector became Degraded SHALL be processed under the maximum queue age when the connector recovers while Running, and SHALL be discarded under the stop rules when the connector stops. The framework SHALL log each worker's state (idle, running, overrun) transitions and every entry into and exit from Degraded.

#### Scenario: One handler overruns

- **WHEN** one of 4 handlers ignores cancellation and the budget plus overrun grace elapse
- **THEN** its worker is marked overrun, one error is logged, the other 3 workers keep processing events, and the connector is not Degraded

#### Scenario: All handlers overrun

- **WHEN** all 4 handlers ignore cancellation and the budget plus overrun grace elapse
- **THEN** the connector becomes Degraded, a new event is discarded without registration with a log entry and a 200 response, and never more than 4 handlers run at the same time

#### Scenario: Recovery while running

- **WHEN** one overrun handler returns while the connector is Degraded and Running
- **THEN** the connector leaves Degraded, logs the exit, and processes the next queued or new event

#### Scenario: Queued work after recovery

- **WHEN** an item was queued 40 seconds before recovery with a maximum queue age of 30 seconds and another was queued 10 seconds before
- **THEN** after recovery the first is discarded as too old and the second is processed

#### Scenario: Handler returns during stopping

- **WHEN** all workers are overrun, stop begins, and one handler then returns
- **THEN** the connector does not leave Degraded, does not reopen admission, does not process queued work, and keeps responding 503

#### Scenario: Handler returns after stopped

- **WHEN** an overrun handler returns after the connector reached Stopped
- **THEN** only the overrun count and the log change and nothing is processed or sent

#### Scenario: Overrun handler cannot send

- **WHEN** an overrun handler later tries to deliver a message
- **THEN** the delivery is rejected as unavailable and no platform API is called

### Requirement: Delivery acceptance and lease classification

The connector's delivery operation SHALL return `Accepted` when it was permitted to attempt the delivery, in which case later platform failures are logged and do not change the result, and SHALL return `Unavailable` without calling the platform when it refuses the delivery. A work lease SHALL be a connector-private, revocable shared reference that records the connector instance that owns it, that the framework creates around each handler invocation and propagates through the normal asynchronous execution context, restoring the previous ambient value when the handler returns; it SHALL be revoked when its handler returns, its event budget is cancelled, its worker is marked overrun, or the grace period of stopping ends. A lease is valid for a delivery only when its owner is the connector instance receiving the delivery, it is not revoked, and the state rules below allow it; a lease owned by another connector instance, whether valid or revoked, SHALL be treated as no lease for the receiving instance. Delivery SHALL be classified by the caller and the connector state as follows: a caller with a valid own lease is accepted while Running and while Stopping within the grace period and is unavailable when Stopped; a caller carrying a revoked own lease is unavailable in every state; a caller with no lease or only another instance's lease, such as a later active push, is accepted while Running and unavailable while Stopping and Stopped. Whether a caller carries a revoked own lease SHALL be determined by the presence of the ambient value and its owner, independent of the connector state. A delivery check and the registration of an in-flight delivery SHALL be ordered by the same state gate, and the supplied cancellation token SHALL NOT be treated as permission. The lease SHALL NOT be visible to the host.

#### Scenario: Caller without a lease while running

- **WHEN** a caller with no lease delivers a message with no in-reply-to message while the connector is Running
- **THEN** the delivery is accepted and the platform push operation is called

#### Scenario: Valid lease while running

- **WHEN** a handler running under a valid lease delivers a message
- **THEN** the delivery is accepted

#### Scenario: Revoked lease after the handler returned

- **WHEN** a task started by a handler delivers after the handler returned, with a cancellation token that is never cancelled, while the connector is Running
- **THEN** the delivery is unavailable and no platform API is called, for both a reply and a push

#### Scenario: Revoked lease after the budget expired

- **WHEN** a task started by a handler delivers after the event budget was cancelled, while the connector is Running
- **THEN** the delivery is unavailable and no platform API is called, for both a reply and a push

#### Scenario: Valid lease while stopping

- **WHEN** a handler running under a valid lease starts a task with Task.Run during Stopping within the grace period and that task delivers a message
- **THEN** the delivery is accepted

#### Scenario: No lease while stopping

- **WHEN** a caller without a lease delivers during Stopping
- **THEN** the delivery is unavailable and no platform API is called

#### Scenario: Any caller after stop

- **WHEN** any caller, with or without a lease, delivers after the connector reached Stopped
- **THEN** the delivery is unavailable and no platform API is called

#### Scenario: Platform failure after acceptance

- **WHEN** a delivery is accepted and the platform call fails
- **THEN** the result is still accepted and the failure is logged

#### Scenario: Another instance's lease while stopping

- **WHEN** connector B is Stopping and a handler of connector A, holding a valid lease owned by A, sends a message that the gateway routes to B
- **THEN** B treats the caller as having no lease, the delivery is unavailable, and no platform API is called

#### Scenario: Another instance's lease while running

- **WHEN** connector B is Running and a handler of connector A, holding a valid lease owned by A, sends a message routed to B
- **THEN** B treats the caller as having no lease and the delivery is accepted as an ordinary active push

#### Scenario: Two instances from one assembly and from separate load contexts

- **WHEN** two instances of the same connector type are created from one loaded assembly, and again from two separately loaded copies of the assembly
- **THEN** in both cases a lease owned by one instance is never accepted as the other instance's lease

#### Scenario: Lease stays private

- **WHEN** the host handler runs
- **THEN** neither the inbound envelope nor any host-visible type exposes the lease

### Requirement: Connector stop and cleanup

The connector SHALL have the states Running, Stopping, and Stopped, and admission of events and the transition to Stopping SHALL be serialized by the same state gate. On stop the framework SHALL enter Stopping, close admission and respond 503 to new webhook requests, discard each work item not yet started with a log entry (keeping its registration), and let executing work continue until the configured stop grace period (default 10 seconds) without draining the queue; deliveries during Stopping are governed by the lease classification. When the grace period ends the framework SHALL revoke all leases, close delivery permission, cancel executing work and in-flight deliveries, and then wait up to the configured join timeout (default 5 seconds) for workers, background timers, and activity cleanup to exit before entering Stopped; every activity disposable not yet disposed SHALL be disposed exactly once. Cancellation is cooperative and the framework SHALL NOT promise to terminate operations that ignore cancellation. A stop whose join completes normally leaves no framework-owned worker, timer, or cleanup running. When the join timeout expires the framework SHALL log the abandoned work and isolate its continuations: after Stopped no new event processing, activity indicator, or platform call SHALL start and no timer SHALL restart, and a late continuation SHALL only observe its result or exception and release resources, and MUST NOT register an activity indicator or send through the gateway. The connector SHALL report a stop budget equal to the sum of the configured stop grace period and the configured join timeout, and when the cancellation token supplied to the stop operation is cancelled the framework SHALL treat the remaining budget as exhausted: it SHALL revoke all leases at once, cancel executing work and in-flight deliveries, skip waiting for the grace period and the join, log that the stop was interrupted by the host, return, and isolate any late continuations as for an expired join timeout.

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
- **THEN** no platform API is called and the delivery is unavailable

#### Scenario: Late activity result is released once

- **WHEN** the activity-start operation returns a disposable after stop completed
- **THEN** the disposable is disposed exactly once and is not added to the activity registry

#### Scenario: Activity disposed on stop

- **WHEN** stop is requested with two activity indicators still active
- **THEN** each is disposed exactly once

#### Scenario: Stop budget

- **WHEN** the grace period is 10 seconds and the join timeout is 5 seconds
- **THEN** the reported stop budget is 15 seconds, and after overriding the grace period to 20 seconds it is 25 seconds

#### Scenario: Host cancels the stop

- **WHEN** the cancellation token given to the stop operation is cancelled 2 seconds into the grace period while an event is executing
- **THEN** the leases are revoked, the work and in-flight delivery are cancelled, the stop returns without waiting for the remaining grace period, and the interruption is logged

#### Scenario: Admission races with stop

- **WHEN** admission of an event and the transition to Stopping happen concurrently
- **THEN** the event is either fully admitted before Stopping or rejected with no registration, never partially admitted

#### Scenario: Nothing runs after a clean stop

- **WHEN** stop completed with a normal join and the clock advances past all sweep intervals
- **THEN** no sweep, callback, or platform call occurs

#### Scenario: Requests after stop

- **WHEN** a webhook request arrives after stop was requested
- **THEN** the response status is 503 and nothing is admitted

### Requirement: Framework settings schema

The framework SHALL expose a list of setting descriptors for its tuning parameters so that platform factories can include them in their own settings schema, each descriptor giving the key, value kind, default, minimum, and a description. The parameters SHALL be: `Dedup:Ttl` (duration, default 10 minutes), `Dedup:MaxEntries` (integer, default 10000), `Timeouts:Activity` (duration, default 2 seconds), `Timeouts:Send` (duration, default 10 seconds), `Timeouts:Event` (duration, default 30 seconds), `Work:MaxConcurrency` (integer, default 4), `Work:MaxPending` (integer, default 100), `Work:MaxQueueAge` (duration, default 30 seconds), `Work:OverrunGrace` (duration, default 5 seconds), `Stop:Grace` (duration, default 10 seconds), `Stop:JoinTimeout` (duration, default 5 seconds), and `Activity:MaxDuration` (duration, default 2 minutes). Every duration MUST be positive and every integer MUST be at least 1; a value that violates its descriptor SHALL fail connector creation with an error naming the setting and not its value. Changing these values through settings SHALL NOT require any code change.

#### Scenario: Defaults

- **WHEN** a connector is created with none of these settings
- **THEN** it uses the defaults listed above

#### Scenario: Override through settings

- **WHEN** `Work:MaxConcurrency` is set to 8 and `Timeouts:Event` to 60 seconds
- **THEN** up to 8 handlers run at the same time and the event budget is 60 seconds

#### Scenario: Invalid value

- **WHEN** `Work:MaxConcurrency` is 0 or `Timeouts:Send` is negative
- **THEN** connector creation fails with an error naming the setting and not its value

#### Scenario: Descriptors are discoverable

- **WHEN** a factory lists its settings schema
- **THEN** the list includes every framework parameter above with its default and description

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
