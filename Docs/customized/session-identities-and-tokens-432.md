# Session identities and client tokens, issue #432

## Server changes

`Session` uses one process-wide atomic counter. Its identity does not depend on
the socket endpoint. The public identity stays a `uint`, which preserves the
current packet and manager contracts. A checked conversion rejects counter
exhaustion. It never wraps to zero or reuses an earlier identity in that process.

The Game and Stream connection tables reject a second connection with the same
identity. They close only the rejected connection. Receive and disconnect
callbacks also compare the exact `ISession` object. A callback from a rejected
or old socket cannot find or remove a different connection.

The counter is an identity source, not a secret source. Before this change,
Stream admission and Login reconnect used the Game identity as a token.
Both paths now use separate random, nonzero 32-bit tokens from
`RandomNumberGenerator`.

`StreamManager` binds each token to the exact Game connection. It checks the
account, authentication, open state, and connection table before a Stream join.
A wrong account does not remove another account's token. A Stream connection
cannot change to another Game connection after a successful join. The token
supports another Stream connection while its Game connection stays current.
Game disconnect removes the token by connection identity.

`ReconnectTokenManager` binds each pending Login acknowledgement to the exact
Game connection. A new request replaces the previous pending token for that
connection. The acknowledgement expires after 60 seconds and succeeds once.
The connection must still occupy the authenticated lobby state. Game sends the
same random token in `GLPlayerReconnectPacket` and `SCReconnectAuthPacket`.
The Go Login server already checks the account and server, expires the token,
and consumes it once when the client reconnects. No Go packet change is needed
for this path.

## Exact client evidence

The source client reports `version 208022` in `client/history.txt`.

| Input | SHA-256 |
| --- | --- |
| `client/bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| `x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| `client/bin32/crynetwork.dll` | `2e98d25290dd86bffeb48699ec84a9c8d219494ca47ed3fa264f508bf63f0438` |
| `crynetwork.dumped.dll` | `5115de707d457a85bd5ea95d78b6e069cba9d1c3f7b7b82dd41c8188995dbe11` |

The original and dumped `x2game` PE identity uses timestamp `543cb835`, image
base `38ff0000`, image size `01cb0a00`, and entry point `008c5d6d`.
The original and dumped CryNetwork identity uses timestamp `543cb849`, image
base `394f0000`, image size `005ae000`, and entry point `000aa8e9`.
These dumps predate this work. This work does not claim a new runtime capture.

CryNetwork retains pointers from runtime base `045c0000`. Independent string
references confirm that base: file offsets `ba858` and `ba870` contain
`pubKeySize` and `gm`. Their code operands are `0467a858` and `0467a870`.
The addresses below use the PE image bases shown above.

| Packet | Confirmed contract and evidence |
| --- | --- |
| Game to client `X2EnterWorldResponse`, level 1, opcode `000` | Parser `39576eb7` reads reason `u16`, GM `bool`, Stream cookie `u32`, Stream port `u16`, world field `u64`, public-key size `u16`, and public-key data. The cookie occupies object offset `14` and uses the 32-bit helper at vtable offset `4c`. Consumer `39575c2b` passes it to `39570ece`, which stores the unchanged DWORD at connection offset `34`. |
| Client to Stream `CTJoin`, opcode `001` | Producer `393d70e0` constructs opcode 1 after the Stream connection succeeds. Constructor `397ad270` copies the stored account and cookie to object offsets `10` and `14`. Serializer `397b1a20`, through vtable `399d4668`, writes account `u32` then cookie `u32`. The body contains 8 bytes. |
| Game to client `SCReconnectAuth`, level 1, opcode `001` | Factory `391a7350` allocates 16 bytes and assigns opcode 1. Parser `397ae860`, through vtable `399b2fe8`, reads one cookie DWORD at object offset `0c`. Registration `391eb9fe` selects handler `39185930`. That handler copies the cookie to authentication state offset `30`. The body contains 4 bytes. |

These values act as opaque 32-bit tokens. The client preserves all 32 bits.
The token changes do not alter packet widths, opcodes, or the other packet
fields. The current public-key encoding is outside this change.

The internal Go contract provides an independent check for Login reconnect.
`GLPlayerReconnect` contains server `u8`, account `u32`, and token `u32`.
`LGPlayerReconnect` echoes the token as `u32`. Its old C# local variable name,
`accountId`, did not describe that value correctly.

## Checks

`SessionTests` opens 2 TCP connections with the same client endpoint against
different local listener ports. Their remote endpoint hashes match, but their
Session identities differ. Concurrent construction also checks that identities
remain unique and nonzero.

`ConnectionIdentityTests` checks rejected Game and Stream sockets, synchronous
disconnect callbacks, exact socket lookup, and late callbacks after replacement.
`SessionTokenTests` checks account ownership, collisions, zero values, stale
connections, disconnect removal, concurrent requests, expiry, repeated replies,
and token values with their highest bit set.

The release record supplies the final build and test results.
Human client validation remains necessary. Check normal world entry, Stream
content access, character selection, server selection, and reconnect.
