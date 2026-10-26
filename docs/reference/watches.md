# One-shot watches

The three watched reads on `ZooKeeperClient` establish a watch in the same server
request that observes the value. They accept a final optional `CancellationToken`.

| Method | Result |
| --- | --- |
| `ExistsAndWatchAsync(path)` | `WatchedResult<ZNodeStat?>`; a missing node returns null and an active watch |
| `GetDataAndWatchAsync(path)` | `WatchedResult<ZNodeData>`; missing or denied reads fail without returning a watch |
| `GetChildrenAndWatchAsync(path)` | `WatchedResult<ZNodeChildren>`; child names and parent Stat, with the same failure rule |

`WatchedResult<T>` has getter-only `Value` and `Watch` properties. The watch is
registered locally before the reader handles the next frame. An event can
therefore be buffered before the method's task completes or enumeration begins.
Ordinary reads and transaction reads continue to register no watch.

## Handles and events

`ZooKeeperWatch` implements `IAsyncDisposable` and exposes stable
`RegistrationId`, client-relative `Path`, and `Mode` (`OneShotExists`,
`OneShotData`, or `OneShotChildren`). Each handle permits one consumer through
`ReadEventsAsync(cancellationToken)`. A second consumer throws
`InvalidOperationException`; separate watched reads provide separate handles.

| Watch | Consuming invalidation |
| --- | --- |
| Missing existence read | Node creation |
| Present existence or data read | Data change or node deletion |
| Child-list read | Child-list change or watched-node deletion |

The stream buffers at most one event and completes after that invalidation.
There is no automatic rearming. A new watched read observes a new value and
establishes a new watch. State changes use `SubscribeStateChanges` and do not
consume one-shot watches.

`ZooKeeperWatchEvent` contains getter-only `RegistrationId`, `Kind`, `Path`,
and nullable `Zxid`. Kinds include node creation, deletion, data/child changes,
and data/child watch removal. `None` is an unspecified value and is not emitted.
Null zxid means the server supplied no usable zxid. Notifications do not advance
the session's ordinary-reply watermark. Default handle/event formatting omits
path values. Chroot is applied to registration, removal and restoration, then
stripped from event paths at component boundaries.

Watch events are invalidations, not a complete history. Multiple changes before
rearming may be missed; reread application state after an event.

## Removal and ownership

`RemoveAsync(WatchRemovalMode.Local)` suppresses unread and future delivery
immediately and schedules bounded best-effort server cleanup. `DisposeAsync`
uses the same local behavior and does not wait for server acknowledgment. Ending
or canceling enumeration also disposes the logical handle. A value already
dequeued by a concurrent consumer cannot be recalled.

`RemoveAsync(WatchRemovalMode.ServerConfirmed, cancellationToken)` suppresses
locally first, then waits for a definitive server response. It returns
`ZooKeeperWatchRemovalResult` with one of these statuses:

| Status | Meaning |
| --- | --- |
| `LocalOnly` | Local suppression completed; server cleanup is unconfirmed |
| `ServerConfirmed` | The server acknowledged removal |
| `NoWatcher` | The server definitively reported no matching watch |
| `SharedRegistrationRetained` | Another active or pending logical handle needs the server registration, so no remove request was sent |

Existence and data handles at the same path share the server's data category;
child watches have a separate category. Removing one handle preserves other
handles. Registration and removal admission are ordered so an earlier cleanup
cannot remove a later re-registration.

A canceled or failed confirmation never revives local delivery. Confirmation
while disconnected fails with request-outcome metadata; local disposal still
works. Repeated disposal is safe. Local removal completes its local action even
if the supplied token is already canceled.

## Cancellation and reconnect

Cancellation before transmission reports `NotSent`. After transmission, a
watched read can have an `Unknown` outcome and a server-side subscription effect.
The driver retains its pending intent until the late reply or transport teardown.
A late success, including `Exists/NoNode`, suppresses orphan delivery and schedules
cleanup unless another handle needs the registration. Failed reads install no
successful local subscription. Application operations are never replayed.

Same-session recovery authenticates first, restores acknowledged watches, and
waits for all reset acknowledgments before publishing readiness. Restoration
uses one captured ordinary-reply zxid across bounded data/existence/children
batches. Events arriving before reset acknowledgment are buffered normally. A consumer
may receive such an event while the session is still restoring; await
`WaitForConnectionAsync` before issuing the follow-up read.
Local disposal wins over a captured restoration snapshot; unconfirmed lost-response
registrations are omitted.

Restoration cannot reconstruct every missed change. An absent node created and
deleted during disconnect can remain invisible. Persistent and recursive watches
and their reconnect-gap events are not implemented yet.

## Limits and verification

`MaxWatches` defaults to 10,000, with range 1–1,000,000. Pending registrations and
unread completed streams retain a handle slot until released. Cleanup metadata
is also bounded by this limit, so a new path can encounter local backpressure
while cleanup is outstanding. A watched path must fit a one-path restoration
frame after chroot expansion.

`MaxBufferedEvents` is shared across state and watch queues and counts every
logical recipient after fan-out. Consumers never run on the socket reader or
under the session lock. A slow consumer cannot block decoding or heartbeats.
Exhaustion faults the client with `ZooKeeperEventOverflowException`; events are
never silently dropped, overwritten or coalesced. Session state and terminal
stream completion remain observable. Recovery requires a fresh client and a
new application snapshot.

Controlled peers verify reader ordering, cancellation/timeouts, shared and
confirmed removal, global overflow, unknown lost responses, disposal during
restoration, and 10,000 long Unicode watches across bounded reset frames. Live
tests on 3.8.7 and 3.9.6 cover normal events, sharing, chroot, removal,
re-registration, close, and a real connection cut followed by same-session
restoration. Run `just zk-operations-matrix`; the `--watches` sample also runs on
every node. Broader quorum faults, live secure-server qualification, persistent
watches and release/AOT qualification remain open.

[Reference](README.md) · [Documentation home](../README.md)
