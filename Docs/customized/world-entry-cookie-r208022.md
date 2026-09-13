# r208022 world-entry cookie

This change resolves aaemu-cluster issue #429. Login creates a random cookie.
Game checks the cookie, account, expiry, and source address before admission.
The change uses the normal paired Login and Game release.

## Exact client evidence

The client history reports `version 208022`. These artifacts predate this task.
This task did not capture a new client process.

| Artifact | SHA-256 |
| --- | --- |
| Original `client/bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| `.tools-re/dumps/x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| `.tools-re/dumps/crynetwork.dumped.dll` | `5115de707d457a85bd5ea95d78b6e069cba9d1c3f7b7b82dd41c8188995dbe11` |

The original and dumped x2game PE headers agree. They show timestamp
`543cb835`, image base `38ff0000`, entry point `008c5d6d`, and image size
`01cb0a00`. The CryNetwork PE base is `394f0000`. Its runtime pointers use
`045c0000`. The `cl_world_cookie` and `cookie` string references independently
confirm that runtime base.

The 32-bit cookie contract is **confirmed** by these native paths:

- x2game handler `393d50c0` reads the cookie as a DWORD at packet object `+8`.
  It calls `393d4fe0`, which stores the value at `+2c` and sets
  `cl_world_cookie` at `393d501f`.
- CryNetwork producer `3957ac88` gets `cl_world_cookie` through the integer
  getter at `3957ada9`. Constructor `3957c688` stores that DWORD at object `+18`.
- Serializer `3957fb61` passes object `+18` to the 32-bit serializer at virtual
  offset `+4c`. The next field, the zone ID at `+1c`, uses the same helper.
- The Game `X2EnterWorldPacket` reader reads the cookie with `ReadUInt32`
  after the account ID. Login `WorldCookie.Encode` writes its first field
  with `U32`. The high bit stays part of the secret.

The client wire bodies do not change. The private Login-to-Game contract changes
because it belongs to this paired server release.

## Private admission body

`LGPlayerEnter`, opcode `0x0001`, has exactly 44 body bytes. Numeric fields use
little-endian byte order. Patron dates use unsigned UTC seconds.

| Offset | Width | Field |
| --- | --- | --- |
| 0 | 4 | Account ID |
| 4 | 4 | Request correlation ID |
| 8 | 4 | Random world cookie |
| 12 | 8 | Patron start |
| 20 | 8 | Patron end |
| 28 | 16 | Client address in network byte order |

IPv4 addresses use their IPv4-mapped IPv6 form. Game normalizes them before
comparison. The parser rejects zero identifiers, unspecified addresses, invalid
Patron periods, truncated bodies, and trailing bytes.

The Go and C# tests share this exact body:

```text
2A00000078563412D4C3B2A100F153650000000000D2496B0000000000000000000000000000FFFFC0000201
```

It contains account 42, correlation `12345678`, cookie `a1b2c3d4`, Patron dates
1700000000 and 1800000000, and address `192.0.2.1`. The Game reply echoes the
correlation ID. Login sends the cookie to the client only after that reply.
Each new Login request gets a new correlation ID, including after cancellation.
A late reply cannot acknowledge the next request on the same connection.

## Server policy

`PendingWorldAdmissions` permits one pending cookie per account. It rejects
random token collisions without replacing another account's cookie. A new
cookie for the same account removes its previous cookie. The cookie expires
at 60 seconds and permits one successful consumption.

`Network.ValidateWorldCookieAddress` defaults to `true`. Both public TCP paths
must preserve the same source address for this check. The cluster LAN Services
use `externalTrafficPolicy: Local`. An installation with different translated
addresses must set this Game option to `false`. Account, cookie, expiry, and
failure-count checks still apply when address comparison is off.

Each failed cookie attempt closes its socket. After 3 failures from an address,
Game rejects further attempts until that 60-second failure window ends. IPv4
and IPv4-mapped IPv6 share a count. The failure cache holds 4096 addresses. It
removes expired entries and removes the oldest entry if the cache is full.
The code does not log cookies. No database schema changes are needed.

## Tests and manual checks

Automated tests cover the private packet body, all truncation lengths, trailing
data, identifiers, address normalization, expiry boundaries, single use,
concurrent consumption, account and address mismatches, replacement, collisions,
and failure-window expiry. Go tests check the address and cookie sent through
the private handoff and public reply. They also check fresh correlation after
cancellation.

Human client validation remains open. Use the published paired release for
these checks:

1. Log in and enter the character lobby through the normal public endpoints.
2. Select another server, return, and enter the lobby again.
3. Repeat world entry after a canceled request.
4. Delay the Game connection for more than 60 seconds and check its rejection.
5. Start a fresh login after the expired cookie and check normal entry.

No human client validation is claimed by this change.
