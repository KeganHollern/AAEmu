# Issue 464: r208022 lunagem limits

This change resolves [cluster issue 464](https://github.com/KeganHollern/aaemu-cluster/issues/464).
The source base is `c721206cae898b205e86f5512b650d7003f379f6`.
The issue has no comments.

## Confirmed data

The server reads the current compact. This change does not alter either compact or persistent SQL.

| Table | Rows | Used fields |
| --- | ---: | --- |
| `item_socket_num_limits` | 372 | `slot_id`, `grade_id`, `num_socket` |
| `item_sockets` | 123 | `item_id`, `equip_slot_group_id` |
| `item_socket_level_limits` | 108 | `item_id`, `level` |
| `equip_slot_groups` | 24 | `id` |
| `equip_slot_group_maps` | 48 | `equip_slot_group_id`, `equip_slot_type_id` |
| `content_configs` | 1 selected row | `id=62`, `kind_id=24`, `value=20` |

The count table covers equipment slot IDs 1 through 31 and grade IDs 0 through 11.
Grade IDs are not `grade_order`. Common and poor have different orders.
The actual item grade selects the count. The inventory position does not select it.

| Equipment slot IDs | Socket counts for grade IDs 0 through 11 |
| --- | --- |
| 1, 5 | 0, 0, 1, 1, 2, 3, 4, 5, 6, 6, 6, 6 |
| 3, 14, 16, 17, 18, 20, 21 | 0, 0, 1, 2, 3, 4, 5, 6, 7, 7, 7, 7 |
| 4, 8 | 0, 0, 1, 1, 1, 2, 2, 3, 4, 4, 4, 4 |
| 6, 7 | 0, 0, 1, 1, 1, 2, 3, 4, 5, 5, 5, 5 |
| Other slots | 0 for every grade |

All 108 live gem items have a level row and a valid group with slot members.
All use skill 23728. The other 15 socket records refer to absent item templates.
Some orphan records have a null group. Group map 9 has no group record.
The loader does not turn that orphan map into a valid group.

The client uses 2 different item levels:

- `items.level` must reach the global minimum of 20.
- `items.level_requirement` must reach the gem-specific level from `item_socket_level_limits`.

For example, gems 30907, 30918, and 30929 need equip levels 0, 40, and 50.
Their group 11 permits slots 14, 17, 18, and 21. It does not permit the two-handed slot 16.

## Exact client evidence

The local `client/history.txt` identifies version 208022.
The source DLL and the local runtime dump have the same PE timestamp, image base, entry RVA, and image size.
This task used static analysis. It did not capture a new runtime dump or claim an in-client test.

| Artifact | SHA-256 |
| --- | --- |
| `compact/server.sqlite3` | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |
| `client/bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| Local `x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| `socket_enchant.alb` | `4a0b9ac59645a385fa81cf6a4c5ca4564a7a42a3d6b031322a02729bbfb6118f` |
| `socket_enchant_view.alb` | `77042cb27fd254c4c31fae7a18154e0621c74431e55a2624497a69d7677e102f` |

The UI files came from `game/scriptsbin/x2ui/inventory/enchant/` in the client pack.
The UI uses `X2ItemEnchant`, `socketInfo.maxSocket`, and the native executable-state result.
The native code supplies the limits.

The PE image base is `0x38ff0000`, the timestamp is `0x543cb835`, and the entry RVA is `0x008c5d6d`.
The image size is `0x01cb0a00`. All addresses below are absolute virtual addresses in that image.

| Address | Confirmed operation |
| --- | --- |
| `0x39571fb0` | Reads `SELECT value, id FROM content_configs`, then stores each value at `base + id*4`. |
| `0x395720e0` | Gets a content config value by its ID. |
| `0x395b53d0` | Loads the map from `SELECT item_id, level FROM item_socket_level_limits`. |
| `0x395fb900` | Loads the slot and grade map from `item_socket_num_limits`. |
| `0x396cc6e0` | Loads item and group IDs from `item_sockets`. |
| `0x395aca00` | Gets the maximum count by slot and actual grade. It first checks the global minimum item level. |
| `0x395aca70` | Gets a gem level. A missing row returns `std::numeric_limits<int>::max()`. |
| `0x397cc290` | Gets an armor, weapon, or accessory equipment slot. Other item implementations return 0. |
| `0x397cd200` | Checks group membership. Group 0 and defined empty groups allow all slots. An unknown nonzero group denies the slot. |
| `0x397ceb80` | Checks the complete install or extraction request. |

The item loader starts at `0x396cb3f0`.
Its SQL query at `0x39a00d90` selects `level` at column 22 and `level_requirement` at column 24.
The stores at `0x396cb70c` and `0x396cb728` write descriptor offsets `0x4c` and `0x54`.
The copy at `0x396cbb4d` uses the descriptor base at stack offset `-0x140`.
These stores prove the 2 level meanings.

The complete validation function checks config ID `0x3e` against offset `0x4c`.
For installation, it compares the gem level with offset `0x54`.
It then checks slot membership, a nonzero maximum count, and the current gem count.
For extraction, it needs at least 1 current gem but does not need an install grade or group.

Native result IDs match the current `ErrorMessageType` enum:

- 18: `InvalidTarget`.
- 742: `ItemSocketsFull`.
- 743: `ItemSocketsEmpty`.
- 781: `SocketTargetLevel`.

The server sends the matching current error message and cancels the skill.
No opcode, packet field, or equipment detail format changes.
The 7 gem slots remain in the 55-byte equipment details.

## Server behavior and payment

The server checks the rules at skill entry, cast completion, and effect entry.
The cast check occurs before mana use. The effect check occurs before effect rolls, item use events, products, and source consumption.
The special effect also checks its direct entry and never rolls after rejection.
The source must remain in the owner's inventory with a positive count, matching item template, and matching use skill.

Socket casts use the current trade execution lease.
The current persistence lock keeps final validation, the socket roll, gem changes, and source consumption together.
Inventory movement, another socket request, and persistence snapshots use that same lock.
Other skills keep their current execution path.

Skill 23728 and Dawnstone skill 23729 each have a 3000 ms cast and no mana, labor, or effect delay in this compact.
Tests also use nonzero costs to check that rejection occurs before those resource paths.
The current source consumption path still uses 1 gem after a valid result.
The current success chances and remove-all-gems failure behavior remain unchanged.
Success fills the first free array entry without overwriting a current gem after a hole.
The effect marks each valid result dirty so the normal item save includes it.
Rejection does not change the item's dirty state.

Official upstream `develop` at `00ed43a0` has no equivalent limit checks to reuse.
The relevant upstream history includes `af517c94` and `d2dfd438`.
This change does not import a different client's failure policy or chance table.

## Tests and reproduction

The deterministic unit tests cover all 372 count rows and each occupancy boundary from 0 through 7.
They also cover slot groups, missing rows, native group defaults, both level boundaries, and Dawnstone extraction.
Skill tests cover early rejection, changed targets, source identity, trade reservations, concurrent requests, source consumption, and equipment detail serialization.
The full solution build passed. All 2659 unit tests passed, with no failed or skipped tests.
The separate explicit compact test also passed, with 1 successful test and no skipped tests.

Use this command for the unit tests:

```sh
dotnet run --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --no-build -- --treenode-filter '/*/*/*ItemSocketing*/*'
```

The separate explicit test reads the actual compact without write access.
It checks all 372 count rows and all 108 live gem mappings.

```sh
AAEMU_SOCKET_TEST_COMPACT=/absolute/path/to/server.sqlite3 dotnet run --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --no-build -- --treenode-filter '/*/*/ItemSocketingCompactTests/ActiveR208022Compact_LoadsEveryCountRowAndAllLiveGems'
```

The client behavior still needs human validation after deployment.
Check a last legal socket, a full item, an incompatible gem, and an item below the gem's required equip level.
Check that rejected requests keep the source and current gems.
Then reconnect and check that a valid result persists.
