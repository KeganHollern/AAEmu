# Tree byproducts, exchange rows, and the legacy UCC imprint

This review covers [#142](https://github.com/KeganHollern/aaemu-cluster/issues/142)
and [#651](https://github.com/KeganHollern/aaemu-cluster/issues/651).
The source baseline is `70079df5376511cbf769a217409b48d0770693c2`.

## Evidence identity

The exact client reports `version 208022` in `client/history.txt`.

| Input | SHA-256 |
| --- | --- |
| `client/bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| `.tools-re/dumps/x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| Client compact | `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4` |
| Server compact | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |
| `game/scriptsbin/x2ui/ucc.alb` | `eec9ebf67f397f74a14337be5ee3df815e2792a896726ba3edea7b8344b36bf3` |

The native source and dump share timestamp `543cb835`, image base `38ff0000`,
entry point `008c5d6d`, and image size `01cb0a00`.
The original dump capture method is unknown.
The earlier [UCC review](UCC-Purchase-519.md) records the same native and script identities.
No packet body or compact data changes are part of this review.

## Confirmed tree and exchange data

Both compacts contain the following records:

| Record | References | Result |
| --- | --- | --- |
| Tree byproducts 6 | Function 937, group 952, next group 953, skill 0 | Groups 952 and 953 are absent. |
| Tree byproducts 7 | Function 2330, group 2495, next group -1, no skill | Group 2495 belongs to test template 1568 and uses `a://invalid`. |
| Exchange item 4 | Parent exchange 2, item 26794, loot pack 7857 | The parent exchange is absent. |

`doodad_func_exchanges` contains 0 records.
`doodad_phase_funcs` contains 0 `DoodadFuncExchange` references.
The exchange item is an orphaned child record, not a complete exchange rule.
It does not specify a reachable interaction or a complete cost and result contract.
The issue's claim that this exchange is deployed content is incorrect.

The old tree chain also contains orphaned records for groups 951 and 954.
Group 951 is absent and has no incoming function or timer transition.
No tracked world spawn or `item_spawn_doodads` record selects test template 1568.

The current oak template 376 uses different actions.
Its fruit phase 957 uses `DoodadFuncUse` 2021 with skill 14240 and next group 4842.
Group 4842 uses `DoodadFuncLootPack` 200 and next group 8108.
Its cut action uses `DoodadFuncUse` 4727 with skill 13977 and next group 4843.
These reachable actions do not use `DoodadFuncTreeByproductsCollect`.

The server did contain a separate exact-name defect.
Both compacts spell the type `DoodadFuncTreeByproductsCollect`.
The native function loader at `0x39673b90` compares the same string at `0x399fde38`.
The C# class and its loader key instead used `DoodadFuncTreeByProductsCollect`.
The correction uses the exact compact spelling, so the normal case-sensitive lookup finds the type.
It does not add collection behavior for the orphaned or test records.
It does not change normal tree loot, labor, inventory, or phase transitions.

The exchange loader comment now records the absent parent and phase references.
A future authored exchange needs a complete content rule before server item operations can use it.
No price, reward count, or duplicate item operation was inferred from the orphaned child.

## Confirmed UCC imprint data and client behavior

Legacy template 2633 uses `DoodadFuncUccImprint` 1 in group 5691.
Function 4801 lists skill 13764 and next group 5692.
Skill 13764 points through effect 8507 to `InteractionEffect` 872, with world interaction 19.
The skill has no item cost or item output.
Timer 1179 returns group 5692 to 5691 after 10,000 milliseconds.
No tracked world spawn or `item_spawn_doodads` record selects template 2633.

The exact X2UI script registers `OPEN_EMBLEM_IMPRINT_UI`.
Its handler at source lines 458-460 only calls `ChatLog` and returns.
It does not open a purchase window or start a stream upload.
Native event registration at `0x39215c2b` uses this exact event string.
A new server interaction context cannot complete this missing client UI.

The supported printer is template 3038.
There are 51 tracked spawns in `main_world/doodad_spawns.json`.
Its `DoodadFuncStampMaker` rule supplies the output and the costs.
Native `GetMakeUccConsumeInfo` at `0x394b5e80` and `UploadEmblem` at `0x393dcec0`
read that rule, as recorded in the earlier UCC review.
The supported printer already sets the current interaction object.
`UccManager.StartUpload` also demands an active `DoodadFuncStampMaker` rule.
The legacy imprint function cannot substitute for that rule.

The UCC handler now explains why it does not set a purchase context.
No replacement UI, stream contract, cost, or phase behavior was invented.
Issue #651 does not identify a defect in the supported crest printer.
A separate request can define a new use for the unused legacy printer if needed.

## Tests and manual checks

The exact-name regression test resolves the authored tree type through the same assembly and dictionary boundary as the loader.
The UCC regression test uses template 2633 with its authored function and skill.
It checks that the legacy function grants no printer context.
It also checks that a remembered legacy object cannot authorize a purchase or change the wallet or inventory.
The existing UCC tests cover normal printer success, cost failures, and repeated completion.

No live client check proves new behavior for the inactive tree, exchange, or legacy imprint records.
No player behavior change is claimed for those records.
The normal printer still needs its existing human check: open printer 3038 and complete a simple or custom crest purchase.
Normal tree collection can use the existing gameplay validation steps.
These checks belong in HUMAN VALIDATION #573, with any earlier results preserved.
