# Peace protection for r208022

This record supports [cluster issue 284](https://github.com/KeganHollern/aaemu-cluster/issues/284).
The check used deployment commit `61f535c0c0953f1c8da249c77ac74c330a74c2a4` on 2026-09-10.
The change adds server attack protection from `peace_protected_faction_id`.
It does not change a packet, database, compact snapshot, or client file.

## Exact client and tools

`client/history.txt` identifies client revision `208022`.
The following paths are relative to the cluster workspace, not this fork.

| Module | Role | Bytes | SHA-256 |
| --- | --- | ---: | --- |
| `client/bin32/x2game.dll` | Source PE | 18483712 | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| `.tools-re/dumps/x2game.dumped.dll` | Unpacked PE dump | 30085120 | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |

Both PE headers have these values:

| Field | Value |
| --- | --- |
| Machine | i386 |
| PE timestamp | `0x543cb835`, 2014-10-14 00:44:21 UTC |
| Preferred image base | `0x38ff0000` |
| Entry RVA | `0x008c5d6d` |
| Image size | `0x01cb0a00` |

The dump tool and command are not recorded in the available artifacts.
This provenance gap also applies to earlier research that used this dump.
The inspected code and vtable pointers use the preferred base.
All addresses below are virtual addresses at that base, not file offsets.
The `.text` section starts at VA `0x38ff1000` and file offset `0x1000`.

The check used GNU `objdump` and Ghidra `12.1.3_PUBLIC` with JDK `21.0.12.1+1`.
Ghidra analyzed an isolated project under `.tools-re/issue-284/ghidra`.
The helper was `.tools-re/weather/DecompileAddresses.java`.
Its SHA-256 is `dbacaeedd37cb521b8e19429d3875fa5afdba58978bf0c7fc71d02b917b1ad87`.
The local Ghidra application log contains the selected decompilations.
Raw binaries and generated decompilations stay outside Git.

## Starting evidence and history

`ZoneManager.LoadConflictZones` loads the protected faction ID.
`ZoneConflict` stores it but the old combat path never reads it.
`BaseUnit.CanAttack` uses static zone factions and faction relations.
`Skill.GetInitialTarget` accepts hostile faction relations without that attack check.
`DamageEffect` checks `CanAttack` late, after some combat effects.
`Unit.ReduceCurrentHp` also serves damage outside `DamageEffect`.

The local server compact has SHA-256
`636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.
All 16 `conflict_zones` rows use `peace_protected_faction_id=5`.
Their group IDs are `14,15,16,17,19,20,22,23,26,27,30,36,39,59,60,78`.
There is no `system_factions` row with ID `5`.
Thus a normal faction lookup cannot explain this value.

History search found the field loads in `78fddc62`, `77c653a4`, and `da7a3c0d`.
It found no later combat use in the available fork history.
The checked upstream tip was `00ed43a0`.
Its `BaseUnit.CanAttack` also lacks this rule.
Upstream source supplies comparison evidence, not authority for the r208022 client.

## Native contract

The native relation result has separate relation and attack fields.
Byte `0` holds relation `1=Hostile`, `2=Neutral`, or `3=Friendly`.
Bit `0` of byte `1` means attack allowed.
Byte `2` holds the refusal or exception reason.
Peace clears the attack bit without changing the relation.
Thus Peace must not make opposing factions friendly.

| Address | Confirmed role |
| --- | --- |
| `0x39829420` | Loads `conflict_zones`. The SQL string is at `0x39a15950`. |
| `0x397f2080` | Finds a conflict record by zone-group key. |
| `0x39027eb0` | Selects zone protection and handles closed conflict rows. |
| `0x390298d0` | Returns record offset `0x3c` only when the live conflict state is `7`, Peace. |
| `0x390281c0` | Installs the zone protection callback at relation context offset `0x2c`. |
| `0x39837b90` | Resolves protection selectors `0`, `4`, `5`, or a normal faction ID. |
| `0x39837d00` | Applies player protection only when both units have a player owner. |
| `0x39838880` | Applies protection and exceptions to the attack bit. |
| `0x39028240` | Gets the local character's relation result against a unit. |
| `0x39027f40` | Gets the relation result for 2 supplied units. |
| `0x393910e0` | A target consumer tests result mask `0x100` before it replaces the target. |

The conflict record has stride `0x4c`.
The loader puts `closed` at `0x00`, `peace_min` at `0x38`, and `peace_protected_faction_id` at `0x3c`.
It puts `peace_tower_def_id` at `0x40` and `war_min` at `0x44`.
This load order identifies the selected field without an inference from its name.

An open conflict row overrides the static zone faction.
It returns protection `0` outside Peace.
A closed conflict row also returns protection `0`.
Non-conflict zones use a different path for static zone and siege protection.
This change does not replace that path.

| Selector | Native meaning |
| --- | --- |
| `0` | No protection. |
| `4` | Protect units friendly to Nuia `148` or Haranya `149`. |
| `5` | Protect all units with a player owner. This includes pirates. |
| Other ID | Protect units friendly to that faction. |

The selector constants are at `0x39acb680` through `0x39acb68c`.
The values are `4`, `148`, `149`, and `5`.
`0x39837a00` resolves normal faction relations and tests for Friendly.
Each unit supplies its own zone through virtual offset `0x1c`.
An owned unit does not inherit its owner's position for this check.

## Exceptions and owned units

The native unit relation interface starts at unit offset `0x3c28`.
Its vtable starts at `0x399d08d0`.
Virtual offset `0x24` calls `0x39356b60` for the player-owner ID.
Characters supply their character ID.
Mates, slaves, houses, and shipyards supply their owner's character ID.
NPCs supply `0`.
Thus an NPC does not gain PvP immunity from selector `5`.

The server uses stored owner IDs for houses, slaves, and shipyards.
An offline owner does not remove their protection.
The shipyard owner ID is the current `ShipyardData.Type2`, which `ShipyardManager.Create` sets from `owner.Id`.
The current mate path resolves its owner through `OwnerObjId` in its world.

`0x39838880` uses this order for player-versus-player checks:

1. An active duel pair bypasses Peace protection.
2. A protected character at level `10` or lower cannot start or receive the attack.
3. Hostile targets permit the attacker's active retaliation exception.
4. Other protected hostile targets refuse the attack.
5. Friendly and neutral targets permit the target's Retribution exception.
6. Other protected friendly and neutral targets refuse the attack, before forced PvP can apply.

Same-owner and team checks are separate native restrictions.
The Peace helper only adds a refusal to the current combat rules.
An exception removes that refusal. It does not override other server restrictions.

The low-level predicate at `0x39837a90` first asks for the unit's character ID.
`0x390b4990` returns that ID only for type `0`, Character.
Thus the level guard does not use a mate's level or its owner's level.

The duel predicate is at `0x39358ab0`.
It needs different owners and the same nonzero duel flag.
The flag getter at `0x3935feb0` supports Character and Mate, not Slave.
The server checks the exact active duel pair after the countdown.
It does not exempt 2 unrelated characters because both are in a duel.

The Retribution getter at `0x3935ff30` uses buff `2167` at `0x39ac744c`.
It checks Character directly and the owner for Mate or Slave.
It does not grant that exception to houses or shipyards.
For hostile faction relations, Retribution alone does not bypass Peace.

The retaliation predicate at `0x39837ad0` passes the target's object ID to virtual offset `0x3c`.
The getter at `0x39356cb0` accepts only the local character in combat.
It tests the target object in the manager's set through `0x39394e40`.
It does not pass the target owner's ID.
The server now records owned attackers by object ID too.
It keeps the current `WorldManager.DefaultCombatTimeout` of `15` seconds.
The native code confirms the pair and combat-state checks, not this timeout duration.

The house exception at `0x39837800` reads the demolished flag through `0x39356d20`.
The demolition path at `0x3932a34e` sets that flag and clears the house owner at offset `0x20`.
The server demolition path also clears `House.OwnerId` before it makes the house killable.
The owner-ID check thus leaves demolished houses outside Peace protection.

## Client script evidence

The archive path is `game/scriptsbin/x2ui/hud/clock.alb`.
Its extracted SHA-256 is `b2dd5e7e09e87bb3ce833d7ada4f919d59fc5a9a378048dd3c0a62c039a9c748`.
Its strings include `HPWS_PEACE`, `HONOR_POINT_WAR_PEACE`, `GetZoneFaction`, `X2Faction.GetFactionInfo`, and `statePeaceTooltip`.
They support the Peace UI and faction display.
They do not prove attack eligibility or server damage rules.
No UI file changed.

## Server decision and validation

`PeaceProtection` reads the current zone group and conflict state for each check.
It has no saved protection flag or relation cache.
Peace entry, Peace exit, and movement thus use the current state.
`BaseUnit.CanAttack` and hostile initial skill targets use the same check.
Non-friendly skill effects check it for each affected target.
`BuffEffect` also checks Bad buffs before direct, plot, or tick application.
This covers mixed friendly/non-friendly effects in self-centered skills, such as skill `12001` and buffs `2278` and `975`.
`ManaBurnEffect` and `DisturbCasting` check before MP loss or cast cancellation.
This also covers delayed skill `12002`, which uses both friendly flags for its interrupt effect.
Good buffs, NPC effects, and self effects keep their current paths.
`DamageEffect` checks before bonuses, buff removal, crime state, and damage.
The HP methods check again for direct or delayed damage.
GM kills, self damage, and NPC combat keep their current paths.

The client establishes the attack contract, not the original server's HP code.
The repeated server damage checks are an explicit inference from that contract.
They prevent a cast accepted before Peace from bypassing the new state.
No schema, persistence, or deployment order change is needed.

The regression suite covers all selectors, both alliances, pirates, and Peace entry and exit.
It also covers forced PvP, Retribution, retaliation pairs, combat exit, low levels, duels, owned units, and offline owners.
Damage tests check delayed HP loss and the early `DamageEffect` refusal.
A target test checks the private initial hostile-target path.

| Local check | Result |
| --- | --- |
| `dotnet build AAEmu.slnx` | Passed, 0 errors. |
| `PeaceProtectionTests` | 34 passed, 0 failed. |
| Full `AAEmu.UnitTests` | 2395 passed, 0 failed, 0 skipped. |
| `AAEmu.IntegrationTests` | The local fixtures could not start. |
| `git diff --check` | Passed. |

The integration fixtures need Docker and a MySQL config.
This host has no Docker or Podman endpoint, and the local config retains `%db_port%`.
The fork PR workflow supplies MySQL for the integration gate.

After deployment, do these r208022 gameplay checks:

1. Test opposing characters across Peace entry and exit in a conflict zone.
2. Test forced PvP and Retribution with same-faction characters during Peace.
3. Test a mate and a ship across the zone boundary during Peace.
4. Cast damage before Peace starts and check HP after Peace starts.
5. Check a duel during Peace and normal combat against an NPC.

These gameplay checks need human validation on the published release.
The native evidence and automated tests do not replace them.
