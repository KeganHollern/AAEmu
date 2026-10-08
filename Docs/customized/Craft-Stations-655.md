# Craft stations and carried packs, issue 655

This review covers the r208022 workbench, trade-pack recipe, product, and cancel paths.
The empty doodad handlers do not own the active recipe operation.
The review found one active defect in recipe admission.
The server rejected an ordinary bag product when the character carried a trade pack.

## Source and client identities

The reviewed server base is `4d0f9ff2cdd6bbd0e01e07305ac6879820aaff81`.
The client history identifies revision `208022`.

| Input | SHA-256 |
| --- | --- |
| `client/bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| Memory-layout `x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| Client compact | `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4` |
| Server compact | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |
| `game/scriptsbin/x2ui/crafting/crafting_renewal.alb` | `0afe417960629cbd2386d8ab6b7665ebe0c427d5a261fa5b06efa6e53f082170` |

Native addresses below refer to that memory-layout dump at base `0x38ff0000`.
The source DLL and dump share PE timestamp `0x543cb835`, entry RVA `0x008c5d6d`, and image size `0x01cb0a00`.
No client or compact file changed.

## Confirmed craft and cancel paths

The craft button in `crafting_renewal.alb`, source lines 736–738, calls
`X2Craft:ExecuteBatchCraftByType(craftType, doodadId, count)`.
The cancel button, source lines 747–749, calls `X2Craft:StopBatchCrafting()`.
Neither button invokes the legacy `DoodadFuncCraftAct` or `DoodadFuncCraftCancel` handler.

| Native function | Confirmed role |
| --- | --- |
| `FUN_39460930` | Binds the selected recipe, doodad object ID, and positive count to the native craft manager. |
| `FUN_394d7650` | Starts a batch and calls the normal recipe admission function. |
| `FUN_394d6dc0` | Checks the recipe, station, distance, selected craft list, materials, profession, and labor before the request. |
| `FUN_398a9480` | Finds the requested recipe through the station's current legacy craft list or CraftPack list. |
| `FUN_398a9100` | Checks requested material counts and the recipe tool. It has no blanket backpack-slot check. |
| `FUN_394d1340`, `FUN_397ac9e0` | Build C2G opcode `0x0f8` from the recipe ID, doodad object ID, and requested count. |
| `FUN_397bbfc0` | Serializes UInt32 recipe ID, 3-byte object ID, and Int32 count, in that order. The body is 11 bytes. |
| `FUN_39460870`, `FUN_394d1870` | Bind the cancel button to native skill cancellation. |
| `FUN_393966d0`, `FUN_393967c0` | Send C2G opcode `0x054` for active craft casts. |
| `FUN_397abc10`, `FUN_397cad70` | Build and serialize the 2 timeline IDs and character object ID used by `CSStopCastingPacket`. |

`CSExecuteCraft` already checks the recipe and count before `CharacterCraft.Craft`.
`CharacterCraft` checks the current station pack, distance, materials, profession, and permission.
`CraftEffect` completes the selected recipe through `CharacterCraft.EndCraft`.
That method checks materials and permission again before it grants products.
It consumes materials, records craft progress, and schedules the rest of a batch.
`Skill.Stop` calls `CharacterCraft.CancelFromSkill` for the selected craft skill.
The current cancellation test proves that this path does not grant a product or consume materials.

The server grants ordinary products into the bag.
It uses `ItemManager.IsAutoEquipTradePack` to select products that need the backpack slot.
The old admission check applied that slot requirement to every recipe.
The new admission check uses the same product classification as the product grant path.
A bound-on-equip backpack product remains a bag product under that current classification.
An auto-equipped pack still needs an available backpack slot.

## Confirmed handler reachability

Both compact databases have the counts below.
A function table row alone does not establish a reachable interaction or phase.

| Handler | Template rows | Finding |
| --- | ---: | --- |
| `DoodadFuncCraftAct` | 27 | Referenced rows in valid groups belong to legacy station templates. |
| `DoodadFuncCraftCancel` | 30 | The active craft window uses skill cancellation. |
| `DoodadFuncCraftGetItem` | 27 | The active recipe completion grants products. Legacy collection phases do not serve the current window. |
| `DoodadFuncCraftInfo` | 23 | Legacy station information does not start the current recipe operation. |
| `DoodadFuncCraftPack` | 304 | Current station functions expose recipe lists. `CharacterCraft` uses these lists for admission. |
| `DoodadFuncCraftStart` | 25 | Legacy templates do not establish a current missing craft-start path. |
| `DoodadFuncCraftStartCraft` | 1,379 | There are **0** references in `doodad_phase_funcs`. These are template rows, not active phase rows. |

Legacy templates `274`, `291`, `1309`, `1692`, and `1974` have no tracked world spawn or item-placement row.
Template `2323` also has no tracked world spawn.
Its placement row refers to item `16185`, which does not exist in either compact's `items` table.
Its name identifies a test campfire.
These records do not justify a new legacy production system.

Alchemy Table template `568` has 118 tracked spawn records.
Its start phase `1012` exposes CraftPack through function `7911`.
Its CraftDirect phase descriptor points to phase `1088`, which also exposes CraftPack.
The timer in `1088` returns to `1012`.
The CraftGetItem row sits in phase `1089`.
The only authored `next_phase=1089` reference belongs to missing group `4331`.
The active Alchemy Table path cannot reach that collection phase.

The CraftPack handler now explicitly keeps `ToNextPhase=false`.
It cannot advance or remove a station when an interaction reaches the descriptor.
The CraftStartCraft handler documents its lack of phase references.
The other dormant handlers remain unchanged.
No second product grant or material debit was added to these descriptors.

## Inferences and limits

The exact client admission path supports ordinary recipe requests with a carried pack.
The server's product destinations establish why only auto-equipped products need that slot.
This is a source and native-code conclusion, not a reported in-client pass.

The work does not restore the unused legacy station production lifecycle.
Its former retail rules remain unknown.
No live gameplay, batch-cancel timing, or craft animation check occurred during this review.
The normal packet body and current batch scheduler remain unchanged.

## Automated checks

The focused tests cover these cases:

- Ordinary bag products reach the material check while a trade pack remains equipped.
- Auto-equipped pack products reject an occupied backpack slot before materials change.
- Bound-on-equip backpack products follow the current bag-product path.
- A completed ordinary recipe consumes its materials and keeps the carried pack equipped.
- The CraftPack descriptor does not advance the station phase.
- Current location and reservation tests still cover recipe packs, material changes, and cancellation.

The focused Release build and all 23 `CharacterCraft*` tests passed.
The release record contains the complete suite results for the final selected commit.

## Human validation

Add these checks to HUMAN VALIDATION issue 573 before issue 655 closes.
Use the deployed release and the current launcher-managed r208022 client.

1. Use a public workbench with enough materials, labor, profession points, and bag space.
   Craft 1 ordinary product with an empty backpack slot.
   Check the product count and material and labor costs.
   Repeat with a trade pack equipped.
   The ordinary product must enter the bag, and the carried pack must remain equipped.
2. Use a specialty workbench with enough recipe materials and labor.
   Craft 1 trade pack with an empty backpack slot.
   Check that the product equips once and the recipe costs apply once.
   Try another pack recipe while the pack remains equipped.
   The server must reject the second pack without product or material changes.
3. Start a batch of at least 3 ordinary products.
   Use Cancel during an active cast, before its product appears.
   The interrupted cast must not grant a product or consume its recipe materials or labor.
   No later product from that batch must appear.
   Craft 1 product again to check that the cancelled batch did not keep the character busy.

Preserve completed products and their costs when the player cancels a later cast in the same batch.
