# Watches

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

## Persistent registration

`AddWatchAsync(path, PersistentWatchMode.Persistent, cancellationToken)` returns
an owned `ZooKeeperWatch`. Registration also works when the path is absent; it
returns no initial snapshot. Register first, then read the current state and
process buffered invalidations.

| Mode | Delivered invalidations |
| --- | --- |
| `Persistent` | Creation, deletion, data change and child-list change at the exact path |
| `PersistentRecursive` | Creation, deletion and data change at the path and descendants |

Persistent handles remain active after delivery, deletion and recreation until
removed or the session ends. Recursive routing follows path components: `/a`
does not include `/ab`. Recursive handles omit child-list events, including when
another handle causes the server to send those events. Each matching logical
handle receives one event. The compatibility profile governs mixing modes at
one path.

See [Watch repeated changes](../tutorials/watch-repeated-changes.md).

## Server compatibility

`CompatibilityProfile` defaults to `ZooKeeper38`, an operator assertion about
all endpoints. This profile permits multiple handles of the same persistent
mode at one path, but rejects mixing different persistent modes or a one-shot
registration at that exact path with `NotSupportedException`. Separate paths,
including an ancestor recursive watch and a descendant one-shot watch, work
together. ZooKeeper 3.8 stores only one mode per connection/path, so mixing
would silently overwrite a registration.

After removing the old mode, wait for server confirmation before changing
modes at that path. Outstanding cleanup or a canceled removal awaiting its
reply can produce `ZooKeeperBackpressureException`. Persistent removal uses
the common server removal category, and cleanup metadata remains bounded.

Set `CompatibilityProfile = ZooKeeperCompatibilityProfile.ZooKeeper39` only
when every endpoint is at least as capable as the pinned 3.9 server. This
profile permits independent one-shot, persistent and recursive registrations
at the same path and uses category-specific removal. During a rolling upgrade,
keep the least capable endpoint's profile. The driver does not discover the
server version or probe removal support.

## Handles and events

`ZooKeeperWatch` implements `IAsyncDisposable` and exposes stable
`RegistrationId`, client-relative `Path`, and `Mode` (`OneShotExists`,
`OneShotData`, `OneShotChildren`, `Persistent`, or `PersistentRecursive`). Each handle permits one consumer through
`ReadEventsAsync(cancellationToken)`. A second consumer throws
`InvalidOperationException`; separate registrations provide separate handles.

| Watch | Consuming invalidation |
| --- | --- |
| Missing existence read | Node creation |
| Present existence or data read | Data change or node deletion |
| Child-list read | Child-list change or watched-node deletion |

A one-shot stream buffers at most one event and completes after that invalidation.
There is no automatic rearming. A new watched read observes a new value and
establishes a new watch. State changes use `SubscribeStateChanges` and do not
consume one-shot watches.

`ZooKeeperWatchEvent` contains getter-only `RegistrationId`, `Kind`, `Path`,
nullable `Zxid`, and `ConnectionGeneration`. Kinds include node creation, deletion,
data/child changes, data/child/persistent watch removal, and local
`ReconnectedWithGap`. A persistent removal notification ends the affected stream. `None` is an unspecified value and is not emitted.
Null zxid means no server zxid is available, including for a local gap. Notifications do not advance
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
child, persistent and persistent-recursive watches have separate categories.
Removing one handle preserves other handles and independent categories. Registration and removal admission are ordered so an earlier cleanup
cannot remove a later re-registration.

A canceled or failed confirmation never revives local delivery. A definitive
cleanup rejection retains bounded metadata without continuously retrying; an
explicit confirmation retry or transport teardown can release it. Confirmation
while disconnected fails with request-outcome metadata; local disposal still
works. Repeated disposal is safe. Local removal completes its local action even
if the supplied token is already canceled.

## Cancellation and reconnect

Cancellation before transmission reports `NotSent`. After transmission, a
watched read or add-watch can have an `Unknown` outcome and a server-side subscription effect.
The driver retains its pending intent until the late reply or transport teardown.
A late success, including `Exists/NoNode`, suppresses orphan delivery and schedules
cleanup unless another handle needs the registration. Failed reads install no
successful local subscription. Application operations are never replayed.

Same-session recovery authenticates first, restores acknowledged watches, and
waits for all reset acknowledgments before publishing readiness. Restoration
uses one captured ordinary-reply zxid across bounded data/existence/children
batches, plus extended persistent/recursive batches. Events arriving before reset acknowledgment are buffered normally. A consumer
may receive such an event while the session is still restoring; await
`WaitForConnectionAsync` before issuing the follow-up read.
Local disposal wins over a captured restoration snapshot; unconfirmed lost-response
registrations are omitted.

Restoration cannot reconstruct every missed change. An absent node created and
deleted during disconnect can remain invisible. Every surviving persistent
handle receives `ReconnectedWithGap` after all restoration acknowledgments and
before application admission resumes. This local event has the watched path,
null `Zxid` and the new `ConnectionGeneration`. Reread or resnapshot application
state; restoration does not replay missed events. Events received during
restoration precede the gap in received order. Attempts that fail before
readiness do not publish a gap. One-shot streams do not receive gap events.

## Limits and verification

`MaxWatches` defaults to 10,000, with range 1–1,000,000. Pending registrations and
unread completed streams retain a handle slot until released. Cleanup metadata
is also bounded by this limit, so a new path can encounter local backpressure
while cleanup is outstanding. A watched path must fit a one-path restoration
frame after chroot expansion.

`MaxBufferedEvents` is shared across state and watch queues and counts every
logical recipient after fan-out, including reconnect gaps. Persistent streams
can buffer repeated events, with all their queues sharing this same budget. Consumers never run on the socket reader or
under the session lock. A slow consumer cannot block decoding or heartbeats.
Exhaustion faults the client with `ZooKeeperEventOverflowException`; events are
never silently dropped, overwritten or coalesced. Session state and terminal
stream completion remain observable. Recovery requires a fresh client and a
new application snapshot.

Controlled peers verify reader ordering, cancellation/timeouts, shared and
confirmed removal, global overflow, unknown lost responses, disposal during
restoration, and 10,000 long Unicode one-shot and persistent watches across bounded reset frames. Live
tests on 3.8.7 and 3.9.6 cover normal events, sharing, chroot, removal,
re-registration, close, and a real connection cut followed by same-session
restoration, persistent/recursive delivery, independent removal categories and
explicit reconnect gaps after missed create/delete intervals. Run `just zk-operations-matrix`; the `--watches` sample also runs on
every node. Broader quorum faults, live secure-server and release/AOT
qualification remain open.

[Reference](README.md) · [Documentation home](../README.md)
