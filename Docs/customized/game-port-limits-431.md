# Game port limits and bounded persistence

This change resolves aaemu-cluster issue #431 on the r208022 deployment lineage.
It adds operational limits. These values are server policy, not recovered retail
limits. It does not change the Game packet layout or the Stream port policy.

## Connection and packet limits

`GameNetworkLimits` exposes these settings in `Config.json` and the normal
config providers:

| Setting | Default | Behavior |
| --- | ---: | --- |
| `MaxConnections` | 1024 | Reject the next Game socket at this total. |
| `MaxConnectionsPerAddress` | 16 | Reject the next Game socket from this IP. |
| `AuthenticationTimeoutSeconds` | 10 | Close a socket without completed authentication. |
| `PacketsPerSecond` | 1000 | Close a socket above this count in a monotonic 1-second window. |

All values must be positive. IPv4 and its IPv4-mapped IPv6 form share one address
count. Admission and removal share one lock. Rejected or obsolete sessions cannot
release an accepted session's slot. Closing sessions retain their slots until
normal disconnect cleanup removes them.

The 10-second authentication timeout already existed. The change exposes its
value as a setting and preserves its authentication race protection. Incoming
bytes do not extend this deadline. The existing unknown-packet log budget also
remains at 3 events per connection.

The packet limit counts complete frames before dispatch, including unknown
opcodes. Fragmented input counts once after assembly. A TCP receive with several
frames counts each frame. Closed connections cannot dispatch later input. The
high default allows movement, transport, and UI bursts. It is a fixed window,
not a sliding window. Bursts on opposite sides of a boundary can each use the
window budget.

`Network.NumConnections` retains its existing server-load display meaning. It
is not the admission limit. Game admission does not apply these limits to the
internal Login link or the separate Stream listener.

## Exact-client UI contract

The analyzed client is r208022. Native evidence uses these binaries:

| Binary | SHA-256 |
| --- | --- |
| `x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| `client/bin32/xlcommon.dll` | `0e0881aa837553d7e307a5c6f9d886f3e82a0d72d94b6d666448aedaa94e2203` |

The original `x2game.dll` SHA-256 is
`3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205`.
The source and dump share PE timestamp `0x543cb835`, image base `0x38ff0000`,
and entry RVA `0x8c5d6d`. This work reused the established runtime dump. The source record for
this batch contains Ghidra exports and the static table disassembly. These are
local research artifacts, not client content in Git.

The following evidence is confirmed:

- `FUN_39217640` creates opcode `0x118` for CSSaveUIData. It takes the current
  character ID and calls `Data::SaveToBuffer` with capacity `0x2000`.
- `FUN_392178d0` visits UI keys 0 through 6. The table at `0x39acb8e0`, with
  stride 8, sends only keys 1 through 5 to the server. Keys 0 and 6 remain local.
  Key 7 is the native sentinel.
- `FUN_397acbb0` stores the key at native object offset `0x0c`, the character ID
  at `0x10`, and a `0x2000` byte buffer at `0x14`.
- `FUN_397bc4d0` serializes the key through the u16 helper at vtable `+0x40`,
  the ID through the u32 helper at `+0x3c`, and the string through `+0xdc`.
  The string length comes from `XlStringSize`. No fields follow it.
- `xlcommon.dll` export 480, `XlStringSize`, at RVA `0x199a0`, returns the byte
  count before the NUL. It does not include the NUL. The established same-helper
  music contract independently confirms the string form. See
  [r208022-music-note-limits-520.md](r208022-music-note-limits-520.md).
- The native read branch caps string bytes at `0x1fff` and then writes its local
  NUL. `OnResponseUIData`, `FUN_391e57e0`, also uses an 8192-byte native buffer.

The confirmed CSSaveUIData level-1 body is:

| Offset | Wire type | Meaning | Limit |
| --- | --- | --- | --- |
| 0 | u16 little-endian | UI key | 1 through 5 for server storage |
| 2 | u32 little-endian | Current character ID | Must equal the active character |
| 6 | u16 little-endian | UTF-8 byte count | 0 through 8191 |
| 8 | UTF-8 bytes | UI document | Exact byte count, no NUL or trailing fields |

The packet now rejects invalid keys, IDs, lengths, UTF-8, NUL bytes, truncated
fields, and trailing bytes before it changes character state. The server counts
bytes, not .NET characters. Malformed requests close that connection.

## UI persistence

UI updates change memory at once. One shared timer checks pending UI data each
second. Repeated writes to one key replace the pending value. The timer handles
at most 128 characters per tick and rotates by character ID. Each character has
at most 5 client-writable keys. Each character's SQL statement writes all changed
keys together. Each character has its own transaction on the shared connection.
The legacy column uses utf8mb3. A document with unsupported Unicode remains
pending, but it cannot prevent another character's document from committing.
The server does not silently change that document.

The timer reads current world characters under `PersistenceSyncRoot`. It keeps
no queue of departed characters. Character option access and normal saves use
the same gate. Dirty state clears only after a confirmed commit. Failed batches
retain dirty values for the next tick. A later value cannot be cleared by an
older acknowledgement.

Normal character saves and logout still write every option. Shutdown stops the
UI timer before Game connections drain through their normal saves. A hard
process stop can lose the latest pending UI changes. With fewer than 128 dirty
characters and a responsive database, this delay is at most about 1 second.
This replaces the old synchronous database write for every client packet.

## Suspicious-activity persistence

`SusManager.LogActivity` now queues a record without database work. A successful
return means that the queue accepted the record, not that MySQL committed it.
The queue holds at most 1024 records. A full queue rejects new records and counts
them for one aggregate warning on the next flush.

The queue rejects nonfinite position values before they can block a SQL batch.
The timer writes at most 128 records each second with one parameterized INSERT.
Each record keeps its original incident time. Category text has at most 64 UTF-16
code units. Description text has at most 4096 code units. Truncation does not
split a surrogate pair. These bounds keep the memory and SQL batch size bounded.

A failed write leaves its records in the bounded queue. Timer callbacks cannot
write concurrently. Shutdown stops producers, stops the timer, and drains all
accepted records in bounded batches. A failed final write stops the drain and
reports the error. An abrupt process stop loses queued audit records. An
uncertain database outcome can cause duplicate audit rows on retry. These
records do not control economic settlement or gameplay state.

## Validation

Focused tests cover concurrent admission, total and per-address caps, mapped
addresses, release of slots, all-frame accounting, fragments, monotonic reset,
large permitted bursts, unknown packets, and invalid settings. Existing tests
cover the authentication timeout and its race with authentication.

UI packet tests cover all 5 valid keys, native byte boundaries, every truncated
body prefix, ownership, invalid UTF-8, NUL bytes, and trailing bytes. UI batch
tests cover replacement, failed writes, stale acknowledgement, batch rotation,
and departed characters. Audit tests cover deferred writes, bounded queues,
bounded text, concurrent producers, retry, and shutdown drain.

The local MySQL tests use a new random test database on port 33306. They cover
UI transaction rollback, commit acknowledgement, isolation of an unsupported
Unicode document, replacement values, a full
logout-style option save, audit SQL parameterization, the batch size, and the
last shutdown batch.

The focused Release build passed. The 63 focused unit and regression tests passed.
The 3 local MySQL tests passed. The parent release work runs the full suites on
the combined release source.

Human validation remains necessary after deployment:

1. Log in, enter the world, and use normal combat and movement.
2. Change UI layout, key bindings, and quest tracker choices.
3. Wait at least 2 seconds, reconnect, and check those UI values.
4. Change a UI value and log out at once. Reconnect and check that value.
5. Repeat the normal client login after a Game restart.

The broad movement test cannot prove every possible legitimate packet burst.
Record any disconnect with the release and Game log before changing the limit.
