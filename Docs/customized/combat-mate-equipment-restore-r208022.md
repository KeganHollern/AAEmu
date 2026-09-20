# Saved mate equipment startup correction

The combat release added gameplay eligibility checks to `MateEquipmentContainer` for cluster issue #325.
`ItemManager.LoadUserItems` also used that gameplay path for saved items.
At startup, the mate container has no `ParentUnit`, so the new rule rejected saved equipment.

`ItemManager.LoadUserItems` now uses `MateEquipmentContainer.RestorePersistedItem` for saved mate equipment.
This internal method preserves the saved item, owner, slot, flags, and details without bind rules or gameplay callbacks.
It needs a persistent container with no parent and an empty, valid saved slot.
It rejects an item that already belongs to a container.

`CharacterMates` later gets the same container through `GetItemContainerForCharacter` when it creates the mate.
That code attaches the mate and updates gear bonuses.
Normal equipment moves keep owner checks and use `CanAccept` for slot, item tag, model, and level checks.
Old saved gear stays available even when current gameplay rules reject a new equip action for that gear.

The correction does not change the schema or compact database.
It does not repair rows that a previous process changed.
If a saved mate item cannot enter its saved slot, the loader stops startup and preserves the database row.
It removes the incomplete object from the in-memory pool.
A later save cannot write a zero container ID for that item.

The unit regressions cover parentless restore, exact item state, later mate attachment, rejected gameplay moves, and duplicate slots.
The MySQL regressions use the production save and load methods, then save again after mate attachment or a failed load.
The tests check that the item keeps its container, owner, slot, and equipment details.
The failed-load cases cover invalid and duplicate slots.
Both cases check the saved rows after an explicit save.

The Release build passed with 0 errors and 13 warnings on September 14, 2026.
The runtime script compiler passed with 0 errors and 0 warnings on that date.
On September 19, the full unit suite passed 4072 tests with 16 skips and no failures.
That run used the current server compact and the extracted mate model assets.
The full local MySQL suite passed all 357 tests, including the 3 new persistence cases.
All 33 focused mount authorization tests also passed before the full suite.
The release records keep the logs in `.tools-re/combat-20260913/release/equipment-correction/` in the cluster workspace.

After the correction reaches the cluster, summon a mate with saved gear.
Check its equipment slots and bonuses, then dismiss and summon it again.
Check that an invalid item still fails to enter a mate equipment slot.
