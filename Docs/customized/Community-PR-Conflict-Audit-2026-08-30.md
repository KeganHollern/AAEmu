# AAEmu Community PR Conflict Audit

**Snapshot date:** August 30, 2026  
**Upstream repository:** [AAEmu/AAEmu](https://github.com/AAEmu/AAEmu)  
**Upstream base branch:** `develop`  
**Local integration branch:** `deployment/r208022`  
**Target client:** ArcheAge 1.2 (`r208022`)

## Executive summary

The local `deployment/r208022` branch is not currently conflict-free with `upstream/develop` or with every open community pull request targeting `develop`.

At the time of this audit:

- The local branch was 125 commits ahead of and 4 commits behind `upstream/develop`.
- The common ancestor was `b34db3e5f5bce0b9b9bac2a6916a936f0e946a1f`.
- The local branch tip was `f31df6de`.
- The upstream `develop` tip was `41b2dcd9`.
- The working tree contained 12 modified tax, housing, and mail files. Those uncommitted changes were included in the merge simulations.
- Six open pull requests targeted `develop`; five other open pull requests targeted the separate `client_version/zone-10.0.2_r575` branch and were excluded from the primary integration assessment.

Two community PRs merge cleanly with the local branch, one requires a small manual reconciliation, two collide with gameplay systems already implemented locally, and one draft PR is not currently mergeable into upstream itself.

The largest risk is [PR #1545](https://github.com/AAEmu/AAEmu/pull/1545), which introduces a new floor/path architecture over the same NPC grounding, terrain sampling, pathfinding, and skill-landing systems already changed on `deployment/r208022`. It must be reconciled deliberately; it should not be accepted through an automatic conflict resolution.

## Scope and method

This audit used the current public PR heads and Git's three-way merge machinery. No merge was applied to the working tree.

The checks included:

1. Fetching the current `upstream/develop` tip and the open PR head commits.
2. Creating a temporary synthetic commit representing `HEAD` plus all tracked working-tree changes.
3. Simulating the merge of current `upstream/develop` into that synthetic local state.
4. Simulating each community PR individually against the local state.
5. Simulating each clean PR as if it had first been incorporated into the current upstream base.
6. Comparing changed-file sets for committed branch work and uncommitted working-tree work.
7. Testing pairwise interactions among the otherwise mergeable community PRs.
8. Inspecting overlapping implementations for semantic conflicts that a text merge alone cannot detect.

This document is a dated snapshot. PR heads, reviews, and mergeability can change, so the simulations must be repeated immediately before an actual upstream integration.

## Existing conflicts with current `upstream/develop`

Four upstream commits landed after the local fork point:

| Commit | Change | Local result |
| --- | --- | --- |
| `62e3eb1d` | Delete `LICENSE` ([PR #1540](https://github.com/AAEmu/AAEmu/pull/1540)) | Clean |
| `d06e67f1` | Run `ActiveRegionTick` asynchronously ([PR #1541](https://github.com/AAEmu/AAEmu/pull/1541)) | Clean |
| `dad07dbb` | Marine weather mechanics ([PR #1423](https://github.com/AAEmu/AAEmu/pull/1423)) | Four conflict files |
| `41b2dcd9` | Code-quality and consistency changes ([PR #1544](https://github.com/AAEmu/AAEmu/pull/1544)) | One conflict file |

The five baseline conflicts are:

| File | Source of conflict | Required resolution |
| --- | --- | --- |
| `AAEmu.Game/Core/Managers/DuelManager.cs` | Upstream narrows faction helpers from `Unit` to `Character`; local work changes duel countdown, faction timing, achievement credit, and hostile-effect cleanup. | Keep the local duel behavior while accepting the safe `Character` typing and any applicable cleanup. |
| `AAEmu.Game/Core/Packets/C2G/CSStartSkillPacket.cs` | Upstream adds marine-weather skill blocking and centralizes skill-failure packets; local work adds combo authorization and restrictions on unlearned skills. | Preserve both security gates and the storm restriction. There must be one consistent skill-failure path. |
| `AAEmu.Game/Models/Game/DoodadObj/Doodad.cs` | Upstream prevents broadcasts after a doodad is deleted; local work already expands deletion safety, phase walking, time-of-day handling, and area-trigger notifications. | Retain the local lifecycle implementation, including its `_deleted` guard. Do not duplicate the upstream guard. |
| `AAEmu.Game/Models/Game/DoodadObj/Funcs/DoodadFuncRatioRespawn.cs` | Both sides independently implement the same spawner-based doodad template replacement. | Keep one implementation. The two versions are functionally close and should not both run. |
| `Docs/WorldConfig_en.md` | Local world-clock and tax documentation overlaps the upstream marine-weather section. | Preserve all sections and normalize the final ordering and line endings. |

These conflicts must be resolved before attributing later failures to an open PR.

## Open `develop` PR assessment

| PR | Upstream status at snapshot | Changed files | Overlap with local committed work | Overlap with current working tree | Direct merge result against local work | Assessment |
| --- | --- | ---: | ---: | ---: | --- | --- |
| [#1545 – Floor/Path split](https://github.com/AAEmu/AAEmu/pull/1545) | Mergeable upstream; changes requested | 55 | 26 | 5 | 21 conflict files | High-risk architectural collision |
| [#1494 – glibc Docker runtime](https://github.com/AAEmu/AAEmu/pull/1494) | Mergeable but reported unstable; changes requested | 1 | 1 | 0 | Clean | Low risk |
| [#1488 – Skip null loot items](https://github.com/AAEmu/AAEmu/pull/1488) | Mergeable upstream; changes requested | 1 | 1 | 0 | Clean | Low risk |
| [#1483 – Guard missing skill templates](https://github.com/AAEmu/AAEmu/pull/1483) | Mergeable upstream; changes requested | 13 | 7 | 0 | 2 conflict files | Moderate, straightforward reconciliation |
| [#1447 – Halcyona War](https://github.com/AAEmu/AAEmu/pull/1447) | Draft; conflicts with upstream | 33 | 16 | 2 | 8 direct conflict files; 2 additional base conflicts | High risk, not ready for integration |
| [#1424 – Generic gimmick projectile](https://github.com/AAEmu/AAEmu/pull/1424) | Mergeable upstream; changes requested | 8 | 4 | 0 | 2 conflict files | High semantic risk despite small text-conflict count |

### PR #1494: glibc Docker runtime

This PR changes the Game runtime image from Alpine/musl to the standard .NET glibc image and replaces `apk` package installation with `apt`.

The local branch only adds port `1281` to the same Dockerfile. Git merges the changes cleanly, and the resulting intent is compatible: retain the glibc runtime change and keep ports `1239`, `1250`, and `1281` exposed.

**Disposition:** safe to integrate after the upstream baseline is resolved.

### PR #1488: skip null loot items

This PR skips an item when `ItemManager.Create` returns `null` for a missing template. The local branch changes the same file to increment loot achievements after a successful grant, but it changes a different region.

The changes merge cleanly and are semantically complementary.

**Disposition:** safe to integrate after the upstream baseline is resolved.

### PR #1483: missing skill-template guards

Direct conflicts occur in:

- `AAEmu.Game/Core/Packets/C2G/CSStartSkillPacket.cs`
- `AAEmu.Game/Models/Game/AI/v2/Behaviors/Common/SpawningBehavior.cs`

The PR centralizes skill-template lookup and adds `null` guards across player skills, NPC spawn skills, buffs, gimmicks, crafting, simulation, and unit skill helpers.

The resolution must preserve local behavior in `CSStartSkillPacket.cs`:

- combo-follow-up authorization;
- rejection of unauthorized unlearned skills;
- combo-state clearing for normal or variant learned skills;
- correct failure packets instead of a silent or unsafe cast.

The `SpawningBehavior.cs` conflict is mechanical: retain local tower-defense/spawn behavior and skip only a missing skill template.

**Disposition:** integrate manually; low implementation difficulty but security-sensitive.

### PR #1424: generic gimmick projectile runtime

Direct conflicts occur in:

- `AAEmu.Game/Models/Game/Gimmicks/Gimmick.cs`
- `AAEmu.Game/Models/Game/Gimmicks/GimmickSpawner.cs`

The PR adds a broad projectile handler with:

- horizontal and vertical velocity;
- gravity and air resistance;
- terrain and water impact detection;
- ship hull collision;
- collision-skill execution;
- caster fallback and range-zero handling.

The local branch already implements `GimmickMovementFreeFall`, target-relative spawning, landing-plane selection, collision handling, and client velocity correction for Sharpwind Mines and related content.

The following local guarantees must be preserved:

- a delayed `skill_delay` fuse is not preempted by an early ground collision;
- `CollisionUnitOnly` gimmicks do not detonate on terrain;
- fade-out duration is honored for `DisappearByCollision`;
- offsets are anchored to the correct source or target;
- local-axis initial velocity is rotated by the caster's yaw;
- a valid ground height of `0` remains distinguishable from a missing surface;
- only one movement handler integrates a gimmick on each tick.

The preferred resolution is to evolve the local movement abstraction into one consolidated projectile handler, porting the PR's ship, water, horizontal-motion, and air-resistance support while retaining the local timing and content rules.

**Disposition:** architectural merge required; do not keep both physics implementations.

### PR #1545: Floor/Path split

This is the most consequential conflict. The PR adds `FloorQuery`, `FloorResolver`, `NavSurfaceSampler`, `PathLocomotionZ`, new floor policies, diagnostic commands, and 510 new lines of floor/path tests.

The local branch already contains a competing surface architecture built around:

- `AiGeodataManager.TryGetGroundSurface`;
- `GroundSurfaceResult` and explicit source/decision/failure information;
- terrain interpolation matching the rendered client surface;
- distinction between valid sea-level height `0` and an unavailable surface;
- BAI node-type policy that preserves authored navigation height where appropriate;
- NPC spawn grounding and authored-Z preservation;
- NPC skill-controller and forced-movement grounding;
- indoor and obstacle compatibility behavior;
- GM surface diagnostics and extensive ground-height tests.

The PR produces 21 textual conflicts against the local branch. Five are already part of the upstream baseline; the 16 additional conflict files are:

- `AAEmu.Game/Core/Managers/World/WorldManager.cs`
- `AAEmu.Game/Models/Game/AI/v2/Behaviors/BaseCombatBehavior.cs`
- `AAEmu.Game/Models/Game/NPChar/Npc.cs`
- `AAEmu.Game/Models/Game/NPChar/NpcSpawnerNpc.cs`
- `AAEmu.Game/Models/Game/Skills/Effects/ImpulseEffect.cs`
- `AAEmu.Game/Models/Game/Skills/Effects/PhysicalExplosionEffect.cs`
- `AAEmu.Game/Models/Game/Skills/Effects/SpecialEffects/Blink.cs`
- `AAEmu.Game/Models/Game/Skills/Effects/SpecialEffects/KnockBack.cs`
- `AAEmu.Game/Models/Game/Skills/Effects/SpecialEffects/TeleportToUnit.cs`
- `AAEmu.Game/Models/Game/Skills/SkillControllers/DashSkillController.cs`
- `AAEmu.Game/Models/Game/Skills/SkillControllers/FloatingSkillController.cs`
- `AAEmu.Game/Models/Game/Skills/SkillControllers/LeapSkillController.cs`
- `AAEmu.Game/Models/Game/Skills/SkillControllers/WanderingSkillController.cs`
- `AAEmu.Game/Models/Game/World/WorldTemplate.cs`
- `AAEmu.Game/Scripts/Commands/TestNavMesh.cs`
- `AAEmu.Game/Utils/Scripts/SubCommands/AStar/AStarPathFindingSubCommand.cs`

Five current working-tree files also overlap the PR:

- `AAEmu.Game/Configurations/World.json`
- `AAEmu.Game/Core/Managers/HousingManager.cs`
- `AAEmu.Game/Models/Game/Configurations.cs`
- `Docs/WorldConfig_en.md`
- `Docs/WorldConfig_ru.md`

Most of the active tax/prepayment changes are in different sections and auto-merge. The principal collision is with the previously committed NPC and world-surface work.

#### Critical semantic incompatibility

The PR currently represents unavailable terrain and navigation samples with a numeric `0` sentinel. Its resolver therefore uses checks such as `terrainZ != 0f`. The local branch intentionally removed that ambiguity: sea-level terrain and BAI surfaces at exactly `0` are valid and covered by tests.

Accepting the PR's sentinel behavior unchanged would regress sea-level and coastline grounding. Any integration must use a success flag, nullable candidate, or equivalent result type rather than treating `0` as missing.

#### Recommended architecture

Use one public floor-query entry point, but keep explicit success and diagnostic results. A suitable reconciliation would:

1. Retain the local `TryGetGroundSurface`/`GroundSurfaceResult` contract or adapt it behind a new `FloorQuery` facade.
2. Port the PR's bounded Z-hint candidate selection for caves and multi-level spaces.
3. Port nav-edge interpolation for path waypoint Z without replacing A* XY selection.
4. Preserve the local node-type policy for outdoor triangular nodes, authored waypoint nodes, obstacle vertices, and terrain-unavailable fallbacks.
5. Preserve valid zero-height surfaces throughout the API.
6. Keep a single set of GM diagnostics that reports provider, decision, failure, BAI reference, terrain value, and nav value.
7. Combine both test suites before changing call sites.

**Disposition:** integrate only through a dedicated reconciliation branch with focused tests and play validation.

### PR #1447: Halcyona War

This PR is a draft and currently conflicts with `upstream/develop` in:

- `AAEmu.Game/Configurations/World.json`
- `AAEmu.Game/Models/Game/Configurations.cs`

It also directly conflicts with the local branch in:

- `AAEmu.Game/Core/Managers/World/SpawnManager.cs`
- `AAEmu.Game/GameData/TowerDefGameData.cs`
- `AAEmu.Game/Models/Game/AI/v2/Behaviors/Common/FollowPathBehavior.cs`
- `AAEmu.Game/Models/Game/AI/v2/Behaviors/Common/RunCommandSetBehavior.cs`
- `AAEmu.Game/Models/Game/AI/v2/Controls/AiPathHandler.cs`
- `AAEmu.Game/Models/Game/Skills/Effects/NpcControlEffect.cs`
- `AAEmu.Game/NLog.config`
- `AAEmu.Game/Scripts/Commands/TowerDef.cs`

The local branch already has a reusable rift tower-defense runtime and substantial path/command-set behavior for Sharpwind and other scripted content. Merging the PR's separate `TowerDefManager` wholesale would duplicate scheduling and event ownership.

The preferred approach is to extract Halcyona-specific content, cannon behavior, paths, spawns, relic rules, and zone-conflict behavior and implement them as a scenario on the local reusable tower-defense runtime.

**Disposition:** wait for the PR to become mergeable and leave draft status, then port its content rather than introducing a second tower-defense framework.

## Pairwise community PR conflicts

Among PRs that individually merge into current `upstream/develop`, every tested pair merged cleanly except:

| PR pair | Conflict | Resolution |
| --- | --- | --- |
| [#1483](https://github.com/AAEmu/AAEmu/pull/1483) + [#1424](https://github.com/AAEmu/AAEmu/pull/1424) | `AAEmu.Game/Models/Game/Gimmicks/Gimmick.cs` | Combine #1483's early missing-template guard with #1424's original-caster fallback and public collision trigger. |

PR #1447 was excluded from the clean pairwise sequence because it already conflicts with current `upstream/develop`.

## Recommended integration sequence

1. **Stabilize local work.** Commit or otherwise preserve the current tax, housing, and mail changes so the integration has a reviewable starting point.
2. **Merge current `upstream/develop`.** Resolve the five baseline conflicts and run the complete build and test suite.
3. **Integrate PR #1494 and PR #1488.** These are clean, isolated changes.
4. **Integrate PR #1483 manually.** Preserve the local skill authorization rules while adding all missing-template guards.
5. **Reconcile PR #1424.** Consolidate gimmick physics into one handler and retain local fuse/collision semantics.
6. **Create a dedicated PR #1545 reconciliation branch.** Merge the floor-query designs at the API and test level before converting callers.
7. **Defer PR #1447.** Reassess after upstream resolves its base conflicts and the PR is no longer a draft.

This order minimizes simultaneous conflicts and produces a testable checkpoint after each behavioral subsystem.

## Required verification

After each integration checkpoint:

```powershell
dotnet build
dotnet test
```

At minimum, explicitly run or review the following focused suites after the relevant merges:

- `CSStartSkillPacketTests`
- `WorldTemplateTests`
- `WorldManagerHeightTests`
- `BaseCombatBehaviorGroundHeightTests`
- `NpcEffectGroundingTests`
- `SkillControllerGroundHeightTests`
- `NpcSpawnerNpcTests`
- `GimmickPhysicsTests`
- `TowerDefGameDataTests`
- `TowerDefenseScheduleTests`
- `HousingManagerTests`
- `MailTests`

Manual validation should include:

- an outdoor NPC on triangular BAI data does not float;
- an authored indoor/cave NPC remains on the intended vertical layer;
- a valid ground surface at Z `0` resolves successfully;
- NPC chase/path movement uses stable waypoint Z without oscillation;
- forced movement, knockback, blink, leap, and floating controllers land correctly;
- delayed gimmick fuses are not triggered early by ground contact;
- gimmick projectiles collide correctly with terrain, water, and ships;
- duel countdown and faction changes occur in the intended order;
- marine-weather restrictions return the correct skill failure to the client;
- loot with a missing item template is skipped without preventing valid loot or achievements;
- tax prepayment and mail reconciliation remain serialized and duplicate-free.

## Decision record

The branch should not claim compatibility with future community merges until the current five baseline conflicts are resolved. PRs #1545, #1424, and #1447 affect systems with substantial local implementations; for those PRs, a clean textual merge is not sufficient evidence of behavioral compatibility.

The integration policy is therefore:

- accept isolated fixes directly after verification;
- manually combine overlapping safety checks;
- consolidate duplicate runtime systems rather than running both;
- preserve r208022-specific behavior and valid-zero surface semantics;
- rerun this audit against the final PR heads immediately before incorporation.
