# Skill and ability feedback for r208022

This change resolves aaemu-cluster issue #134. The audit found 1 active defect in the 4 packet claims.
Learned skill ranks did not reach the client after an ability level increase.

## Exact inputs

| Input | Identity |
| --- | --- |
| Server base | `4268f2f9310405d1b4e6463f331f435fcf5a898c` |
| Client revision | `r208022`, from `client/history.txt` |
| Packed `x2game.dll` SHA-256 | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| Runtime dump SHA-256 | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| Server compact SHA-256 | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |
| `game/scriptsbin/x2ui/skill/tab_combat.alb` SHA-256 | `ee57b1e415dd42954e7e8844f73ea9434a18a31782f6ddcd1921bb5b329199a9` |

The packed DLL and dump share PE timestamp `0x543cb835`, entry point `0x8c5d6d`, and image size `0x1cb0a00`.
Addresses below use dump base `0x38ff0000`.
The dump comes from the retained r208022 runtime research input. This audit did not create a new runtime capture.
Local exports are under `.tools-re/combat-effects-321-134-20261004/feedback/` in the cluster workspace.
The audit used a separate copy of the retained Ghidra project.

## Audit result

| Original claim | Result |
| --- | --- |
| Cooldown state never reaches the client. | Earlier #132 work sends the snapshot after `CSNotifyInGameCompleted`. Commit `db230ebb640669775e978e816663fc17654c4431` records the native lifecycle. |
| `SCSkillStopped` must handle every cast interruption. | Incorrect for r208022. Normal casts use `SCCastingStopped` and `SCSkillEnded`. `SCSkillStopped` handles the melee auto-attack state. |
| Active ability XP needs `SCAbilityExpChanged` after every reward. | Incorrect. `SCExpChanged` with `shouldAddAbilityExp=true` updates those abilities and their UI. A second XP delta would repeat the gain. |
| Skill upgrades need a client notice. | Confirmed. The server computed new cast ranks but kept the learned entry and client rank unchanged until reload. |

`CharacterAbilities.AddExp` has no runtime callers in this source tree. Its TODO does not prove an active ability-only reward defect.
`ActAbility` is vocational proficiency. It uses its own labor and proficiency packets, separate from combat `Ability` XP.
The audit adds no packet to the unused ability-only method.

## Confirmed packet contracts

All packets below are G2C level `1`. `bc` is the fixed 3-byte object identifier used by this client.
No listed body has optional fields or trailing groups.

| Packet | Opcode | Body in wire order | Bytes |
| --- | --- | --- | ---: |
| `SCExpChanged` | `0xfe` | `bc unitId`, `i32 delta`, `u8 shouldAddAbilityExp` | 8 |
| `SCAbilityExpChanged` | `0xff` | `bc unitId`, `u8 ability`, `i32 delta` | 8 |
| `SCSkillUpgraded` | `0x106` | `u32 skillId`, `u8 rank` | 5 |
| `SCSkillStopped` | `0xa4` | `bc unitId`, `u32 skillType` | 7 |
| `SCCastingStopped` | `0xa5` | `u16 timelineId`, `u32 duration` | 6 |

Factories establish the opcode, allocation, and parser vtable independently of each parser body.
The stream helper `0x397b27d0` reads `unitId` through stream slot `+0xcc` with byte count `3`.
The scalar slots are `+0x3c` for `u32`, `+0x40` for `u16`, `+0x44` for `u8`, and `+0x4c` for `i32`.
The boolean slot is `+0x78`. The current server writers match these bodies.

| Packet | Factory | Parser | Handler |
| --- | --- | --- | --- |
| `SCExpChanged` | `0x391aeb10` | `0x397c6580` | `0x391d9bd0` |
| `SCAbilityExpChanged` | `0x391aed60` | `0x397c65d0` | `0x391d9c60` |
| `SCSkillUpgraded` | `0x391af110` | `0x397c67c0` | `0x391da210` |
| `SCSkillStopped` | `0x391aba00` | `0x397c6300` | `0x391d5c70` |
| `SCCastingStopped` | `0x391ab900` | `0x397ca500` | See the earlier #452 record. |

### XP and UI

`SCExpChanged` reaches `0x39356f40`, then local-player XP consumer `0x39356d60`.
The consumer adds the player delta and calculates the resulting level.
When the flag is true, it calls `0x398a1720` to update active abilities through `0x398a1290`.
The consumer emits the XP event and the ability-level event when a level changes.
`SCAbilityExpChanged` instead calls the ability consumer directly. Ability value `11` selects all active abilities.
Both routes add a delta. They do not replace the total.

The exact `tab_combat.alb` registers both `EXP_CHANGED` and `ABILITY_EXP_CHANGED`.
Their callbacks at source lines `616-621` call `UpdateAbilityExp`.
`PLAYER_ABILITY_LEVEL_CHANGED`, at lines `623-625`, calls the full window `Update`.
These callbacks confirm live ability-bar updates without a second XP packet.

### Learned ranks

The client keeps a learned skill map. Lookup `0x390b8a20` reads the map without changing ranks.
`0x390b9950` uses the stored rank for a learned ability skill.
General skills use the current character level instead.
Tooltip function `0x39421720` uses that learned rank for a learned player ability skill.
Its separate unlearned preview path calculates a rank from the current ability level.

`0x39898000` calculates the rank from `ability_level` and `level_step`:

```text
if abilityLevel < requiredLevel: rank = 0
else if levelStep == 0: rank = 1
else: rank = (abilityLevel - requiredLevel) / levelStep + 1
```

The server already uses the same positive-rank formula when it constructs a cast.
`SCSkillUpgraded` reaches `0x39384650`, which calls `0x390ba010` to replace the stored byte rank.
It also emits the skill-upgrade UI events. The XP consumer does not replace learned skill entries.
The old server never sent this rank update after XP changed an ability level.

### Cast interruption

`SCSkillStopped` consumer `0x39398410` compares `skillType` against the melee auto-attack constant at `0x39ac769c`.
That constant is `2`. Other values reach the log message `stop skill: impossible case`.
This packet must not become the general cast-cancel notice.
`Skill.Stop` already sends `SCCastingStopped`, then ends the skill or channel.
The #452 tests cover movement, repeated cancellation, old callbacks, and plot cancellation.
See `r208022-cast-movement-and-damage-452.md` for that lifecycle and its pending human checks.

## Change and lifecycle

`CharacterSkills.RefreshLearnedSkillRanks` compares each learned active-ability rank with the current cast rank.
It updates only higher ranks. It sends entries in skill-ID order and does not learn new skills.
The server does not charge another skill point or change skill ownership.
General skills and inactive abilities do not receive this notice.

The normal XP path sends `SCExpChanged` first, then sends the changed ranks.
The shared labor reward callback follows the same order after commit.
The auction-sale receipt path sends the rank updates after its committed XP notice.
Failed paid-skill transactions preserve the previous rank and send no XP or rank update.
A repeated refresh sends no duplicate rank notice.
The existing skill save path stores the updated rank. Login already reconstructs ranks from ability levels.

The current level cap is `55`. The authored positive level steps fit the native byte rank field.
The wire test also covers ranks `0` and `255` without changing the gameplay level cap.
There is no new client content, SQL migration, compact change, or server rule.

## Automated checks

`SkillFeedbackTests` checks combined XP updates, active and inactive abilities, rank order, unchanged ranks, deferred publication, and the complete upgrade packet body.
`SkillLaborTests.ExperienceRank_CommitsWithPaidSkill_AndFailureSendsNoUpgrade` checks both commit results against the real paid-skill path.
`AuctionMailClaimManagerTests.GetAttached_SaleExperiencePublishesOneRankOnlyAfterCommit` checks both sale commit results, packet order, and repeat claims.
The focused runs passed all `12` new cases: `8` feedback cases, `2` paid-skill cases, and `2` auction cases.
The release record contains the final build and full test results.

## Pending human checks for #573

Use the published server and the current r208022 client. A GM can prepare a character near each level threshold.
The following checks do not claim a human pass.

1. Learn Magic skill `10667` at Magic level `5`. Earn enough ordinary XP to reach Magic level `6`.
   Keep the ability window open. Check that its XP bar changes immediately and the learned skill reaches rank `2`.
   Check the tooltip and a cast, then reconnect. The rank must remain the same after reconnect.
2. Prepare the same threshold with a valid craft or gathering reward. Complete the action and check the immediate rank update.
   Cancel another action before completion. It must give no reward or rank increase.
3. Cast normal skill `10107`, then move before completion. Repeat with plot skill `10752` and channel skill `10372`.
   Check that each active cast or channel stops. Start another skill and check that the old action does not stop it.
4. Prepare an auction-sale receipt and an active ability near a rank threshold. Claim the completed sale.
   Check the immediate XP and rank update, then claim again. The second request must give no duplicate reward or rank notice.

Check 4 needs a completed sale. Use a second character or a GM-prepared sale in the test environment.
The rendered rank notice, tooltip refresh, and cast bars still need these human checks.
