# r208022 stat limits and kill XP: #453 and #479

## Changes

`UnitAttributeLimitsGameData` reads the authored raw limits. The shared unit calculator applies each limit after bonuses and before a getter converts the result. Manual NPC, mate, slave, transfer, and shipyard getters use the same final limit where an authored row exists. Integer getters keep their old truncation points through `Math.Truncate`, without an early integer overflow. `Character.SpellCritical` combines its `SpellCritical` and `SpellDamageCritical` contributions before the final `SpellCritical` limit.

The current compact has 21 limits. The current XP and GCD loaders keep their 2 rows and conversions. The new loader owns the other 19 rows. It rejects invalid or duplicate rows and replaces its snapshot only after a complete load. No compact or persistent database change is needed.

The no-tag kill fallback now calls the same award function as tagged kills. Both use the recipient owner's level, the same level window, and the same level multiplier. Both compute player and pet awards before player XP can change the owner's level. Tagged party and raid shares stay unchanged. Each final award uses the old float calculation and integer truncation. A large positive result cannot wrap into negative XP.

The preserved rule awards no XP when `recipientLevel - npcLevel` is at least 10 or at most -10. Otherwise, the award is `KillExp * share * (1 - 0.1f * levelDifference)`. For a level-30 NPC with 1000 base XP, a level-35 solo owner gets 500 XP. A level-39 owner gets 99 XP because the prior float calculation truncates that result. This change does not redesign the level window, team allocation, pet level rules, achievements, quests, or loot.

## Exact-client evidence

The research used the r208022 client and the server compact read-only. The unpacked DLL came from the earlier local dump. This work did not repeat that extraction.

| Input | Identity |
| --- | --- |
| `client/history.txt` | Client version 208022 |
| Original `client/bin32/x2game.dll` | SHA-256 `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| `.tools-re/dumps/x2game.dumped.dll` | SHA-256 `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| PE identity, original and dump | Timestamp `0x543cb835`, image base `0x38ff0000`, entry RVA `0x008c5d6d`, image size `0x01cb0a00` |
| `compact/server.sqlite3` | SHA-256 `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |

Addresses below are virtual addresses in that dump. Local disassembly and decompilation logs are under `.tools-re/combat-rules-20260926/stats/` in the cluster workspace.

| Native address | Confirmed behavior |
| --- | --- |
| `0x39577a20` | Loads `unit_attribute_id, minimum, maximum` from `unit_attribute_limits`. |
| `0x398a0bc0` | Computes a raw attribute from its base and bonus contributions, then calls the limit function. |
| `0x395729f0` | Applies the inclusive signed minimum and maximum for that attribute. |
| `0x3989fde0` | Adds Value contributions to a flat accumulator and Percent contributions to a percent accumulator. |
| `0x398a0d60`, `0x398b4e80` | The base getter and character base getter return no separate movement base for attribute 10. |
| `0x398aa290` | The movement display converts raw attribute 10 with `raw / 10 + 100`. |

The raw movement maximum is 3000. The native display proves this means a 300% bonus and 400% total speed. The issue's proposed 30% maximum is incorrect.

The server currently uses 1000 as the normal-speed seed before it applies Percent movement modifiers. This release preserves that calculation. It subtracts 1000 from the calculated value, applies the raw limit, then restores 1000 before division by 1000. This baseline conversion is an inference that preserves the server's authored Percent effects. The research does not establish the full native physical-movement calculation. It does establish the raw limit and display conversion.

The compact has 34 Percent movement rows. Examples include buff 958 at -50, buff 2054 at -80, and buff 5226 at -100. A zero seed would remove those standalone slow effects. Tests retain their 0.5, 0.2, and 0 multipliers. The raw minimum remains the authored -10000. This release does not invent a zero movement floor.

## Correction to the primary-stat audit

`character_p_stat_limits` does not cap final Strength, Dexterity, Stamina, Intelligence, or Spirit. It limits a separate permanent pStat allocation array.

| Native address | Confirmed behavior |
| --- | --- |
| `0x39752fc0` | Loads `p_stat_id, max, min` from `character_p_stat_limits` into descriptors and an index. |
| `0x39744620` | Gets a pStat descriptor from that index. |
| `0x3939dd10` | Checks a requested pStat change against the stored pStat array at unit offset `0x3bcc`, before derived stats. |
| `0x391e26b0` → `0x393547d0` → `0x390b3820` | Applies the AddCharacterPStat event to that array and invalidates computed attributes. |
| `0x398b4e80` | Adds stored pStat to the character base formula before equipment and buffs. |

The compact has 5 pStat rows with bounds 0 through 10000. It has 0 `special_effects` rows with type 113, `AddPStat`. Thus no `effects` wrapper can reach such a row in this snapshot. The server's `AddPStat` effect remains a stub, and `SCUnitStatePacket` sends 6 zero pStat values. These facts support a dormant feature, not a limit on total primary stats. This release does not add that feature or cap equipment and buff totals at 10000.

## Separate defects for follow-up

### Percent bonuses use different accumulation rules

Native `0x3989fde0` adds Percent contributions together. Native `0x398a0bc0` then applies that sum once to base plus flat contributions. The server's `Unit.CalculateBonuses` applies each Percent contribution in sequence. Manual NPC, mate, and slave loops also apply each contribution in sequence. For a base of 100 and two +50% bonuses, the native result is 200 and the server result is 225, before limits.

This is a separate calculator issue. The compact has Percent content for MaxHealth (368 static rows), Armor (66), and MeleeDpsInc (40), among other attributes. Movement includes NPC 439 at +100% and NPC 3673 at +300%, as well as the slow buffs above. A follow-up needs exact ownership, simultaneous applicability, dynamic bonus timing, and equipment ordering tests. This release preserves the server's bonus order and applies limits to its final raw result.

### Some authored attributes have no current getter

The loader retains their limits. No computed value exists to limit for the following attributes.

| Attribute | Static modifiers | Dynamic modifiers | Examples |
| --- | ---: | ---: | --- |
| `MeleeBlock` (21) | 0 | 0 | The current `BlockRate` getter uses `Block` (177), not 21. |
| `MeleeSpeedMul` (54) | 210 | 26 | Row 10977: Buff 651, -600. Row 12099: Buff 757, +500. |
| `RangedSpeedMul` (55) | 181 | 22 | Row 10978: Buff 651, -600. Row 12101: Buff 757, +500. |
| `AttackAnimSpeedMul` (119) | 189 | 12 | Row 24601: Buff 4313, +700. Row 24622: Buff 4343, -700. |

No runtime getter or calculator consumes attributes 21, 54, 55, or 119 in this source. The speed attributes need a separate native timing and attack integration study. Loading their bounds does not make those absent mechanics work. This release addresses limits on current computed paths and does not claim complete combat timing behavior.

## Automated checks

`UnitAttributeLimitsTests` covers the authored raw units, movement caps, stacked effects, removal, Percent slows, flat and Percent order, and dynamic contributions. It checks both raw endpoints and the actual character conversion getters. It also tests the two contribution groups for spell critical chance, manual unit getters, overflow before conversion, preserved truncation, loader reloads, invalid rows, and the 19 exact compact limits. Each test restores the singleton state it replaces.

`NpcKillExperienceTests` covers the level window, the level multiplier, solo, party, raid, and pet shares, old float truncation, and positive overflow. The excluded fallback test has no world or reward templates. An incorrect award attempt would reach those missing dependencies and fail.

The Release build passed. Focused tests passed with 0 skips: 30 stat-limit tests, 14 kill-XP tests, 12 existing XP-modifier tests, 22 existing cooldown tests, and 11 existing quest-sharing tests. The exact compact test uses `AAEMU_COMBAT_TEST_COMPACT` with a read-only SQLite connection.

```sh
dotnet build AAEmu.UnitTests/AAEmu.UnitTests.csproj -c Release -m:1 -p:UseSharedCompilation=false
AAEMU_COMBAT_TEST_COMPACT=/absolute/path/to/server.sqlite3 dotnet AAEmu.UnitTests/bin/Release/net10.0/AAEmu.UnitTests.dll --treenode-filter '/*/*/UnitAttributeLimitsTests/*' --no-progress
```

The other focused runs use the same test command with `NpcKillExperienceTests`, `ExperienceModifierTests`, `SkillCooldownTests`, and `QuestTeamShareTests`.

## Pending human validation for #573

- [ ] For #453, record normal movement speed. Apply a known speed buff, then remove it. Check that normal speed returns.
- [ ] For #453, apply a known Percent slow. Check the slow and normal movement after it ends. Include a mount if the buff permits mounts.
- [ ] For #453, use suitable GM test buffs to exceed movement, cast-time, damage, healing, and armor bounds. Check the displayed values and gameplay result. Remove the buffs and check normal values again.
- [ ] For #479, record player and active pet XP. Kill same-level and lower-level NPCs with normal tags. Check the awards and the 10-level cutoff.
- [ ] For #479, use a controlled server test setup to kill an NPC through the no-tag fallback. Check that player and pet awards match the normal path for the same levels and shares.

These checks need the released server. No human gameplay result is claimed here.
