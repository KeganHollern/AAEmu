# Theft evidence and owner factions

This change addresses aaemu-cluster issue #503.

## Defect and source comparison

At source commit `0175f4dfc9c591403c0f8a504e3022d662c280de`, `Doodad.Use` did not check factions before theft evidence creation.
`CrimeManager.GenerateEvidenceFromTheft` also omitted this check.
Enemy property could leave footprints, which players could report for crime points.

Upstream commit `00ed43a0c` changed property guard time in PR #1557.
That change retains the faction TODO.
This release keeps the current 24-hour public-property rule.

## Eligibility and owner lookup

`CrimeManager.GenerateEvidenceFromTheft` now accepts only theft against a friendly owner faction.
The manager rejects missing actors, missing factions, zero character IDs, self-owned property, and non-character property.
The check applies to every caller of this method.

The manager reads `characters.faction_id` with a parameterized query and `deleted=0`.
It does not cache the result.
A missing or deleted owner cannot create theft evidence, even if an old owner object remains in the world.

For an active owner, the current character faction takes precedence over the saved faction.
For an offline owner, the saved faction supplies the relation check.
`Character.Load` reads this same field, and `Character.Save` writes the current faction ID.
The lookup does not infer a faction from race, continent, or a property object's faction.

The relation check uses `SystemFaction.GetRelationState` directly.
It does not use `BaseUnit.GetRelationStateTo`, which treats duel opponents as hostile.
A duel therefore does not change theft evidence rules.
Unknown owner factions and neutral relations do not create evidence.

The r208022 server compact gives faction 103 the mother faction 148.
Pirate faction 161 has mother faction 114.
Factions 148 and 149 have hostile relations with faction 114.
The change uses these authored relations, including the current friendly relation between members of the same faction.
It adds no separate pirate exception.

## Scope

`Doodad.Use` keeps its current owner, skill, skill-less pickup, and property-age checks.
An ineligible footprint does not stop loot or a phase change.
The change does not alter evidence reporting, crime-point amounts, assault, murder, or character deletion.
No SQL schema, compact data, client packet, or client content changes are needed.

## Automated checks

`TheftEvidenceTests` checks invalid callers and ownership before any database or world access.
`TheftEvidencePersistenceTests` uses the disposable MySQL fixture for these checks:

- Offline owners use saved faction relations, including mother factions, hostile factions, pirates, and neutral or unknown factions.
- Fresh reads observe faction changes and deletion.
- Active owners use their current faction even when the saved faction differs.
- Missing and deleted owners cannot use stale world objects to create evidence.
- Active duels do not change friendly-property eligibility.
- Real `Doodad.Use` calls grant loot and change phase for eligible and ineligible theft.
- Eligible calls create persistent footprints with the correct victim, source template, and position.
- Public property, non-criminal skills, self-owned property, and system property keep their current exclusions.

## Pending human checks

Use ordinary harvestable property less than 24 hours old and the same criminal harvest action for both faction checks.

1. Harvest friendly-faction property from another character. Check the loot and the footprint.
2. Harvest enemy-faction property, including pirate property. Check the loot and the absence of a footprint.
3. Repeat the faction checks with the property owner offline. Check that the results stay the same.

These checks need property from another character or a controlled test setup.
They do not need a dungeon encounter.
