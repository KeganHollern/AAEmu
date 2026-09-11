# Staff moderation and Game authentication

This change covers cluster issues #427 and #434. Login stores account bans,
mutes, expiry, actors, reasons, and history. Game checks roles and enforces live
state. The client packet bodies do not change.

## Roles and commands

Moderator and Admin can moderate Normal Players. Only Admin can moderate staff.
Game reads current actor and target roles immediately before request dispatch.
An authorized request can complete after a later role change. This is a
request-time rule, not a distributed transaction with role changes.

Commands accept a character name, character ID, or `account:<id>` target.
Account targets also support users who never entered Game.

- `/ban <target> <duration> <reason>`
- `/unban <target> <reason>`
- `/mute <target> <duration> <reason>`
- `/unmute <target> <reason>`
- `/kick <target> <reason>`

Duration is `permanent` or a positive count with `s`, `m`, `h`, or `d`.
The maximum duration is 315360000 seconds. Reasons contain 1 to 512 UTF-8 bytes
without control characters.

Ban and mute commands first report `pending`. Login confirmation completes the
same command audit row. A timeout reports `unconfirmed`, because Login can commit
before a response is lost. A confirmed rejection reports `rejected`.

Kick saves and removes the character, then closes the actual socket. The socket
also closes when notification or save fails. The client kick notification alone
does not enforce removal.

## Private Login/Game protocol

These are cluster-private extensions. They are not native client opcodes and do
not need a client patch. Both server implementations and their byte tests define
the contract. All integers use little-endian encoding.

| Direction | Opcode | Body |
| --- | --- | --- |
| Game to Login | `0x0005` | ModerationRequest |
| Login to Game | `0x0005` | ModerationResult |
| Login to Game | `0x0006` | ModerationState |

Request fields, in order:

| Field | Type |
| --- | --- |
| requestId | u64, random and nonzero |
| actorAccountId | u32 |
| actorCharacterId | u32 |
| targetAccountId | u32, positive |
| action | u8 |
| durationSeconds | u64 |
| reason | u16 UTF-8 byte count, then bytes, without a terminator |

The fixed request prefix is 31 bytes. Actions are ReadState=0, Ban=1, Unban=2,
Mute=3, and Unmute=4. ReadState needs zero actor IDs, zero duration, and an empty
reason. Mutations need positive actor IDs. Unban and Unmute need zero duration.

Result fields are requestId u64, status u8, then the 30-byte state below.
Its body is exactly 39 bytes. Status is Success=0, InvalidRequest=1,
TargetNotFound=2, Unavailable=3, or RequestConflict=4.

State fields, in order:

| Field | Type |
| --- | --- |
| targetAccountId | u32 |
| revision | u64, persisted Login history ID |
| banned | u8 Boolean |
| banUntil | u64 Unix seconds |
| muted | u8 Boolean |
| muteUntil | u64 Unix seconds |

Boolean values must be 0 or 1. A true flag with expiry 0 is permanent. False
flags use expiry 0. Game checks timed expiry on every admission and chat attempt.
An older revision cannot replace a newer state. Login broadcasts committed state
before it replies to a mutation. A repeated request does not repeat the mutation.

The shared UTF-8 request fixture is:

```text
08070605040302014433221188776655ccbbaa99013c00000000000000030041c3a9
```

The shared result fixture is:

```text
090000000000000000443322110807060504030201013c00000000000000010000000000000000
```

The Go fixtures are in
`server/internal/login/gameproto/moderation_test.go` in the cluster repository.
The Game fixtures are in `AAEmu.UnitTests/Game/Core/Packets/ModerationPacketTests.cs`.

## Session state

Before the enter-world acknowledgement, Game requests fresh account state from
Login. Game creates the pending token only after that state permits admission.
The actual Game login checks the latest state again. A ban closes all local
sessions for the account, including lobby and reconnect sessions. Each Game
server also refreshes live account state after its Login link reconnects.

Only level-1 X2EnterWorld can enter an unauthenticated Game socket. The socket
closes after 10 seconds without authentication. Authentication needs a positive
account ID and the matching single-use pending token. Repeated authentication,
account-0 lobby handlers, and account-0 load or disconnect database work are
rejected. Muted accounts cannot send any normal chat channel message. Commands
still use their separate role checks.

## Validation

Unit tests cover exact private bodies and shared Go bytes, truncation at every
byte, trailing data, invalid flags, UTF-8 reason bounds, target protection,
expiry, stale state, pending command results, live eviction, preauth packet
rejection, timeout, account 0, and save-before-close ordering. Channel tests
cover muted say, whisper, party, raid, guild, family, nation, faction, trial,
trade, shout, and group-finder chat. A cooldown test confirms that demotion
disables a previously enabled cooldown override.

Human validation must use the published release. Check timed and permanent bans,
fresh-session mutes, unmute, expiry, forced socket closure, and staff target
protection. No human client validation is claimed by this source change.
