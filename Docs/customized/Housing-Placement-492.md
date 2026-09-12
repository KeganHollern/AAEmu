# Planted object placement for r208022

This change addresses aaemu-cluster issue #492.
It binds each paid placement to the exact item in the character inventory.
House construction and decoration use their own compact mappings.

## Client evidence

The source client is `client/bin32/x2game.dll`, version `208022`.
Its SHA-256 is `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205`.
The runtime dump has SHA-256 `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0`.
The dump image base is `0x38ff0000`.

Native `393a9340` sends C2G opcode `0x0e6` after the preview reports state `0x118`.
Constructor `397ac850` and serializer `397bba20` define this 40-byte body:

| Field | Wire type |
| --- | --- |
| Doodad template | uint32 |
| World X | int64 |
| World Y | int64 |
| World Z | float32 |
| Yaw | float32 |
| Scale | float32 |
| Item ID | uint64 |

Native `393a8fa0` compares the full 3D distance squared with `900.0f` at `399d2464`.
The permitted range is 30 meters.
This check includes the vertical distance.
The server rejects nonfinite positions, rotation, and scale before world lookup.
It also rejects zero or negative scale and an unknown destination zone.

`restrict_zone_id` identifies a zone group.
It does not identify a row from `zones`.
Loader `39675d80` reads the column into descriptor offset `0x48`.
Native `394eaac0` compares that field with the current zone group before it starts the placement preview.
The current group comes from `39182720` and `397a6c60`.
The server checks both the character group and the destination group.

For example, item `31813` maps to doodad `7750` and restricted group `54`.
The other restricted groups in this compact are `1`, `34`, `44`, `33`, `56`, and `43`.
The server compact SHA-256 is `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.

## Item and transaction rules

The server gets the item from the character bag and resolves its target through `item_spawn_doodads`.
It rejects a different requested target, a bank item, or a reserved item.
A stack item supplies exactly 1 item.
A non-stack item moves to the System container and keeps its item ID and properties.
The inventory change, labor debit, and persistent doodad row share 1 database transaction.
The server publishes the object only after a known successful commit.
A known failed commit restores the item and releases the new object IDs.

House design lookup uses `item_housings`.
Decoration design lookup uses `item_housing_decorations`.
A client design ID cannot select a different object from the item in the bag.

## Decoration packet relation

Native `39327de0` sends C2G opcode `0x058`.
Constructor `397ae590` and serializer `397caf30` define its 45-byte body.
The body contains a uint16 house ID, uint32 design ID, 3 float positions, 4 quaternion floats, a uint24 support ID, and uint64 item ID.
The position and quaternion are relative to the house pose.
The support ID comes from the preview ray hit at offset `0x4c`.
It does not change the transform parent from the house to the support object.
Geometry checks must use that support identity separately from the house transform.

## Checks

The placement rule tests cover the 30-meter boundary, diagonal and vertical distance, zone groups, and nonfinite input.
The SQL tests cover the exact item, a stack, a coffer, reserved and bank items, remote placement, and a failed commit.
Geometry and surface checks have a separate record in `Housing-Geometry-307-309.md`.
