# Account sessions for r208022

This change resolves cluster issues [428](https://github.com/KeganHollern/aaemu-cluster/issues/428)
and [430](https://github.com/KeganHollern/aaemu-cluster/issues/430).
It uses the same release as the world cookie, socket identity, and character creation changes.

## Packet states

The server owns the connection state. A packet cannot select its own state.
GameProtocolHandler checks each frame before the packet reader runs.
An invalid state closes the connection and writes the opcode, level, and state to the log.

| State | Accepted game packets |
| --- | --- |
| Connected | X2EnterWorld only, at level 1, with no account or character |
| Lobby | Character list, refresh, creation, edit, deletion, deletion cancel, and selection |
| CharacterSelected | CSSpawnCharacter, subzone notification, and instance load |
| EnteringWorld | CSNotifyInGame, subzone notification, and instance load |
| World | Gameplay packets, instance load, CSNotifyInGame as a no-op, and CSNotifyInGameCompleted |

Authenticated states also accept the registered CryNetwork transport packets at level 2.
Account UI data, restriction checks, second-password requests, return-address reports, and labor-character requests can precede world entry.
CSLeaveWorld accepts Lobby or World. Its target still selects the applicable path.
All packets that need a character check that the character belongs to the authenticated account.

Character selection binds one character before its load starts.
CSSpawnCharacter advances the state before it sends unit state or adds subscribers.
CSNotifyInGame advances the state before it spawns the character.
A repeated World notification writes a bounded rejection log and does not spawn again.
CSNotifyInGameCompleted runs the server join hook once per selection.
Later completion packets refresh the client snapshots.
Return to the lobby clears the character and the completion flag.
Instance load and teleport keep the selected character and do not repeat selection.

Packet dispatch and disconnect use the connection lock.
A delayed leave uses its original character and cancellation token under that lock.
A stale leave callback cannot remove a later character.

## Duplicate login and disconnect

Each account has one active Game connection.
A new authorized handoff stores a pending cookie while it holds the account admission lock.
It sends the old session `SCKickedPacket(KickDuplicateAccount, "")`, saves the character, and closes the old socket.
It acknowledges the new cookie only after the old save completes.
Cookie consumption uses the same admission lock.

Admission requests also have an arrival generation.
An older moderation reply cannot replace a newer cookie or disconnect the newer session.
Account removal compares the exact connection object.
A delayed disconnect from the old socket cannot remove the replacement connection.

Each disconnect cleanup step catches and logs its own failure.
The character save includes the departing character explicitly in `TryCommitEconomy`.
It commits the wallet and pending items together.
A cleanup error does not skip the save, account removal, token removal, or socket-table removal.
A failed save rejects the immediate replacement handoff and writes an error.
This change does not add recovery for a failed database save.

NetCoreServer 8.0.7 calls disconnect callbacks synchronously, including from its send lock.
The callback marks the connection closed at once.
If a packet owns the connection lock, the callback queues cleanup until that packet finishes.
Explicit duplicate-login cleanup still waits for the save.
The authentication timeout releases its authentication lock before it closes the socket.

## Exact-client evidence

The input is ArcheAge r208022.
The original `x2game.dll` SHA-256 is
`3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205`.
The existing runtime dump SHA-256 is
`a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0`.
The dump predates this work. This work does not claim a new runtime capture.
Addresses below use its image base `0x38ff0000`.

| Packet | Level and opcode | Native evidence |
| --- | --- | --- |
| CSSelectCharacter | 1, 0x025 | Producer 0x391eae1c through 0x391eae82, vtable 0x399b2ebc, serializer 0x397c74d0 |
| CSSpawnCharacter | 1, 0x026 | Producer 0x391a4090 through 0x391a40cd, vtable 0x399b46b4, visual option serializer 0x397ab6a0 |
| CSNotifyInGame | 1, 0x029 | Producer 0x39186f70, empty body serializer 0x391845c0 |
| CSNotifyInGameCompleted | 1, 0x02a | Producer 0x391e73a4, empty body vtable 0x399b2ed4 |
| SCKicked | 1, 0x1a0 | Handler registration at 0x391ece13, handler 0x391dc4d0 |

The client clears its pending notify timestamp at `0x391e72f6` before it sends CSNotifyInGame.
The completed path clears its next timestamp at `0x391e7392` before it sends CSNotifyInGameCompleted.
SCLoadInstance handler `0x391de970` calls `0x3936f1c0`.
That function can send CSNotifySubZone before CSInstanceLoaded at `0x3936f264` and `0x3936f2f8`.
Both packets accept the character load states.
Local-unit binding `0x39187180` can also schedule NotifyInGame at `0x391872b5`.
The native timer has no per-selection flag, so a repeated owned World notification remains a no-op.
Engine callback `0x390f35e0` also schedules Completed through `0x391825b0` after a teleport.
The server accepts those world-state notifications without a second join hook.
The kick handler maps reason 0 to `kick_by_login`, reason 1 to `kick_by_gm`, and reason 2 to `kick_by_maintenance`.
An empty message selects that native reason text.
These changes keep the packet bodies and opcodes unchanged.

The dependency source pins NetCoreServer to commit
`cb58a43ba0bbce6182d9a5259388c75763d63db7`.
`TcpSession.Disconnect` calls `OnDisconnected` directly.
`SendAsync` can reach that callback while it holds `_sendLock`.
The callback regression tests use these synchronous call paths.

## Tests and client checks

The regression tests cover wrong packet states, selection races, replay, return to lobby, duplicate handoff, and late disconnect.
They also cover cleanup failure, concurrent disconnect, pending item persistence, reversed moderation replies, and synchronous socket callbacks.
Run the unit suite and the `GameMySql` integration suite.
The release record contains the exact commands and results.

After release, do these client checks:

1. Log in, select a character, and enter the world.
2. Return to character selection and enter with another character.
3. Return to server selection and connect again.
4. Teleport and enter an instance. Check that movement and UI state still work.
5. Start another login for the same account. Check that the old client shows the duplicate-login message.
6. Check the character wallet and inventory after the new login.

Automated tests do not replace these client checks. Human gameplay validation remains a release follow-up.
