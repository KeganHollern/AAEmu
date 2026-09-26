# r208022 design and recipe audit

This record resolves the premises of cluster issue [#468](https://github.com/KeganHollern/aaemu-cluster/issues/468).
The audit date is 2026-09-26. The source base is
`b11df2e729055d61098d0660f153882b204c7876`.

## Decision

The 78 merchant design items are crafting materials. They do not teach permanent recipes.
Every one has `use_skill_id = 0` and appears in `craft_materials`.
The current crafting path already checks and consumes those materials.

Do not add a learned-craft requirement from `item_recipes`.
That table contains stale references that disagree with the current craft definitions.
Such a requirement would block normal crafts and require designs for unrelated products.
No runtime, packet, schema, or compact change follows from this audit.

The old `TrainCraftEffect` stub and commented `CanLearnCraft` case alone do not prove an active r208022 feature.
The requested learned-list packet has no confirmed r208022 contract.
A future permanent-learning feature needs a separate client and content design.

## Input identity

| Input | Identity |
| --- | --- |
| Client revision | `client/history.txt`: `version 208022` |
| Packed `client/bin32/x2game.dll` SHA-256 | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| Runtime dump `x2game.dumped.dll` SHA-256 | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| Both PE timestamps | `0x543cb835`, 2014-10-14 05:44:21 UTC |
| Both image bases | `0x38ff0000` |
| Both image sizes | `0x01cb0a00` |
| Client compact SHA-256 | `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4` |
| Server compact SHA-256 | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |

The dump came from the earlier local client research files.
This audit checked its hash and PE identity against the packed binary.
It did not create a new dump or repeat the original dump procedure.
All native addresses below refer to this exact dump.

## Confirmed compact findings

| Check | Result |
| --- | --- |
| Distinct merchant items with `impl_id = 12` (`Recipe`) | 78 |
| Those items with a nonzero use skill | 0 |
| Those items present in `craft_materials` | 78 |
| `item_recipes` rows / distinct craft IDs | 2,816 / 2,810 |
| `item_recipes` rows with an absent craft | 49 |
| Mapped recipe crafts with `crafts.need_bind = 't'` | 0 |
| Recipe items with use skill 11144 | 2,461 |
| Those legacy items in merchant goods, loot, or craft products | 0 in each table |

Item 8582 is a concrete counterexample to the original ticket:

- `items` sets `impl_id = 12` and `use_skill_id = 0`.
- `craft_materials` makes item 8582 a material for craft 64, a wooden bed.
- Craft 64 consumes 1 item 8582, 4 items 8337, 2 items 8256, and 10 items 27545.
- Craft 64 produces 1 item 4014.
- The stale `item_recipes` row instead maps item 8582 to craft 277, a lunafrost.
- Both current client and server compacts contain this mismatch.

There are 3 quest supply rows for legacy recipe items with use skill 11144.
Their quest acts reference absent quest components:

| Supply act | Item | Quest act | Absent component |
| --- | --- | --- | --- |
| 577 | 8564 | 7618 | 4778 |
| 579 | 13732 | 7640 | 4751 |
| 849 | 10926 | 10332 | 7421 |

Skill 11144 links to effect 1750, `TrainCraftEffect` 305, and craft 169.
Craft 169 is a pulp recipe.
The 2,461 items refer to many different recipes.
The common effect does not supply a valid permanent-learning rule for them.

## Native and X2UI evidence

| Address or source | Confirmed behavior |
| --- | --- |
| `FUN_39463c10` | Registers `X2Craft.GetExecutableCraftCount`, `GetAllCraftTypesInGroupByName`, and `GetCraftTypeByItemType`. |
| `FUN_39461ee0` | Routes `GetExecutableCraftCount(craftType)` to `FUN_394d3cc0`. |
| `FUN_394d3cc0` | Looks up the craft, calculates labor availability when applicable, then divides available material counts by required amounts. It returns the lowest count. The function has no learned-craft test. |
| `FUN_39462010` | Implements `GetCraftTypeByItemType` through the craft-product descriptors and their `show_lower_crafts` flag. It does not query `item_recipes`. |
| `FUN_396cd560` | Loads `SELECT item_id, craft_id FROM item_recipes`. Table presence proves data loading, not a character learning system. |
| `FUN_391d9700`, named `OnCraftItemUnlock` | Passes the received inventory-slot value to the inventory operation `FUN_397d2200`. This is not a learned recipe notification. |
| `game/scriptsbin/x2ui/crafting/crafting_renewal.alb` | Uses craft categories, search, materials, and `GetExecutableCraftCount`. It has no `CRAFT_RECIPE_ADDED` or `CRAFT_TRAINED` listener. |

The binary retains strings for `CRAFT_RECIPE_ADDED`, `CRAFT_TRAINED`, and the old learning errors.
Those strings do not prove an active packet or state path.
The checked server r208022 offset table defines `SCCraftItemUnlockPacket` at `0xf5` and `SCCraftFailedPacket` at `0x1bf`.
Neither supplies the requested learned list.
The audit did not infer a new opcode or packet body from those names.

**Inference:** The learning records belong to an earlier content system.
The current merchant designs use the material system instead.
The native material-count path and the current compact rows agree with this conclusion.
The audit does not claim that every unused legacy row has a known historical purpose.

## Server path and checks

`CraftManager.Load` loads `craft_materials` for each craft.
`CharacterCraft.HasAvailableMaterials` checks the available bag contents before the craft and again before completion.
`CharacterCraft.EndCraftCore` grants the products and consumes each authored material.
The paid-skill settlement path saves the inventory and labor changes together.
The `CSExecuteCraft` path also checks the craft ID, count, location, profession, and workbench permission through `CharacterCraft`.

The Release build of `AAEmu.UnitTests` passed with 0 errors.
The 21 existing `CharacterCraftTests`, `CharacterCraftReservationTests`, and `CraftDurationTests` passed with no skips.
These tests cover material availability, reservations, craft completion, cancellation, location rules, and craft duration.
No new test duplicates those checks for this documentation-only correction.

The compact checks used read-only SQLite connections. They changed no data.
These SQL queries reproduce the central findings:

```sql
SELECT i.id, i.use_skill_id,
       (SELECT COUNT(*) FROM craft_materials m WHERE m.item_id = i.id) AS material_uses
FROM items i
WHERE i.impl_id = 12
  AND EXISTS (SELECT 1 FROM merchant_goods g WHERE g.item_id = i.id)
ORDER BY i.id;

SELECT r.item_id, r.craft_id, c.title
FROM item_recipes r JOIN crafts c ON c.id = r.craft_id
WHERE r.item_id = 8582;

SELECT c.id, c.title, m.item_id, m.amount
FROM crafts c JOIN craft_materials m ON m.craft_id = c.id
WHERE m.item_id = 8582;

SELECT s.id, s.item_id, a.id, a.quest_component_id, c.quest_context_id
FROM quest_act_supply_items s
JOIN items i ON i.id = s.item_id
LEFT JOIN quest_acts a
  ON a.act_detail_id = s.id AND a.act_detail_type = 'QuestActSupplyItem'
LEFT JOIN quest_components c ON c.id = a.quest_component_id
WHERE i.impl_id = 12 AND i.use_skill_id = 11144;
```

## Human validation backlog

The audit did not run the client. Keep these steps incomplete in the shared `HUMAN VALIDATION` issue:

- Buy 1 cheap furniture design from a merchant. Check the price and the received design count.
- Find its matching craft at the correct workbench. Check that the design appears as a required material.
- Complete 1 craft. Check that it consumes 1 design and the displayed materials, then grants the product once.
- Try another craft without another design. Check that it grants no product and charges no labor or materials.
- Reconnect. Check that the product count and the consumed design count remain correct.

A failure needs a focused material, payment, or persistence defect report.
Do not convert a failed material check into an unsupported permanent-learning feature.
