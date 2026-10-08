# Garden status and world sign review: #639 and #653

## Result

The exact r208022 client contradicts the proposed fixes in these 2 reports.
`SCHouseFarmPacket` prints a chat notice. It does not open a garden window.
World signs get each sign arm's localized name from the client compact.
Their server phase function does not need to replace the doodad name.

This change adds comments and packet contract tests. It adds no packet sends,
crop counts, client patches, or changes to persistent data.

## Sources

The server baseline is AAEmu `70079df5376511cbf769a217409b48d0770693c2`.
The client history states `version 208022`.

| Source | SHA-256 |
| --- | --- |
| Source `bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| Runtime dump `x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| Client compact | `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4` |
| Server compact | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |
| Current `game/scriptsbin/x2ui/interaction.alb` | `d80396f8ffd7759d07fdbaf30ec0a47881a6efe95848044200f51240b771f18b` |
| Current `game/scriptsbin/x2ui/chat/chat_msg_event.alb` | `710aa35b40a8e64e37feee7f23a38ce7f6b36cd141f083b418137168c68445fd` |

Both native files have image base `38ff0000`, image size `01cb0a00`, entry point
`008c5d6d`, and PE timestamp `543cb835`. The dump is the pre-existing research
input. This review did not create a new runtime dump.
All native addresses below use that image base. Analysis used a private copy
of the Ghidra project in read-only mode. Both scripts were extracted again
from the installed `game_pak` for this review.

The earlier chat script has SHA-256
`e16b3585c9d6993e2dc7c7f584620539c68620d1a2df45b98856c35517d06894`.
Its farm handler has the same 13 instructions as the current script.
The current script omits local variable names, but keeps this handler's
operation and argument order.

## #639: the confirmed packet contract

`SCHouseFarmPacket` is Game to Client, level 1, opcode `0x0c3`.

| Body field | Wire type | Meaning |
| --- | --- | --- |
| Name byte count | `uint16` | Length of the UTF-8 name |
| Name | UTF-8 bytes | Label for the chat notice |
| Total | `int32` | Denominator in the chat notice |
| Harvestable | `int32` | Numerator in the chat notice |

The body has `10 + nameByteCount` bytes. It has no house ID, crop list,
window selector, or trailing group.

Confirmed native chain:

1. Registration `391c0be0` selects dispatch table `399b5070`.
2. Factory `391acf70` sets opcode `0xc3` and allocates `0x98` bytes.
3. Parser `397aece0` reads the name, total, and harvestable fields in that order.
4. The parser gives the name reader a capacity of `0x80` bytes.
5. Handler `391d7ba0`, named `OnHouseFarm`, passes these fields to `39326010`.
6. Function `39326010` emits event `0xfe`, `HOUSE_FARM_MSG`.
7. The chat script formats `[%s] %d/%d` with name, harvestable, and total.
8. The script sends that text to `X2Chat:DispatchChatMessage` in `CMF_ETC_GROUP`.

The native object stores the total at `+0x0c`, harvestable at `+0x10`, and
name at `+0x14`. The allocation independently fits the bounded name and
its terminator. Distinct test values protect the wire order from confusion
with the reversed display order.

### The proposed garden-info trigger is not confirmed

Both compacts have 368 `doodad_func_house_farms` rows and 368 matching
`doodad_phase_funcs` references. Of those references, 282 join to current
phase groups. Those groups cover 147 doodads.
Examples include mature Pine and Avocado trees, coral, cows, and mineral veins.
The table's only value after the ID is `item_category_id`.
A row is a phase descriptor. It does not identify a house, window, or request.

`DoodadFuncOpenFarmInfo` is a different function. Its 4 authored definitions
select public-farm metadata by `farm_id`. Native dispatcher `393bc260`,
case `0x48`, calls `393b8700`. That function emits event `0x102` with the
configured farm ID, then returns a local-success result. The earlier review in
[Housing-Limits-Public-Farms-497-500.md](Housing-Limits-Public-Farms-497-500.md)
records that path. The current server handler already preserves the board
and does not treat authored `next_phase=-1` as deletion.

The server's house visibility path sends house state and attached doodads.
The doodad visibility path sends template and phase state. The public-farm
list request uses `SCResponseCommonFarmListPacket`. None of these paths
establishes a retail trigger for the house-farm chat notice.
The local Git history search found no `new SCHouseFarmPacket` or
`new TCHouseFarmPacket` sender in any available ref.

The notice's retail send trigger remains **unknown**. The inspected code does
not prove whether its name represents a house, a crop, or an item category.
It also does not prove the exact objects that contribute to each count.
Do not derive a new whole-house counter or send a notice on every crop phase
change from this packet's name alone.

Issue #639's garden-window premise is unsupported. A future request for this
chat notice needs a retail capture or other source that proves its trigger
and count membership. The same event also has a confirmed Stream path. Factory `393d7a40` creates
opcode `0x0e`, `TCHouseFarmPacket`. Parser `397b1990` reads the same body.
Handler `393d5f30` forwards the name and
both counts to `39326010`. The shared UI consumer does not establish when
a retail server sends either packet. The dormant Stream writer is not a
garden request or evidence of a separate garden window.

## #653: world sign names belong to the client

The client and server compacts each contain 516 sign definitions.
Native loader `3966e320` reads `id`, `name`, and `pick_num` from
`doodad_func_signs`. It resolves `name` through localization function
`395b1350` and stores the localized name with its pick number.

Phase loader `39674d30` maps `DoodadFuncSign` to native phase kind `0x10`.
The hover path `393b51a0` gets the doodad template and its current phase.
It calls `393b4bf0` directly or through `393b4da0`.
Function `393b4bf0` finds the sign record for that phase and pick number.
It emits event `0x1a7`, `DRAW_DOODAD_SIGN_TAG`, with the localized name.

The interaction script's handler, at source lines 388 through 401, clears
and fills the fixed tooltip with that name. It shows the tooltip as a doodad
hint. An empty event hides the tooltip.
This path does not need a new Game packet or a server rename.

A single signpost can have several names. For example, doodad `1874` uses
phase `3642`, with sign IDs `6`, `7`, and `8`, and pick numbers `1`, `2`, and `3`.
Sign `8` is the Windshade Village entry. A single `Doodad.Name` value cannot
represent all 3 sign arms.

`Doodad.Write` already sends `TemplateId` and `FuncGroupId`.
Those IDs select the authored client records. The current static spawn for
`1874` is in `main_world` at `(12465.114, 15162.776, 145.6056)`.
Its yaw is `-41.1658` degrees. The server no-op is correct for this path.

## Validation and manual checks

`SCHouseFarmPacketTests` covers the empty notice, distinct count values,
a UTF-8 name, and the native name capacity. Each case reads every byte
in wire order and checks that no bytes remain. The shared batch build and
unit-test result belongs to the release record.

No live client check was completed during this review. Record these checks
in the single human validation issue, aaemu-cluster #573:

1. Use the current r208022 client and enter the world.
2. Approach a signpost with several arms, such as doodad `1874` at the coordinates above.
3. Point at each arm and check its separate localized place name.
4. Move the pointer away and check that the tooltip disappears.
5. Reconnect and repeat the sign check.
6. Open a public-farm board and check that its normal info window still works.
7. Close and reopen that window, and check that the board remains present.

An empty garden and a harvestable crop are not valid window checks for
`SCHouseFarmPacket`. The client has no garden window on the confirmed packet
path. Do not record those proposed checks as passes.
