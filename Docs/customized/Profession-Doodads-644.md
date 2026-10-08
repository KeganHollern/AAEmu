# Profession doodad functions in r208022

This review covers [#644](https://github.com/KeganHollern/aaemu-cluster/issues/644).
The source baseline is `4d0f9ff2cdd6bbd0e01e07305ac6879820aaff81`.
The issue lists 24 log-only handlers and 2 phase handlers.
The review found no missing reward operation in a current gathering path.
Most listed records belong to inactive content.
`DigTerrain` describes a client terrain effect, and `Cutdown` already advances its server phase.

## Evidence identity

The client reports revision `208022` in `client/history.txt`.
The source DLL and dump share PE timestamp `543cb835`, image base `38ff0000`,
entry point `008c5d6d`, and image size `01cb0a00`.
The original dump capture method is unknown.

| Input | SHA-256 |
| --- | --- |
| `client/bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| `.tools-re/dumps/x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| Client compact | `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4` |
| Server compact | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |

## Confirmed reference inventory

Both compacts give the same counts below.
Each name uses the `DoodadFunc` prefix.
The counts describe `doodad_funcs` references, not runtime calls or definitions in the individual function tables.
An absent template count excludes references with an absent group.

| Function | References | Absent group | Absent template | Result |
| --- | ---: | ---: | ---: | --- |
| `OreMine` | 55 | 23 | 16 | Legacy ore chains and test item links. |
| `RockMine` | 12 | 5 | 5 | No current spawn path found. |
| `CrystalCollect` | 21 | 10 | 10 | No current spawn path found. |
| `GassExtract` | 33 | 15 | 16 | No current spawn path found. |
| `FiberCollect` | 2 | 1 | 0 | No current spawn path found. |
| `FruitPick` | 12 | 4 | 1 | Test item links and 2 explicit test skills. |
| `PlantCollect` | 2 | 1 | 0 | No current spawn path found. |
| `SoilCollect` | 5 | 2 | 2 | No current spawn path found. |
| `SeedCollect` | 1 | 0 | 0 | No current spawn path found. |
| `SpiceCollect` | 5 | 4 | 0 | No current spawn path found. |
| `DyeingredientCollect` | 1 | 0 | 0 | No current spawn path found. |
| `MedicalingredientMine` | 6 | 5 | 0 | No current spawn path found. |
| `MachinePartsCollect` | 3 | 0 | 2 | No current spawn path found. |
| `CerealHarvest` | 3 | 2 | 0 | No current spawn path found. |
| `CropHarvest` | 44 | 42 | 1 | No current spawn path found. |
| `DairyCollect` | 4 | 3 | 0 | No current spawn path found. |
| `Shear` | 5 | 2 | 1 | No current spawn path found. |
| `SkinOff` | 1 | 0 | 0 | No current spawn path found. |
| `Butcher` | 10 | 7 | 1 | No current spawn path found. |
| `Mow` | 2 | 0 | 0 | No current spawn path found. |
| `Dig` | 4 | 0 | 1 | No current spawn path found. |
| `DigTerrain` | 4 | 0 | 0 | The client creates terrain effects. |
| `Feed` | 5 | 2 | 1 | No current spawn path found. |
| `Catch` | 2 | 0 | 0 | No current spawn path found. |
| `Cutdown` | 44 | 22 | 5 | Dynamic trees use the current phase handler. |
| `Harvest` | 11 | 2 | 3 | Legacy phases, including unused nameplate phase 98. |

A nonzero skill appears on only 2 listed references.
`FruitPick` function 8009 selects skill 11109 for test template 3725.
Function 8085 selects the same skill for template 3785, which is absent.
Neither template has a tracked spawn or item-spawn source.

All 26 C# class names match the compact names exactly.
This review found no repeat of the spelling defect from [#142](World-Items-142-651.md).

### Reachability checks

Only `DigTerrain` has a reference in a template with a tracked world spawn.
Template 888 has 1 spawn in `main_world`, at `(8397.864, 10608.546, 227.9035)`.
The world spawn scan covers every tracked `Data/Worlds/**/doodad_spawns.json` file.

Only test templates 272 and 317 have item-spawn links to the listed handlers.
Template 272 contains `OreMine` and has 4 old item-spawn links.
Three linked item definitions are absent.
Item 1449 remains, but its target is the timer example template 272, not a normal vein.
Template 317 contains `FruitPick` and `Cutdown`.
Both linked item definitions, 13925 and 14829, are absent.

The review also checked dynamic sources.
These include item placement, bundles, farm and schedule entries, housing and vehicle attachments,
backpacks, skill channel objects, `SummonDoodad` interactions, and special effect 15 (`SpawnDoodad`).
It then followed `DoodadFuncRatioRespawn` edges from these sources.
This deliberately includes sources whose own activation is uncertain.
Even that wider graph gives no path into the legacy ore respawn chains.

The legacy ore chain includes templates 1532, 1534, and 1535.
Another chain includes 3684, 3689, and 3690.
The latter chain's parent template 3692 is absent.
Templates 1964 and 1968 refer back to rubble 1677.
The other parents of rubble 1677, templates 326 through 330, are absent.
No current source reaches those cycles.
This differs from a claim that every function row is safe to enable.

Three dynamic tree templates still use `Cutdown`:

| Source | Template | Phase path |
| --- | ---: | --- |
| `InteractionEffect` 2754, `SummonDoodad` | 1704 | `Cutdown` 124 or 125 advances to 3169, with `LootItem` 964 and `Cutdowning` 114. |
| Special effect 1624, `SpawnDoodad` | 2383 | `Cutdown` 135 advances from 4903 to 4904, then `Cutdowning` 117 and `LootItem` 1372. |
| Special effect 676, `SpawnDoodad` | 1358 | `Cutdown` 138 advances from 5200 to 5203, then `Cutdowning` 120 and `LootItem` 1419. |

The current `Cutdown` handler sets `ToNextPhase=true`.
`Doodad.DoFuncLocked` applies the authored next phase.
The following functions own the tree animation and reward.
Adding another reward inside `Cutdown` duplicates that responsibility.

Housing 170 has a binding to template 319.
Its starting phase 96 uses `ParentInfo` with skill 15212.
The old `Harvest` record belongs to phase 98.
There is no incoming function or timer transition to that phase.
The nameplate is not a current harvesting path.

## Confirmed client terrain behavior

`DoodadFuncDigTerrain` is not an ore or soil collection action.
Its data defines a radius and a lifetime.
The 4 definitions belong to templates 888, 1703, 3297, and 6315.
For example, template 888 uses radius 3 and lifetime 12000.
Its function has no skill, reward, or next phase.
`SummonDoodad` effects also create template 888 for spell effects.

The exact native client establishes the following path:

| Address | Confirmed behavior |
| --- | --- |
| `397a4b10` | Loads `id, life, radius` from `doodad_func_dig_terrains`. |
| `39673b90`, branch at `3967437a` | Maps `DoodadFuncDigTerrain` to native function kind `0x27`. |
| `3983bee0` | Finds kind `0x27` in the current phase's function list. |
| `393a4530`, `ClientDoodad::Init` | Finds the descriptor, copies its lifetime and radius, and creates the terrain object through `393af450`. |
| `393acb40` | Sets the terrain object's position and radius, then checks nearby terrain effects through `393ac550`. |
| `393a50c0` | Excludes terrain descriptors from normal model recreation. |
| `393a1f90` and `39038a30` | Select the terrain-specific removal fade when the descriptor is present. |

`Doodad.Write` supplies the template and current phase IDs.
Those IDs select the local descriptor when the client creates the object.
The server must not add a second gathering operation or change phase for this descriptor.
The source comment now records that distinction.
No packet, client content, compact, or persistent state change is needed.

## Confirmed normal gathering paths

Current gathering skills call `InteractionEffect`, then the matching world interaction, then `Doodad.Use`.
The world interaction name and doodad function type need not match.
For example, the `OreMine` world interaction selects `DoodadFuncUse` on a normal iron vein.
It does not select the legacy `DoodadFuncOreMine` class.

| Action | Authored route | Base cost |
| --- | --- | --- |
| Iron Vein 1671 | Skill 13986, effect 998, interaction 73 (`OreMine`), `Use` 1013, phase 3150, `LootPack` 49. | 10 labor. |
| Rare vein stage on 1671 | Skill 13988, effect 1000, interaction 73, `Use` 3491 and related phase rules, then `LootPack`. | 20 labor. |
| Potato 2259 | Skill 13980, effect 992, interaction 78 (`CropHarvest`), `Use` 1047 or 2039, then `LootPack`. | 1 labor. |
| Feed calf 2672 | Skill 20595, effect 3434, interaction 19 (`Use`), then the current `Use` phase rule. | 3 labor and 1 item 26744. |
| Milk calf 2672 | Skill 13800, effect 901, interaction 19, `Use` 501 or 4697, then `LootPack`. | 15 labor. |
| Butcher calf 2672 | Skill 13972, effect 982, interaction 20 (`Butcher`), then `Use` and `LootPack`. | 25 labor. |

These costs come from the compact, before skill modifiers.
`Skill.ApplyEffects` processes the explicit effect reagent.
`SkillLaborBatch` owns the labor and inventory transaction.
`DoodadFuncLootPack` and `DoodadFuncLootItem` grant the authored rewards.
`Doodad.Use` follows the phase chain after the completed skill.
A second cost or reward in the legacy profession classes would bypass this shared path or duplicate its work.

## Result, limits, and validation

The reference counts, current source routes, and native terrain path are **confirmed**.
The absence of an ordinary source for legacy content is an **inference** from the checked source and compact references.
A GM can still create an old template directly.
The intended complete behavior of those inactive templates is **unknown**.
This review does not introduce new recipes, item rewards, or default skills for them.

The issue's general missing-handler premise does not describe the current gathering path.
There is no confirmed runtime correction for #644.
The change adds this evidence and a comment at the terrain handler.
It preserves the current costs, rewards, phases, and client terrain behavior.
No new test is needed for the comment change.

Existing automated coverage includes:

- `MiningCompletion_PreservesTheBrokenVeinPhaseOnlyAfterCommit`, which checks committed and failed mining transactions.
- `SkillLabor_CheckpointsProductsMaterialsAndPersistentHarvestTogether`, which checks labor, materials, products, and persistent doodad state together.
- The `SkillLaborTests` reagent, inventory failure, and completion checks.

The final client checks remain open in HUMAN VALIDATION #573:

1. Mine 1 normal Iron Vein with enough labor and bag space. Check 1 reward operation and the displayed labor cost.
2. Harvest 1 mature potato. Check the reward, labor cost, and next object state.
3. Feed and milk a calf. Check 1 feed item per successful feed and the displayed labor costs.
4. Repeat one gathering action with too little labor or missing feed. Check that no reward or phase change occurs.
5. Cast an available skill that creates crater template 888. Check the terrain effect and its removal.

These checks need the published release and the r208022 client.
The crop and calf checks also need eligible mature objects and their normal supplies.
The crater check needs an eligible skill or a controlled GM setup.
No manual pass is claimed here.
The Iron Vein break visual remains in [#567](https://github.com/KeganHollern/aaemu-cluster/issues/567).
This review does not close or change that separate report.
