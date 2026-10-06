# Escort guard lifecycle, issue 272

This change adds the missing runtime guard check for
[aaemu-cluster#272](https://github.com/KeganHollern/aaemu-cluster/issues/272).
It does not change NPC paths, quest objectives, compact data, or packets.
The server uses the logout and restart rule that the user approved on 2026-10-06.

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
The approved server rule fails an active, unfinished escort after logout or Game restart.
An unstarted escort remains unstarted. A completed escort remains complete.
This is a custom server rule, not a confirmed retail rule.

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
A compare-exchange operation orders guard failure and completion.
If removal wins that operation, the Ready or Reward transition fails.
If completion wins, a later NPC callback cannot change the result.
Report events and normal quest advancement use this same decision.

Ready, Reward, Fail, Drop, and final quest cleanup remove the guard subscriptions.
The subscriptions continue across Start, Supply, and Progress steps.
The disconnect path checks escort completion before it removes the character from the world.
It runs only the bound escort's normal evaluation, so all authored conditions apply.
It then fails an unfinished escort and removes the subscriptions.
It does not drain the global quest queue under the persistence lock.
Captured Talk and Sphere callbacks cannot change a completed, failed, or disconnected guard attempt.
Other quests keep their normal event behavior.

## Persistence and restart

The change uses the current quest row and serialized step, acceptor, and objective fields.
It adds no schema or compact change.

A Start guard is active after acceptance from its matching NPC template.
A Progress guard is active after its matching Talk objective has positive progress.
The classifier checks all Progress components, including separate Talk and guard components in quest 1897.
It never selects a replacement NPC after a reload.

Exact Talk binding, objective progress, and the first checkpoint share the persistence lock.
A bound escort also evaluates its normal conditions before a Talk or Sphere event returns.
This includes quest 1033's live `QuestActCheckSphere` condition.
When completion succeeds, the server saves the actual Ready or Reward step with Ready status.
Quest 1897 has no Ready component, so its checkpoint uses Reward.
A transient step without a component is not a reload checkpoint.
Guard failure also writes through the current quest row.
Every write checks that the active quest collection still owns that exact attempt.
It cannot recreate a completed or abandoned quest row after removal.

The logout hook runs before world cleanup changes the character's conditions.
An unfinished guard fails synchronously before the normal logout save.
Both socket disconnect and return to character selection use this hook.
`EnterWorldManager.LeaveWorldCore` calls it before timers, owned units, or the character leave the world.
`GameNetwork.Stop` drains `DisconnectWhenIdle`, which uses the normal socket disconnect hook, before the final shutdown checkpoint.
After an abrupt stop, `AddLoadedQuest` registers the saved quest before the guard classifier runs.
An active saved Start, Supply, or Progress escort fails before its acts activate.
Saved Ready, Reward, Fail, and Drop states do not change.
A fresh main-quest restart does not use the interrupted-session classifier.
All 4 authored guard quests have `restart_on_fail=false`, so their retry procedure is abandon and accept again.

The completion boundary is the saved Ready or Reward step.
A pre-fix Progress row counts as active even if some or all objective counters are complete.
That row cannot prove the former live CheckSphere condition or original NPC identity.
The change does not reconstruct that missing state.

The checkpoints use the current `FlushQuest` failure convention.
A failed database write logs a warning, and the periodic or logout save can retry the state.
This change does not add a general transaction or recovery system for quest persistence.

## Automated checks

`QuestGuardTests` covers these paths:

- The real `QuestActObjTalk` handler binds the exact NPC occurrence.
- `CharacterQuests.AddQuestFromNpc` binds the acceptor before active quest registration.
- A different NPC with the same template does not fail or replace the guard.
- A guard does not bind the current target before the matching interaction.
- A Start guard continues through Progress.
- Direct `Npc.Delete` fails the quest without an AI object.
- NPC removal does not wait for the quest persistence lock.
- Removal after the last guard check still blocks the Ready transition.
- Removal during old-step cleanup cannot reverse a completed guard decision.
- A direct Ready request cannot override a pending or processed guard failure.
- Terminal steps and final cleanup remove the subscriptions.
- A different world cannot supply the bound guard.
- A guard death does not fail another owner's separate quest attempt.
- Real Talk and Sphere events checkpoint active and completed escort state.
- Reload fails an active saved escort without binding another NPC.
- Reload and disconnect keep an unstarted escort unchanged.
- Logout evaluates pending completion before it fails an unfinished escort.
- Ready, Reward, Fail, and Drop states survive the reload boundary.
- A legacy Progress row does not invent a completed live condition.
- Captured Talk and Sphere exit callbacks cannot change a closed guard attempt.

`QuestGuardPersistenceTests` uses an isolated MySQL database.
It checks the saved rows after Talk, guard death, logout, normal completion, and completion without a Ready component.
It also checks unstarted reloads, Start-acceptor reloads, pending completion, and a captured Talk callback after disconnect.

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
- [ ] Start an escort, then return to character selection before its objectives finish.
  Select the same character and expect a failed quest in the journal.
  Abandon the failed quest and accept it again to retry.
- [ ] Start another escort, then close the game before its objectives finish.
  Reconnect and expect a failed quest in the journal.
- [ ] Accept In Hot Water without its required Talk to Satish, then return to character selection.
  Select the same character again.
  Expect the Talk objective to remain unstarted, with no new failure.
- [ ] Complete the escort objectives, then return to character selection before reward collection.
  Select the same character again.
  Expect the quest to remain ready for its reward.
- [ ] Repeat the active, unstarted, and completed cases across an operator-controlled Game restart.
  Expect the same results as logout.
  This check needs an agreed restart interval and access to the current server.

Full authored NPC movement remains a separate mechanic.
Record a blocked objective separately if an NPC path or content gap prevents these checks.
Do not mark the guard check complete from service readiness or unit tests alone.

The candidate uses the local .NET 10 SDK and cached NuGet packages.
The Release build passed with 0 errors and 90 warnings.
All 374 quest unit tests passed, including 33 guard cases.
All 9 MySQL guard persistence cases passed.
The socket, character-selection, and graceful shutdown routes also passed a source call-path review.
No SQL schema, compact, or client files changed.
The persistence tests write only to an isolated test database.

The completion race test failed against candidate `f537f6ff3` with the expected incorrect successful completion.
It passes with the atomic guard decision.
