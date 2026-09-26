# r208022 portal use and placement

This record supports aaemu-cluster issue #523. It uses deployed source base
`c50c7a6e`. It does not change a database or client artifact.

## Client identity

`client/history.txt` identifies version `208022`.

| Input | SHA-256 |
| --- | --- |
| Packed `client/bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| `.tools-re/dumps/x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| `game/prefabs/fx.xml` | `e06f142b0f02f9210a407f3611eddc7e9ee93b858e281123ace24dbdc840c5f8` |
| `game/objects/effects/portal_gate_proxy.cgf` | `e58ab9b5eb394b3735264c089251d8e5b66ac4b69f619b9d27cb054c8482a910` |
| Server compact | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |

Both PE images have timestamp `0x543cb835`, base `0x38ff0000`, and image size
`0x01cb0a00`. The earlier dump has no separate capture record. This work used
Ghidra 12.1.3 and `objdump` against that dump. It did not capture live traffic.
All addresses below use the dump's virtual address space.

## Confirmed request contract

`CSUsePortalPacket` is C2G level 1, opcode `0xDE`. The body contains 4 bytes.

| Body offset | Type | Meaning | Native evidence |
| --- | --- | --- | --- |
| 0 | Little-endian `u24` | Portal object ID | Producer `3930cff0`, constructor `397ac7b0`, serializer `397bb8d0` |
| 3 | Boolean byte | Visitor's `onlyMyPortal` preference | The same constructor and serializer, plus the option descriptor below |

The native object stores the fields at `+0x0c` and `+0x10`. Its serializer
writes the object ID with width 3, then the Boolean named `onlyMyPortal`.
The vtable at `399cdab8` points to that serializer at offset `+8`.
The producer sets opcode `0xDE` at `3930d1ab` and sends through `39186810`.
The AAEmu registration and reader independently match the field order and widths.
There are no optional fields or trailing groups.

The flag comes from the visiting player's option `auto_use_only_my_portal`.
The option descriptor at `39ac43b8` connects the name at `399b1700` to the
variable at `3a1776d4`. Its description at `399b16d4` says
`automatically use portal only if it's mine`.
The producer reads that variable at `3930d05d` and copies its Boolean value
to the packet. This is not an owner-controlled private portal setting.
When the flag is true, the server must match the portal owner to the visitor.
When the flag is false, another character can use the portal.

## Confirmed range and delay

The native producer `3930cff0` checks 3D distance between the player origin
and the portal origin. It gets the transformed model shape through `390b3ae0`.
Getter `3989ac80` supplies the aggregate sphere radius from offset `+0x80`.
The producer compares **squared origin distance directly with that radius**.
It does not square the radius. Its comparison includes equality.

The compact maps NPC `3891` to model `308`, prefab model `172`, and
`prefab://Prefabs/fx.xml/portal_gate_dot`. NPC `6949` uses model `667`, prefab
model `271`, and `portal_gate_exit_dot`. Both prefabs contain 1 sphere with
center `(0, 0, 1.5213814)` and radius `3`. The proxy CGF has no aim helpers.
The existing extractor in [issue-260](issue-260/extract-doodad-interactions.py)
checked the prefab and its proxy. The [native geometry record](issue-260/native-contract.md)
explains Comment spheres and scale.

The producer uses origins for this comparison, so it does not add the sphere
center offset. The server uses `distanceSquared <= 3 * portal.Scale` for the
2 portal models that it creates. It rejects unknown portal models.
The use range is separate from the entrance placement limit.
`open_portal_effects` row `1` gives the placement limit as `3` meters.
The server checks that distance in all 3 axes before it consumes a reagent.

Native `391827f0` compares the current millisecond clock with the stored clock
at client offset `+0x109c`, plus `10000`. A rejected use reports error `0x319`,
`cannot_reuse_portal_in_delay_time`. This confirms the 10-second delay.
The producer's separate 1000-millisecond interval only limits automatic requests.
It is not the reuse delay.

`OnTeleportUnit`, at `391d6ed0`, starts the teleport state and sets that clock.
The completion path at `391e72b0` clears the state and sets the clock again.
The server keeps one transient state per character. It blocks concurrent uses
while a teleport is pending. A teleport-end or instance-load acknowledgement
starts the 10-second delay. An earlier timestamp cannot shorten the delay.
No persistent cooldown row or schema change is needed for this session state.

`OnUnitPortalUsed`, at `391d3970`, resolves the unit ID at packet object
offset `+0x0c`. It calls effect handler `393558e0` for that unit.
The server sends the existing `SCUnitPortalUsedPacket` before the teleport.
Its wire layout remains unchanged.

## Server changes

The server resolves the portal from the visitor's current world. It rejects
ordinary NPCs, missing objects, dead portals, despawned portals, remote portals,
and portals in another instance. The live world object must match the portal.

The server checks the visitor's owner preference, backpack buff, trial state,
and attached vehicle cargo before it changes the teleport state. Rejected cargo
no longer leaves `DisabledSetPosition` set to true.

An accepted use updates the server position before the client acknowledgement.
For an instance transfer, `SCLoadInstancePacket` uses the destination instance
ID. The earlier path used the world template ID and replaced the instance ID
with that value. The new path preserves the destination instance and coordinates.
The earlier cross-instance mate and vehicle removal rules remain in place.

An unknown book ID returns `InvalidPortal` before any item charge.
Entrance coordinates must be finite and within the authored placement radius.
The entrance uses the caster's zone. The visual exit uses the destination zone.

## Automated checks

The focused tests cover these paths:

- The exact request body, owner flag, truncation at each byte, invalid Boolean,
  and trailing bytes.
- The inclusive 3D range, scale, unknown model, wrong instance, missing object,
  object replacement, despawn, and death.
- Own and shared portals, backpack and court rejection, and attached cargo.
- Server position, portal-use notification, and the destination instance ID.
- Pending travel, the 10-second boundary, a later completion time, and concurrency.
- Unknown book IDs and invalid entrance coordinates before payment dependencies.
- The production teleport-end packet and the delay after its acknowledgement.

Run the focused checks from the AAEmu source root:

```sh
dotnet build AAEmu.UnitTests/AAEmu.UnitTests.csproj -c Release -m:1 -p:UseSharedCompilation=false
dotnet AAEmu.UnitTests/bin/Release/net10.0/AAEmu.UnitTests.dll --treenode-filter '/*/*/PortalUse*/*'
```

## Pending human checks for HUMAN VALIDATION #573

These checks need the published source release and the r208022 client.
Automated checks do not complete these items.

- [ ] Open an ordinary portal from the teleport book. Check the entrance position and displayed reagent cost.
- [ ] Enter that portal. Check the destination, movement after arrival, and position after reconnect.
- [ ] Try another nearby entrance within 10 seconds after arrival. Check that it waits, then permits travel after the delay.
- [ ] Enable the option to use only your own portals. Check that your portal still works.
- [ ] With a second player, check that this option blocks their portal. Disable the option and check that their portal works.
- [ ] Carry a trade pack through an entrance. Check the rejection, unchanged position, and normal movement afterward.
- [ ] In a controlled instance-transfer setup, check that a vehicle with attached cargo prevents travel without freezing the character.

The second-player and controlled-instance checks have explicit prerequisites.
The current work does not claim a completed human test or a live packet capture.
