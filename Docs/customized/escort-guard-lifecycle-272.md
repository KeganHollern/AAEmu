# Escort guard lifecycle, issue 272

This change adds the missing runtime guard check for
[aaemu-cluster#272](https://github.com/KeganHollern/aaemu-cluster/issues/272).
It does not change NPC paths, quest objectives, compact data, or packets.
The logout and server-restart rule needs a separate decision before release.

## Evidence and scope

The source baseline is `1b35411e93185946a4757c29cba8748d7d626838`.
Official upstream `develop` at `6e2f40734` still returns `true` from
`QuestActCheckGuard.RunAct`.

The r208022 compact snapshots have these identities:

| Snapshot | SHA-256 |
| --- | --- |
| Server | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |
| Client | `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4` |

Both snapshots define the same 4 guard acts.

| Quest | Guard detail | NPC | Guard component | Exact interaction |
| --- | --- | --- | --- | --- |
| 1313, Walking Buddy | 95 | 3138, Caliope | Start 6547, act 12289 | AcceptNpc detail 1135 starts the quest from Caliope. |
| 1033, Memory and Iron Golems | 96 | 4617, Jurihan | Progress 5064, act 12779 | Talk detail 807, act 12778, selects Jurihan. |
| 1897, legacy Missing Gautama | 102 | 7548, Gautama | Progress 8802, act 13429 | Talk detail 816, act 13425, selects Gautama in component 8801. |
| 3656, In Hot Water | 116 | 9846, Satish | Progress 15273, act 20951 | Talk detail 920, act 20950, selects Satish. This talk permits team sharing. |

The quest acceptance path validates the NPC object and stores it in
`Character.CurrentTarget` before `StartQuest` activates its acts.
The talk event supplies the validated NPC object through `Transform.GameObject`.
These paths provide exact NPC instances. A template search does not provide ownership.
The new code does not select a nearby NPC or replace a bound NPC with another occurrence.

The data does not define a logout or restart rule.
The extracted r208022 quest UI does not add that rule.
This document does not claim retail confirmation for a custom continuity rule.

Quest 1033 has a separate content gap.
Its Start component 5062 names skill 13737, but neither compact contains that skill.
The tracked main-world spawns do not contain NPC 4617.
Spawn effect 397 can create that NPC, but its skill 11473 belongs to quest 809.
This change does not repair that content gap.

Quest 1897 has an explicit old-zone name and unfinished deployment note in client data.
The tracked main-world spawns do not contain NPC 7548.
It is not a suitable normal player test.

## Runtime behavior

A guard act registers its NPC template while the quest is active.
A Start guard binds the exact quest acceptor when that NPC matches the guard template.
A Progress guard binds the exact NPC from its matching talk objective.
An eligible shared talk binds the same NPC occurrence for each eligible quest owner.
Before that interaction, the guard does not select an unrelated NPC.

A dead or removed bound NPC fails only the owning active quest attempt.
The NPC removal hook covers direct deletion without an AI object.
It does not reuse the AI despawn event because that event can run despawn skills.

NPC callbacks record the failure and use the normal quest evaluation queue.
They do not acquire the quest persistence lock while a spawner can hold its own lock.
Quest evaluation checks the failure before component side effects and before step advancement.
This also prevents Start alternatives or score completion from overriding the guard failure.

Ready, Reward, Fail, Drop, and final quest cleanup remove the guard subscriptions.
The subscriptions continue across Start, Supply, and Progress steps.
The candidate removes subscriptions at disconnect, but the final continuity rule is still pending.
Do not release this candidate as a complete fix until that rule has tests and documentation.

## Automated checks

`QuestGuardTests` covers these paths:

- The real `QuestActObjTalk` handler binds the exact NPC occurrence.
- `CharacterQuests.AddQuestFromNpc` binds the acceptor before active quest registration.
- A different NPC with the same template does not fail or replace the guard.
- A guard does not bind the current target before the matching interaction.
- A Start guard continues through Progress.
- Direct `Npc.Delete` fails the quest without an AI object.
- NPC removal does not wait for the quest persistence lock.
- Terminal steps and final cleanup remove the subscriptions.
- A different world cannot supply the bound guard.
- A guard death does not fail another owner's separate quest attempt.

## Human checks after release

These checks need one player and a test character that can accept the named quest.
Use a GM account for controlled NPC death or removal when normal combat cannot produce the event.
Record the exact release and result in HUMAN VALIDATION #573.

- [ ] Accept Walking Buddy, quest 1313, from Caliope, NPC 3138.
  Keep the quest in Progress, then kill or remove that exact Caliope occurrence.
  Expect a failed quest in the journal.
  Main-world spawn coordinates are `22373.195, 12181.12, 258.00394`.
- [ ] Accept In Hot Water, quest 3656, from Traveler Baskara, NPC 2979.
  Talk to Satish, NPC 9846, then kill or remove that exact Satish occurrence before objective completion.
  Expect a failed quest in the journal.
  Satish's main-world spawn coordinates are `17052.35, 7723.44, 129.54886`.
- [ ] Repeat either test with another occurrence of the same NPC template.
  Kill the other occurrence and expect the active escort to remain in Progress.
- [ ] Complete the escort objectives and reach Ready before the bound NPC dies.
  Expect the quest to remain ready for its reward.
- [ ] Abandon an active escort, then remove its former guard.
  Expect no new quest failure or stale quest update.

Full authored NPC movement remains a separate mechanic.
Record a blocked objective separately if an NPC path or content gap prevents these checks.
Do not mark the guard check complete from service readiness or unit tests alone.

Candidate validation on 2026-10-06 passed a Release build with 0 errors.
The direct TUnit runner passed all 13 guard tests and all 278 tests selected by `Quest*`.
The build used the local .NET 10 SDK and cached NuGet packages.
No compact, MySQL, or client files changed.
