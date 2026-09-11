# Skill and zone restrictions for r208022

This change resolves the server scope of cluster issues [446](https://github.com/KeganHollern/aaemu-cluster/issues/446) and [525](https://github.com/KeganHollern/aaemu-cluster/issues/525).
The source base is `c721206cae898b205e86f5512b650d7003f379f6`.
The research date is 2026-09-10.

## Source and client identity

The official upstream search used `AAEmu/AAEmu:develop` at `00ed43a0`.
That branch loads zone tags but does not enforce them.
It does not load `skill_reqs`.

```sh
git -c core.commitGraph=false log upstream/develop --oneline \
  -G 'skill_reqs|groupBannedTags|IsTagBanned' -- AAEmu.Game
git -c core.commitGraph=false grep -n \
  -E 'skill_reqs|groupBannedTags|IsTagBanned' upstream/develop -- AAEmu.Game
```

The client history identifies build `208022`.
The local native files have these SHA-256 values:

| Artifact | SHA-256 |
| --- | --- |
| `client/bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| `.tools-re/dumps/x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| `compact/server.sqlite3` | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |

Both native files have PE timestamp `543cb835`, image base `38ff0000`, and image size `01cb0a00`.
The timestamp represents 2014-10-14 05:44:21 UTC.
The process dump comes from the earlier local r208022 research.
This task reused that exact dump and its Ghidra project in read-only mode.
It did not start a client, change client content, or use a live server.

The dump has RVA-aligned section data.
For the addresses below, `RVA = VA - 0x38ff0000`.
Local decompiler output stays in the ignored directory `.client_files/skill-rules-446-525/`.

## Confirmed skill requirement contract

| Native VA | Confirmed function |
| --- | --- |
| `3972aad0` | Loads `skill_reqs` into records with stride `0x34`. |
| `395d11c0` | Adds the `skill_req_skill_tags` relations. |
| `395d13b0` | Adds the `skill_req_skills` relations. |
| `39899850` | Tests the union of direct skill IDs and skill tags. |
| `3936c5f0` | Implements `X2::GameClient::CheckSkillRequirements`. |
| `39897fa0` | Selects target kinds that skip target buff rules. |
| `39390900` | Reads the skill failure and selects its authored message. |

The rule applies only when the selected unit has the authored buff ID or buff tag.
The native checks cover both buff stores on that unit.
The server uses its combined `IBuffs` view.
The `target` column selects the resolved skill target instead of the caster.
It does not select the caster's `CurrentTarget` property.

The native exception-list rule is:

```text
listed = direct skill ID matches OR any skill tag matches
blocked = buff matches AND listed == default_result
```

The value `default_result = true` defines a denylist while the buff is active.
The value `false` defines an allowlist while the buff is active.
It does not require the buff to exist for each listed skill.
All rules must pass, including allowlist rules without a matching relation.

The target filter skips kinds `6, 7, 8, 9, 11, 12, 13, 14, 15, 18`.
These include position, doodad, and item target kinds.
The native check skips a target rule when its selected target is null.
Caster rules still apply for these target kinds.

A failed rule returns result byte `0x32` and the requirement ID as its 32-bit detail.
The client getter `396f6310` resolves that ID.
The handler uses the record's localized `message` text and buff name.
The server preserves the current `SCSkillStartedPacket` body and extra-data flags.

The compact contains 175 rules, 412 direct relations, and 125 tag relations.
No relation points to a missing rule.
The compact uses null for 101 buff IDs and 74 buff tag IDs.
The loader treats these null values as 0, as the native SQLite reader does.

Examples covered by tests include:

- Rule 1 blocks skill 11020 with root tag 27.
- Rule 13 blocks glider skill 12373 with backpack tag 306.
- Rule 35 blocks skill 15660 with backpack-slot tag 367.
- Rule 25 permits peaceful tag 402 with buff 2149 and blocks an ordinary attack.

## Confirmed zone restriction contract

| Native VA | Confirmed function |
| --- | --- |
| `39751c70` | Loads the complete `zone_group_banned_tags` record, including its period mask and localized `usage`. |
| `395d2000` | Also loads a simple zone-to-tag relation. This relation alone does not preserve period conditions. |
| `397a75d0` | Checks an item's tags against the zone group and active period mask. |
| `397a7670` | Checks a skill's tags against the same conditions. |
| `397a6d60` | Makes mask `1 << siegePeriod`, or 0 when no dominion exists. |
| `3974f3a0` | Builds the ID index for the zone restriction records. |
| `39744520` | Resolves a zone restriction ID for the failure message. |

A row applies when its period mask is 0 or overlaps the current siege period mask.
The compact contains 333 rows.
Of these, 324 rows have mask 0.
The other 9 rows have mask 24 and use guard-summon tag 1338.
Mask 24 selects siege periods 3 and 4.

The server does not have an authoritative dominion store or siege lifecycle.
`DeclareDominion` constructs a temporary packet with period 1, but does not keep authoritative siege state.
This change uses the confirmed no-dominion mask 0.
It loads and tests the conditional rows without making them permanent bans.
A future siege change must supply the live mask to the same rule evaluator.
This dependency does not add a siege system to this batch.

Zone failure byte `0x3a` carries the `zone_group_banned_tags.id` value.
The native loader calls `3974f3a0` with descriptor base `global + 0xfba4`.
That index stores record pointers from `global + 0xfc08` under their authored IDs.
Getter `39744520` uses its index at `global + 0xfba8`.
Handler `39390900`, case `0x3a`, then reads the record's localized `usage` field at offset `0xc`.
The server does not send raw Korean text or invent a new packet field.
Direct non-skill actions use the current `ItemCannotUseHere` or `SkillCannotUseHere` error.

## Confirmed item-source exceptions

An item request must use the actual caster's owned item and its authored `UseSkillId`.
The packet's item template must match that item.
`BindOnPickup` does not permit an unrelated skill.
The previous broad exception could hide a banned item behind an unbanned bound item.

The exact client defines 2 exceptions for portal book 4045:

| Action | Native callback and implementation | Skill | Object and target |
| --- | --- | --- | --- |
| SavePortal | `394cace0` calls `3938f7d0`. | 11215 | `SavePortalInfo`, ID 0, self target. |
| RenamePortal | `394cad10` calls `3938f950`. | 16841 | `SavePortalInfo`, selected portal ID, self target. |

Both implementations find item `0xfcd`, or 4045, through inventory function `394e0960`.
They use its actual item data for the item caster.
The save call at `3938f907` passes skill 11215 from global `39ac7660`.
The rename call at `3938fa6a` passes skill 16841 from global `39ac7668`.
The dump bytes at RVA `0xad7660` are `cf 2b 00 00 d0 2b 00 00 c9 41 00 00`.
These values are 11215, 11216, and 16841.
The compact gives book 4045 the normal use skill 11216 and binding value 2.

The server permits only these alternate skills for book 4045 with the confirmed object and self target.
Save needs portal ID 0, and rename needs a positive portal ID.
The source check runs before GCD, labor, mana, effects, and item consumption.

Fireplace skill 16387 has doodad target kind 8 and a `SavePortal` special effect.
The compact does not assign it as an item's use skill.
This research found no native item-source relation for that skill.
It does not receive an item-source exception.
This change does not alter its ordinary doodad action path.

## Server boundaries

`Skill.Use` resolves its actual target before GCD, buff removal, plots, and costs.
The new buff rules use this target.
The source item comes from the actual caster's inventory, not the packet's template ID.
The skill keeps that item template and the original caster for delayed checks.
Item removal or a plot source substitution cannot remove this context.

The zone lookup uses current world coordinates and the current world instance.
It does not trust a stale `Transform.ZoneId` value.
Placement and fishing checks also use the destination coordinates in that world.

The checks cover these boundaries:

- Ordinary, learned, item, mount, and triggered skills enter through `Skill.Use`.
- A mounted requirement or zone failure stops its linked rider action and sends the authored failure detail.
- Cast completion, delayed effects, plot effects, and channel completion check the current zone again.
- A rejected plot requests cancellation and cannot finish as a successful skill.
- Persistent mate creation and final spawn check the source item and destination.
- Slave item creation checks before replacement, database reads, or ID allocation.
- Direct spawn effects check the skill context and final mate or slave position.
- Doodad packets check item identity and zone rules before labor costs.
- `CreatePlayerDoodad` checks the same actual source item before object creation or item consumption.
- Doodad special effects and fishing rewards check before their world or loot action.
- Direct shipyard creation checks its authored design item before costs and object creation.

The change does not add a Player, Moderator, or Admin exemption.
It does not change confirmed client packet bodies, the compact, or persistent SQL data.
The unused `CSSpawnSlavePacket` remains a stub. This change does not add an unconfirmed packet action.

## Destination rejection and rider failures

A destination check can reject an effect after its source-zone check passes.
That rejection cancels the skill and its active plot.
The normal effect loop then stops before later effects, skill products, `ItemUse`, and source consumption.
The plot path stops before later targets, later effects in the same node, and queued nodes.
Both paths still run their normal cleanup.

The regression uses the real `FishingLoot` destination check inside complete normal and plot paths.
Its product relation is synthetic and tests completion order.
It does not claim an observed product exploit in the current compact.
The allowed case still grants the product, calls `ItemUse`, and consumes the source.

A separate rider failure now sends the rider's skill, source, target, and authored detail.
It does not reuse the primary mount context or discard the detail.
The exact compact links mount skill 11328 to rider skill 11327 through `mount_attached_skills` row 38.
Requirement 1 applies to rider skill 11327 while root tag 27 is active.
Packet tests use a successful primary plot and compare the complete rider failure response.

## Validation

The regression tests use small in-memory SQLite tables with the same rule columns.
They cover all 20 native target kinds, allowlists, denylists, null buff IDs, tag unions, reloads, and missing relations.
Zone tests cover dungeon gliders, mates, ships, doodads, Mirage ship restrictions, fishing, and siege masks.
They also cover current coordinates, destination coordinates, delayed item context, plot cancellation, and failure packet bytes.
Portal tests cover the 2 confirmed alternate skills and reject other books, shapes, targets, and skill IDs.
Full packet tests reject forged bound-item requests through both item and default-skill routes before costs.
Mounted packet tests prove that both authored failures stop the linked rider action.
Full-path tests also check destination-only rejection and rider-only failure.

The full unit suite passed 2718 tests, with 0 failed and 0 skipped.
The build passed with 0 errors.
The exact compact checks found the counts above and 0 orphan skill relations.

```sql
SELECT 'rules', count(*) FROM skill_reqs
UNION ALL SELECT 'direct', count(*) FROM skill_req_skills
UNION ALL SELECT 'tags', count(*) FROM skill_req_skill_tags
UNION ALL SELECT 'zone-rows', count(*) FROM zone_group_banned_tags;
SELECT banned_periods_id, count(*) FROM zone_group_banned_tags GROUP BY banned_periods_id;
SELECT id, use_skill_id, bind_id FROM items WHERE id = 4045;
SELECT id, target_type_id FROM skills WHERE id IN (11215,11216,16841,16387);
```

```sh
dotnet build AAEmu.UnitTests/AAEmu.UnitTests.csproj
dotnet run --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --no-build --
```

After deployment, Kegan or Mike must check these client actions:

1. Use a rooted charge and read its authored failure message.
2. Equip a trade pack and try to open a glider.
3. Use a peaceful skill and an attack while Nui protection is active.
4. Try a glider, mount, ship, planting item, and fishing skill in a restricted dungeon.
5. Try a restricted ship item in Mirage and repeat it in an allowed zone.
6. Leave the restricted zone and repeat the permitted action.
7. Save a portal and rename it with portal book 4045.
8. Try fishing from an allowed zone into a restricted zone. Make sure no later reward or item-use action occurs.

These client checks did not run during source development.
