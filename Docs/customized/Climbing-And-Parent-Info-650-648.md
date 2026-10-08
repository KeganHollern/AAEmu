# Climbing and parent information in r208022

This record covers cluster issues [#650](https://github.com/KeganHollern/aaemu-cluster/issues/650)
and [#648](https://github.com/KeganHollern/aaemu-cluster/issues/648).
The source baseline is `4d0f9ff2cdd6bbd0e01e07305ac6879820aaff81`.
The review date is 2026-10-08 UTC.

## Result

The client already starts the climb controller from `DoodadFuncClimb` data.
The server already handles attachment and movement through separate packets.
The empty descriptor callback does not prevent a climb.

The review found a different defect in `CSHangPacket`.
The packet called `Doodad.Use(0)` after attachment.
That call could restart a tree's phase timer or record crop theft for a climb.
The packet now changes attachment state without that call.
Hang and unhang requests can only change the sender's character.

The client also handles `DoodadFuncParentInfo` through a separate path.
It resolves the house, requests tax data, and opens the house window.
The server already supplies the parent and house references in doodad creation.
No new parent-info packet, name change, or descriptor action is needed.

## Exact inputs

`client/history.txt` identifies revision `208022`.
The native addresses below use image base `0x38ff0000`.
The source DLL and its dump share PE timestamp `0x543cb835`, entry RVA
`0x008c5d6d`, and image size `0x01cb0a00`.
The dump is the pre-existing local research dump. This review did not create it.

| Input | SHA-256 |
| --- | --- |
| Source `x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| Dump `x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| Client compact | `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4` |
| Server compact | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |

The databases were read-only inputs. The change does not change either database.

## Confirmed climb path

- Native loader `39673b90` maps `DoodadFuncClimb` to function kind `1`.
- The compact has 156 climb functions in 155 phase groups. None has a nonzero function skill ID.
- Native action `393b8f40` checks the local character and climb target.
  It calls `39363410` to start the local climb controller.
  It then sends opcode `0xcb` with the local character ID and target ID.
- `39363410` contains the label `X2::GameClient::ClientUnit::HangClimbDoodad`.
  It reads the climb descriptor and passes the climb type to `390c1330`.
- `397ac600` stores the 2 object IDs. Serializer `397bb360` writes each ID in 3 bytes.
- Native producer `39187350` sends opcode `0xcc` with the local character ID and unhang reason.
  Serializer `397bb400` writes the 3-byte ID and 4-byte reason. It does not write a target ID.
- Client handler `391db660`, named `OnHung`, starts the remote unit's climb controller.
  Handler `391db6e0`, named `OnUnhung`, passes the saved target and reason to the unhang path.
- Shared movement serializer `397c0470` includes `gcId` when actor flags have `0x20` or `0x40`.
  It includes the 32-bit `climbData` only when `0x40` is set.
  `UnitMoveType` already reads and writes those fields.
- `CSMoveUnitPacket` accepts character climb movement and relays `UnitMoveType`.
  `Character.AddVisibleObject` already sends `SCHungPacket` for a current sticky parent.

All packets below use level `1`. The byte offsets exclude framing and the opcode.

| Packet | Direction | Opcode | Body |
| --- | --- | --- | --- |
| `CSHangPacket` | Client to Game | `0xcb` | Offset 0: character `u24`. Offset 3: target `u24`. Total 6 bytes. |
| `CSUnhangPacket` | Client to Game | `0xcc` | Offset 0: character `u24`. Offset 3: reason `u32`. Total 7 bytes. |
| `SCHungPacket` | Game to client | `0x137` | Character `u24`, target `u24`. Total 6 bytes. |
| `SCUnhungPacket` | Game to client | `0x138` | Character `u24`, target `u24`, reason `u32`. Total 10 bytes. |

The native producers and serializers confirm both changed C2G contracts.
The native handlers confirm the G2C value consumers.
This change preserves the G2C packet bodies and all target object types.
It does not add a guessed distance limit or change climb animation rules.

## Confirmed timer defect

`Doodad.UseLocked(0)` reaches `DoChangePhase` for the current phase even when it finds no loot function.
`DoPhaseFuncs` cancels the current task and runs the phase functions again.
`DoodadFuncTimer.Use` then calculates a new deadline from the current time.

For example, Yew Tree template `408`, phase `2858`, has climb function row `12166`.
That phase also has timer `3350` with delay `172800000` milliseconds.
Thuja Tree template `1603`, phase `2597`, has climb row `12196` and timer `2259`.
Its timer delay is `259200000` milliseconds.
A climb must not restart these 2-day and 3-day timers.

The same `UseLocked(0)` path treats another character's young plant as a skill-less theft interaction.
The native climb message does not request loot or a crop operation.
The removed call therefore had unrelated timer and crime effects.

## Confirmed parent-info path

- Native loader `39673b90` maps `DoodadFuncParentInfo` to function kind `0x43`.
- The compact has 31 parent-info functions. Every function uses skill `15212`, the building-info action.
- Those functions reach 15 template IDs with client records.
  Examples include Official Nameplate `3400`, Building Plaque `2392`, Scarecrow Garden `973`, and For Sale Sign `6760`.
- Native dispatcher `393bc260`, case `0x43`, calls `393b8400` and consumes the local action.
- `393b8400` first reads the child object's parent ID at offset `0x88`.
  If that ID is absent, it resolves the house through the owner reference at offset `0x3a0`.
  It then finds the parent unit and calls `393befd0`.
- `393befd0` requests opcode `0x5c`, `CSRequestHouseTaxPacket`, using the house's transient ID.
  It emits the house-interaction UI event with the house name and category.
- X2UI `housing/housing_manager.alb` handles `HOUSE_INTERACTION_START` and `HOUSE_TAX_INFO`.
  It updates the normal house window. `maintain_window.alb` also reads the house sale information.
- `DoodadManager.Create` supplies `ParentObjId`, `OwnerType.Housing`, and `OwnerDbId` for house children.
  `House.CompleteConstructionStepChange` attaches each bound doodad to the house.
- `Doodad.Write` supplies the parent object ID near the start of the create body.
  It also supplies the owner type and database house ID near the end.
  Attached doodads use local coordinates.
- `House.AddVisibleObject` sends the house state before its attached doodads.
  `SpawnManager` restores a saved child from its persistent house ID and uses the house's new runtime object ID.
- Sale markers intentionally use world coordinates and no transform parent.
  `HousingManager.SetForSaleMarkers` supplies their housing owner type and database house ID for the owner lookup.

The source lifecycle and native action agree. The report's empty `Use` method does not show a missing server action.

## Automated checks

The Release build passed. All 18 focused tests passed.

`ClimbPacketTests` checks these results:

- A valid hang preserves the world position, phase, and growth deadline.
- A climb on another character's tree does not enter the loot or theft path.
- A client cannot attach or detach another character.
- Truncated bodies, trailing bytes, missing targets, self-attachment, and inactive connections do not change attachment state.
- Unhang uses the saved target and preserves reasons `0`, `2`, and `7` in the response.
- Both responses contain the full expected body without trailing bytes.

`DoodadParentInfoPacketTests` checks single and batch create bodies.
It covers an attached sign and a free-standing object with a housing owner reference.
It checks the entire body, normal interaction flag, local position, and house references.

## Limits and pending manual checks

The source and native paths above are confirmed static evidence.
This review did not include an in-client playtest.
Animation quality, moving-ship ladders, and concurrent horizontal climbers remain manual checks.
No new rule for those cases is inferred from the empty handler.

Record these checks in [HUMAN VALIDATION #573](https://github.com/KeganHollern/aaemu-cluster/issues/573):

1. Climb a fixed ladder and a rope. Leave each at the top, bottom, and with a jump.
   The character must move normally and leave the object without a position jump.
2. Note a climbable tree's visible expiry or growth timer. Climb and leave the tree, then check its timer again.
   The deadline must not restart. Check a second account's tree when that account is available.
   A climb alone must not create theft evidence or change items or labor.
3. Use a ladder on a moving ship. A second client must see the climb and departure correctly.
   The ship, ladder, and character must keep their normal relative positions.
4. Open building information from a house plaque, a scarecrow, and a sale sign.
   Each object must show the correct house name, owner, tax data, and sale state where applicable.
   Repeat after a reconnect. Repeat after the next scheduled Game restart for a saved house.

The prerequisites are the published server release, r208022, suitable world objects, and a house with a plaque or scarecrow.
Sale-sign checks need a house listed for sale. The ship and second-account checks need those objects and another client.
