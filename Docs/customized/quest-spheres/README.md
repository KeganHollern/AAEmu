# Quest sphere geometry for r208022

This change resolves aaemu-cluster issues [89](https://github.com/KeganHollern/aaemu-cluster/issues/89),
[274](https://github.com/KeganHollern/aaemu-cluster/issues/274), and
[275](https://github.com/KeganHollern/aaemu-cluster/issues/275).

## Geometry and events

Each world instance owns its quest geometry.
The loader reads that world's client hint circles and `Data/Worlds/{world}/quest_spheres.json` supplement.
Client coordinates use the zone origin from the same world template.
Supplement coordinates already use world coordinates.
The loader does not change the thread culture.

A player is inside a fixed sphere when the 3-dimensional distance from its center is less than or equal to its radius.
The trigger checks this state every 500 ms.
The first sample can produce an entry event, including at the world origin.
Later samples produce an event only when the state changes.
Each act supplies its own sphere ID for requirement checks.
An NPC trigger uses only its requested NPC template in the same instance.
It keeps the previous inside state so NPC movement can produce entry and exit events.

Sphere starter checks use one previous-position snapshot for the whole tick.
One starter cannot consume another starter's entry movement.
The manager removes old player positions when the player leaves its nearby regions or changes instances.

Registration keeps each owner, quest, volume, NPC template, and sphere ID combination once.
Removal takes effect before the next tick and also handles registration followed by immediate quest removal.
`CheckSphere` uses the quest template ID for removal.
Its event filter checks the act, quest, and component.
Its condition uses `OverrideObjectiveCompleted` because it has no objective counter slot.

An instance transfer removes the old world's triggers and registers current sphere acts with the destination world.
This procedure does not restart quest supplies, timers, or event subscriptions.
It clears the current `CheckSphere` condition before the destination check.
It also clears location objectives that normally reset on exit.
It preserves completed arrival objectives with `TriggerEveryNTimeAfter`.
AreaSphere skill requirements also use the owner's world instance.

The shared logout and disconnect path removes the departing character's triggers and starter positions.
It keeps persisted quest counters unchanged.
Cleanup checks the character reference so a repeated call cannot remove a new session's state.

## Data decisions

The client has 2,479 hint volumes in 69 files across 9 worlds.
All file zone keys exist in their own world XML.
The server compact has 125 sphere objective acts with a current quest context and 1 `CheckSphere` act.
The 2 sphere acts for quests 1421 and 1697 have no quest context.
They are orphan data, not playable quests.

The supplement adds 5 volumes for quests 5716, 5979, 6213, and 6216.
The [destination evidence](late-quest-findings.md) gives exact anchors, world identities, zone keys, and input hashes.
The 30 m radius is an authored value from the issue guidance.
The exact client does not contain the original retail trigger radius for these objectives.

The wider review adds a sixth volume for the starter of quest 578, Burnt Castle Jailbreak.
The [starter findings](starter-findings.md) record its source radius of 5 m and its current NPC anchor.
The other starter gaps remain in [issue 536](https://github.com/KeganHollern/aaemu-cluster/issues/536).

The [exclusion manifest](exclusions.json) records unavailable content and the conditions that keep each exclusion valid.
It does not remove quests or change compact data.
The audit checks closed zones, absent NPC placements, absent starters, or unavailable prerequisite chains against current source inputs.
A new starter, open zone, changed chain, or new geometry makes the relevant exclusion fail.

Closed-zone exclusions refer to normal gameplay.
The `EnterClosedZones` permission bypasses the closed-zone return procedure.
Quests 1372, 1392, and 1400 still lack geometry for that privileged path.

## Checks

Run the audit from the cluster repository after extraction of the exact client sphere files.
The command reads both compact data and world data without changes.
The script opens SQLite with `mode=ro`.
The extracted tree must keep the original `game/worlds/...` paths.

```sh
python3 k8s/vendor/AAEmu/Tools/quest_sphere_audit.py \
  --compact compact/server.sqlite3 \
  --client-root .tools-re/sphere-89/client \
  --world-data k8s/vendor/AAEmu/AAEmu.Game/Data/Worlds \
  --report .tools-re/sphere-89/coverage.json
```

The audit covers client geometry and all server supplements.
It rejects absent geometry, incorrect worlds, invalid volumes, and stale exclusions for sphere objectives and `CheckSphere` acts.
It does not classify every `AcceptSphere` starter as an objective.
The fork build workflow runs the audit fixture tests with Python.

The [recorded audit report](audit-report.json) covers 126 acts, with 110 covered acts and 16 checked exclusions.
The report contains no extracted client records.
It records SHA-256 values for the compact and exclusion manifest.
It also records aggregate hashes for client geometry, supplements, and NPC placement files.
Each aggregate uses sorted relative paths and file hashes in JSON with sorted keys, compact separators, and a final newline.
These hashes identify the exact source inputs for the result.

The audit uses `main_world` unless the manifest gives an explicit world with evidence.
It checks supplement `ZoneId` values against `zones.zone_key`, not the compact row ID.
It does not prove that a chosen location matches the quest narrative.
The destination evidence and supplement tests supply that separate check.

Use `--exclusions` to select a candidate manifest.
The script returns exit code `1` for gaps, invalid volumes, or stale exclusions.
Run the 16 fixture tests from the fork root:

```sh
python3 -m unittest discover -s Tools/tests -p 'test_quest_sphere_audit.py'
```

The C# tests cover both world load orders, parallel load, shared zone keys, repeated registration and removal, and overlapping starters.
They also cover origin entry, vertical boundaries, NPC movement, requirement changes, instance transfers, and `CheckSphere` event subscriptions.
The release checks include the Release build, Game script compiler, full unit suite, and Content Studio suite.

## Gameplay checks after publication

1. Accept quest 6213 through quest 1602, then approach the Riverspan Memory Tome.
2. Accept quest 6216 through quest 1086, then approach the Watermist Forest Memory Tome.
3. Accept quest 5716, then approach Borm Gaffrion in Tryster's Garden.
4. Accept quest 5979, then enter Drill Camp from each team entry.
5. Leave the instance and enter it again with an active sphere objective.
6. Drop and accept an available sphere quest 20 times, then check that each entry gives one event.
7. Use a level 28 or higher Nuia character to approach NPC 2445 in Burnt Castle and check quest 578 acceptance.

For each new location, check the objective at its center and at the 30 m boundary.
The automated boundary tests also check a point immediately outside the radius.
The tests do not establish human gameplay results.

Quest 5716 also refers to an absent NPC spawner through its song effect.
The destination evidence records this separate effect defect.
This change supplies the sphere geometry and does not change that effect.
