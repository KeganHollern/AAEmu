# Social packet contracts for r208022

This record supports aaemu-cluster issues #510, #511, and #521.
The source changes remain downstream. An upstream submission needs separate approval.

## Inputs

The client history states `version 208022`.
The source `client/bin32/x2game.dll` SHA-256 is
`3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205`.
The local runtime dump `.tools-re/dumps/x2game.dumped.dll` SHA-256 is
`a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0`.
Both files use PE timestamp `543cb835`, image base `38ff0000`, and image size `01cb0a00`.
The dump predates this task. This task did not repeat its capture.
All addresses below refer to that dump.
GNU `objdump -d -M intel` supplied the native disassembly.
Lua 5.1 disassembly supplied the X2UI behavior.
Client files and disassembly stay outside Git.

## Guild rename

The X2Faction `RenameExpedition` registration at `394742ea` selects
`39472d70`, which calls `392f8f50`.
The producer takes the current guild ID from `39d44be8`, the requested name,
and a constant true `isExpedition` flag.
It creates opcode `0x009` and calls constructor `397ab420`.
The constructor stores the ID at object offset `0x0c`, a 129-byte name buffer
at `0x10`, and the flag at `0x91`.
Vtable `399ccca0` selects serializer `397b9100`.
The serializer confirms the following body.

| Order | Type | Value |
| --- | --- | --- |
| 1 | u32 | Guild ID |
| 2 | length-prefixed string | Name, at most 128 encoded bytes |
| 3 | bool | `isExpedition`, true for this producer |

The server keeps its current guild name rule, including its 32-character limit.
The server rejects a false expedition flag and a guild that the sender does not own.
No nation rename behavior enters this change.

The `SCFactionRenamedPacket` factory at `391a7a50` creates opcode `0x011`,
with object size `0x94` and vtable `399b309c`.
Its parser `397b2be0` reads u32 ID, name with 128-byte maximum, and bool `byGm`.
The client field name at `39a12c88` confirms `byGm`.
Handler `391d23f0` passes those values to `392fdce0`.
That function changes the named faction in the cache and refreshes matching visible tags.
An ordinary owner rename sends `byGm=false`.

## Guild disband

`OnExpeditionDismissed` handler `391d2710` reads the ID at object offset
`0x0c` and success at `0x10`. It calls `392fddf0`.
That function returns for failure or an unknown faction.
It removes only the specified faction through `398a41e0`.
At `392fde61`, it compares the received ID with the local guild ID before
it clears local guild state through `392f8040`.
Its visible-object loop compares each object's guild with the received ID at
`392fded4` before it clears the tag.
This confirms that a server-wide successful disband event does not remove another guild.

## Team markers

The exact X2UI popup script calls `X2Team:IsTeamOfficer` for raid marker access.
The registration at `394b2759` selects `394b0fb0`.
That wrapper converts the 1-based team slot to a 0-based slot and calls `39309980`.
The lookup `39305000` resolves the member ID from the team slot.
`39309980` returns true only when that ID equals the owner at `39d44c54`.

`UnitTeamAuthority` uses `394bd670`. It checks `39308930` for `leader` and
`39309980` for `subleader`. Both native functions compare the member ID with
that same owner ID in this build. No distinct assistant state exists in this path.
An overhead icon does not confer authority.
The server therefore permits markers for party members and for the raid owner.
The ticket's separate assistant assumption does not match this r208022 implementation.
No packet body changes are necessary for the marker or Dismiss guards.

## Server policy and tests

Kegan selected a limit of 100 guild members on 2026-09-12.
The exact client has the `expedition_member_limit` error, but this research
found no numeric guild cap in its producer or UI.
The 100-member limit is an explicit server policy.

Team tests cover outside and member Dismiss rejection, valid owner disband,
invalid targets and actions, party membership, and raid owner permission.
They also cover atomic rejection of an outside loot master and valid member selection.
Guild tests cover packet bodies, rename ownership, cache removal, and persistence.

Human validation still needs the published server and the exact client.
Check party marker access, raid leader markers, guild rename, and disband tags.
Check a second character from another guild during rename and disband.
