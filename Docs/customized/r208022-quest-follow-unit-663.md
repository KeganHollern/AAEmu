# Quest-component FollowUnit, issue 663

This change adds server control for the authored quest-component FollowUnit mode.
It uses the exact NPC from a valid quest interaction.
The unused FollowUnit command sets do not define this behavior.

Issue: [aaemu-cluster #663](https://github.com/KeganHollern/aaemu-cluster/issues/663).
Research date: 2026-10-10 UTC.
Source input: `6c82d9db8d41e0338367889a5fd531020aa74262`.
Official base: `b34db3e5f5bce0b9b9bac2a6916a936f0e946a1f`.
Official `develop` still handles only AttackUnit in `Quest.SetNpcAggro`.
The official issue and PR search found no FollowUnit fix on this date.

## Input identities

The client revision is `r208022`, from `client/history.txt`.
The research measured these SHA-256 values:

| Input | SHA-256 |
| --- | --- |
| Client compact | `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4` |
| Server compact | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |
| Packed `x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| Runtime dump of `x2game.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| `quest_context.alb` | `d11e5d34258870a042f89f39c0a39cdc85b968a7b07a3edc71c27867ee872efc` |

The task extracted selected files from `game_pak`, which contains 24,885,257,728 bytes.
The task did not compute a new hash for the complete archive.
No client or compact file changed.

## Confirmed content

The client and server compact rows agree on these components and acts.
Component kind 4 means Progress in `QuestComponentKind`.

| Quest | Component | Authored condition and control |
| --- | --- | --- |
| 851, Skills of Persuasion | 3273, Start | Act 4729 accepts from Soldier Zhao, NPC 5053. |
| 851 | 3274, Progress | Talk act 4730 selects Runaway Soldier 5055. FollowUnit uses that NPC and skill 13613. |
| 851 | 3275, Ready | Report act 4731 selects Soldier Zhao 5053. |
| 1039, Protect the Reindeer | 5091, Start | Act 7991 accepts from Healer Laura, NPC 4624. |
| 1039 | 5092, Supply | Act 12751 gives Elemental Reindeer Perfume 15840. |
| 1039 | 5093, Progress | Talk act 12752 selects Injured Reindeer 4729. FollowUnit uses that NPC. |
| 1039 | 5094, Progress | Sphere act 12754 selects sphere 943 and NPC 4729. Act 13129 checks component 5093. |
| 1039 | 5095, Ready | Report act 13376 selects Healer Laura 4624. |

These FollowUnit components have no command set, path, or component buff.
Item 15840 has no use skill or buff.
Both reindeer components belong to the same Progress step.

Skill 13613 has a 2,000 ms effect delay.
Effect 8011 uses BuffEffect 2637 to apply Faith 1544 for 4,000 ms.
Faith describes the soldier's trust and follow behavior.
Its timeout triggers 760 and 761 add bubble 235 and In Disbelief 1542.
In Disbelief lasts 90,000 ms and uses taunt with top aggro.
Its timeout trigger 755 uses effect 8015 to vanish NPC 5055 within radius 25.
The traced chain contains no NpcControlEffect.
The buff durations are authored facts. Their relationship to the retail follow lifetime remains unknown.

`quest_context.alb` prototype 5 gets the quest, component, and act IDs for an NPC talk action.
It calls `X2Quest:TryProgressTalkQuestComponent` with those values and the talk argument.
The native binding at `0x394b6fa0` reports that `ScriptBindUnit::NpcFollowUnit()` has no client support.
These facts establish the talk route. They do not establish retail server ownership or stop rules.
No packet contract changed.

## Server rules and limits

These ownership and stop rules are server choices. No retail capture confirms them.

An accepted NPC remains an exact object reference for an applicable component.
A FollowUnit talk must match the quest, component, act, NPC template, and source character.
The NPC AI gives one quest an exclusive follow lease.
Another character or quest cannot replace that lease.
The component skill uses the same NPC occurrence.

The lease continues through Progress and Ready.
Quest 851 reaches Ready immediately after its talk, so a Progress-only lease cannot produce useful movement.
Completion, Reward, Drop, Fail, disconnect, death, despawn, world loss, and another explicit AI control end the lease.
Cleanup removes only the quest's own control and restores its saved idle position when it still owns the target.
The owning AI tick restores Idle if no other control replaces FollowUnit.
Normal idle and default transitions resume a valid lease after talk or combat.
Movement uses the complete tick duration through `TimeSpan.TotalSeconds`.

A saved quest does not select a new NPC by template after reload.
An unfinished follow component needs a new exact valid talk to restore its lease.
That talk does not repeat the component's skill or rewards.
A saved Ready quest remains reportable without a replacement NPC.
The change adds no CheckGuard condition to quest 1039.

For a follow-specific sphere objective, both the owner and the exact leased NPC must enter the authored sphere.
The objective checks the live lease again before it accepts saved sphere credit.
Other NPC-centered sphere objectives keep their current rule.
This geometry rule is a server choice based on the authored NPC requirement and destination text.

## World availability and human checks

Quest 851 has a natural route in Villanelle.
Soldier Zhao appears at `(22072.2, 11300.89, 220.56587)` in `main_world`.
The world contains 4 Runaway Soldier placements in Bleakfish.
Its client sign volume belongs to component 3274, but the compact objective remains Talk.

Quest 1039 belongs to closed Airain Rock, zone 19, zone key 154.
None of the 69 client `quest_sign_sphere.g` files contains quest 1039 or components 5093 and 5094.
The tracked worlds contain no placement for NPC 4624 or 4729.
No quest sphere supplement supplies its destination.
The current [sphere audit exclusion](quest-spheres/exclusions.json) already records these missing placements.
This change does not invent sphere coordinates or add world content.
Controlled test fixtures cover the server path. Natural quest 1039 gameplay remains unavailable.

The manual backlog is [HUMAN VALIDATION #573](https://github.com/KeganHollern/aaemu-cluster/issues/573).
Use an eligible level 26 test character for quest 851.
Accept from Soldier Zhao in Torchfire Bay, then talk to one Runaway Soldier in Bleakfish.
Check that only that occurrence follows, then check the authored trust and disbelief effects.
Report to Soldier Zhao once and check the reward and quest history.
Use another eligible test character for abandonment, disconnect, and competing-owner checks.
Leave those checks open until a human reports a pass for the published release.
Leave the natural quest 1039 check unavailable until authored placements and destination geometry exist.

## Reproduction and automated checks

Query each compact as read-only data:

```sql
SELECT * FROM quest_components WHERE quest_context_id IN (851, 1039);
SELECT * FROM quest_acts WHERE quest_component_id IN (3273,3274,3275,5091,5092,5093,5094,5095);
SELECT * FROM quest_act_obj_talks WHERE id IN (353,804);
SELECT * FROM quest_act_obj_spheres WHERE id = 498;
SELECT * FROM buffs WHERE id IN (1544,1542);
SELECT * FROM buff_triggers WHERE buff_id IN (1544,1542);
```

`QuestFollowUnitTests` covers exact NPC identity, authored talk IDs, ownership conflicts, skill targets, terminal cleanup, reload, target loss, movement, and sphere credit.
The release also runs the full unit suite, Game MySQL suite, Content Studio suite, sphere audit, and Game script compiler check.
The cluster source-selection record contains the exact tested tree and check results.
