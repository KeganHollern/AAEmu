# Tower-Defense Rifts Implementation Plan

- Status: Implemented behind disabled feature flags; in-game validation pending
- Audience: AAEmu game-server developers, content designers, QA, and release reviewers
- Target client: ArcheAge 1.2 (`r208022`)
- Initial events: Crimson Rift and Grimghast Rift, including Grimghast advance notices
- Last updated: August 30, 2026

See [Tower-Defense Rifts Flowchart and Test Runbook](Tower-Defense-Rifts-Flowchart.md) for the implemented control flow, recovery branches, and GM test sequence.

## Outcome

Build one server-authoritative, data-driven tower-defense event system that correctly runs Crimson Rift and Grimghast Rift and can host additional custom events without adding event-specific control flow to the game server.

An event occurrence must own its complete lifecycle: schedule, selected site, initial target, wave state, objectives, spawned objects, timers, client presentation, persistence, and cleanup. No rift controller or wave NPC may exist as an ordinary ambient world spawn.

## Confirmed current failures

The existing code and data are not a partial implementation that needs a small repair. The orchestration layer is missing, while several current workarounds actively produce bad behavior.

1. `GameData/TowerDefGameData.cs` only loads compact rows. Its `PostLoad()` is empty, there is no runtime manager, and nothing schedules, starts, advances, completes, or cleans up an event.
2. The loader uses `return` when a child row references a missing parent. The server compact contains 20 orphan progress rows; the first encountered orphan can stop loading before the Crimson and Grimghast rows are assembled.
3. `Scripts/Commands/TowerDef.cs` only sends presentation packets to one player. It does not start server gameplay, and its `list` operation is unimplemented.
4. Crimson and Grimghast controllers are ordinary unpinned entries in `Data/Worlds/main_world/npc_spawns.json`. `SpawnManager` treats those entries as always-active world content, so approaching a site can independently trigger several event stages.
5. Grimghast wave skills reference placement/group IDs `109220`, `109221`, `109223`, `111326`, `111327`, and `111328`. None of those IDs has a placement in the current main-world spawn data, so `NpcSpawnerSpawnEffect` resolves no wave formation.
6. NPC on-spawn skills are invoked both by `SpawningBehavior` and `NpcEvents.OnSpawn`. Grimghast's zero-cooldown controller skills can therefore run twice.
7. NPC spawn effects load `DespawnOnCreatorDeath`, but the NPC paths do not implement the relationship. Effects with zero lifetime can leave children alive indefinitely.
8. The four tower-defense presentation packets exist, but gameplay never sends them as lifecycle events and reconnecting or zone-changing players receive no active-event snapshot.

The source and main-world spawn data in `AAEmu` and the deployed fork under `AAEmu-cluster/k8s/vendor/AAEmu` currently match, so this plan applies to both. Deployment remains a separate release action.

## Scope

This plan includes:

- A reusable runtime for scheduled, manual, zone-state, and dependency-triggered tower-defense events.
- Correct loading and validation of the retail `tower_defs` graph.
- Deterministic occurrence and site selection.
- Wave actions, timed and kill objectives, success, timeout, cancellation, restart recovery, and cleanup.
- Event-owned NPC and doodad spawning with ownership propagation through spawn effects.
- Correct client start, wave, end, list, reconnect, and zone-entry synchronization.
- Exact migration rules for Crimson and Grimghast data.
- Custom event authoring through the canonical `Content/projects/custom` project.
- Tests, diagnostics, feature flags, staged rollout, and rollback.

This plan does not include:

- Guessing missing Grimghast formations and calling the result retail-accurate.
- Replacing the existing quest, loot, or NPC AI systems. Event NPCs continue to use them.
- Enabling every unrelated retail `tower_defs` row in the first release.
- Deploying to the cluster as part of implementation.

## Design rules

1. The game server is authoritative. Client packets describe server state; they do not create it.
2. One occurrence chooses one site once. Every initial target, wave action, objective, and packet uses that site until the occurrence ends.
3. Only objects tagged with the occurrence can satisfy that occurrence's objectives.
4. All event placements start inactive and remain invisible until an occurrence activates or force-spawns them.
5. Every delayed callback is cancellable, generation-checked, and safe to run more than once.
6. Cleanup is idempotent and runs for success, timeout, manual stop, validation failure, world disposal, server shutdown, and recovery.
7. Invalid definitions fail closed per event. One broken or unsupported event must not truncate other data or stop the server.
8. Compact rows supply retail presentation and wave definitions. A versioned runtime manifest supplies information absent from the compact, such as world, zone group, sites, trigger policy, restart policy, and concurrency.
9. New custom content has one canonical source under `Content/projects/custom`; server and client compact artifacts are compiled separately for their different schemas.

## Target architecture

```text
compact.sqlite3 rows -----> TowerDefGameData -----+
                                                   |
runtime event manifests --> definition compiler ---+--> immutable validated definitions
                                                             |
world clock / zone state / GM command ----------------------> scheduler
                                                             |
                                                             v
                                                  occurrence state machine
                                                   |       |        |
                                                   v       v        v
                                             spawn service objectives client sync
                                                   |       |        |
                                                   +-------+--------+
                                                           |
                                                           v
                                              persistence + diagnostics
```

The compact is static reference data. The manager owns mutable runtime state. MySQL stores only the small recovery and idempotency ledger needed to survive a restart.

## File-level implementation map

The exact class split may be adjusted to match neighboring code during implementation, but the responsibilities and integration points should remain separate.

| Area | Primary files to add or change |
| --- | --- |
| Static compact graph | `AAEmu.Game/GameData/TowerDefGameData.cs`; immutable models under `AAEmu.Game/Models/Game/TowerDefs/` |
| Runtime contracts | `AAEmu.Game/Core/Managers/TowerDefense/ITowerDefenseManager.cs`; definition, validation, occurrence, state, action, objective, and lease contracts in the same feature folder |
| Runtime orchestration | `AAEmu.Game/Core/Managers/TowerDefense/TowerDefenseManager.cs`; scheduler, state-machine, spawn-service, client-sync, and persistence collaborators |
| Dependency injection/lifecycle | `AAEmu.Game/Program.cs`; `AAEmu.Game/GameService.cs` only if orderly shutdown cannot be expressed through the existing manager lifecycle |
| World clock | `AAEmu.Game/Core/Managers/ITimeManager.cs`; `AAEmu.Game/Core/Managers/TimeManager.cs`; new typed clock sample/observer model |
| World placement and ownership | `AAEmu.Game/Core/Managers/World/SpawnManager.cs`; `AAEmu.Game/Models/Game/NPChar/NpcSpawner.cs`; `NpcSpawnerNpc.cs`; a per-world ownership registry disposed from `WorldInstance.CleanupInstance()` |
| Spawn effects | `AAEmu.Game/Models/Game/Skills/Effects/NpcSpawnerSpawnEffect.cs`; `SpawnEffect.cs`; creator-child cleanup integration |
| NPC on-spawn fix | `AAEmu.Game/Models/Game/AI/v2/Behaviors/Common/SpawningBehavior.cs`; retain the canonical path in `NpcEvents.cs` |
| Death objectives | Existing `WorldEvents.OnUnitKilled` subscription plus the tower-defense objective router; no second NPC death event should be invented |
| Client synchronization | Existing `SCTowerDef*` packets; hooks from `CSInstanceLoadedPacket`, character world spawn, and `Character.OnZoneChange`; a packet adapter inside the feature |
| GM operations | Replace `AAEmu.Game/Scripts/Commands/TowerDef.cs` behavior with manager calls |
| Retail runtime data | `AAEmu.Game/Data/TowerDefense/retail-rifts.json` plus its schema; event metadata in `Data/Worlds/main_world/npc_spawns.json` |
| Mutable state | `SQL/aaemu_game.sql`; dated `SQL/updates/YYYY-MM-DD_aaemu_game_tower_def_occurrences.sql`; a small repository owned by the manager |
| Configuration | `AAEmu.Game/Models/AppConfiguration.cs` and the appropriate file under `AAEmu.Game/Configurations/` |
| Unit/integration tests | Mirrored feature folders under `AAEmu.UnitTests`; real-data/runtime scenarios under `AAEmu.IntegrationTests` |
| Custom authoring | Tower-defense schemas/compiler/validation in the existing Content Studio projects and source manifests under `Content/projects/custom/` |

Scheduled task objects should contain only an occurrence key and expected state generation, then call back into `ITowerDefenseManager`. They must not retain world objects or implement event logic themselves.

## Static definition model

### Repair `TowerDefGameData`

Keep the existing protocol name `TowerDef`, but make its loaded graph immutable after `PostLoad()`.

The loader must:

- Use explicit column lists and `ORDER BY id` for all four tables.
- Replace missing-parent `return` statements with recorded validation errors plus `continue`.
- Preserve nullable compact values instead of converting all missing values to zero.
- Load `name`, `start_msg`, `end_msg`, `title_msg`, `milestone_id`, and progress `msg` for logging, diagnostics, and test comparison. Client localization still comes from the client compact.
- Parse `spawn_target_type` and `kill_target_type` into known discriminated types. The current data requires `NpcSpawner`, `Npc`, and `DoodadAlmighty` support.
- Assign a zero-based `StepOrdinal` from the ordered progress rows; do not use sparse progress-row IDs as client steps.
- Expose read-only `Get(id)`, `GetAll()`, and `ValidationReport` APIs.
- Build indexes for spawn targets and objectives during `PostLoad()`.
- Reject duplicate IDs and malformed values for the affected definition.
- Log orphan child rows with their table, child ID, and missing parent ID without disabling unrelated definitions.

An enabled event is usable only when its compact graph and runtime manifest both validate. Definitions that are present but not enabled remain queryable by diagnostics and GM validation.

### Formal progress semantics

Each progress row is translated to a step with entry actions and completion criteria:

- A timer criterion exists only when `cond_to_next_time > 0`.
- Each kill-target row is a separate criterion.
- When `cond_comp_by_and` is true, every active criterion must complete.
- When it is false, any active criterion completes the step. This supports events that end when either a target dies or a timeout expires.
- A step with no criterion is invalid unless the runtime manifest explicitly marks it as an immediate transition.
- Kill counts are monotonic within a step and are capped at the required count.
- All target rows with `Npc` type match the NPC template but count only event-owned NPCs from the current occurrence.
- `despawn_on_next_step` removes the objects leased by that spawn action before the next step begins.

`first_wave_after` is a real-time delay between event start and entry into the first progress step. `force_end_time` is a hard real-time deadline measured from event start. The scheduled `tod` and `tod_day_interval` use the world clock.

Root-level `kill_npc_id` and `kill_npc_count` become an optional occurrence-level terminal objective only when both values are valid and nonzero. Odd legacy combinations are reported and ignored unless an event manifest explicitly resolves their meaning.

## Runtime manifest

Create `AAEmu.Game/Data/TowerDefense/retail-rifts.json` for the retail rift overlays. It adds orchestration metadata without copying the compact's full step graph.

The contract should be JSON-schema validated and conceptually contain:

```json
{
  "schemaVersion": 1,
  "events": [
    {
      "key": "rift.crimson.cinderstone",
      "towerDefId": 3,
      "enabled": false,
      "worldTemplate": "main_world",
      "zoneGroupId": 20,
      "trigger": {
        "type": "TimeOfDay",
        "hour": 12.0,
        "dayInterval": 1,
        "dayPhase": 0,
        "catchUpGraceSeconds": 30
      },
      "siteSelection": {
        "type": "DeterministicUniform"
      },
      "concurrencyGroup": "rift.crimson.cinderstone",
      "restartPolicy": "RestartCurrentStep",
      "sites": [
        {
          "key": "cinderstone-1",
          "spotId": 1,
          "eventZoneId": 0,
          "anchor": { "x": 15480.82, "y": 11883.66, "z": 200.98602 },
          "bindings": {
            "initial": ["cr-cinderstone-1-initial"],
            "9848": ["cr-cinderstone-1-step-1"],
            "9849": ["cr-cinderstone-1-step-2"],
            "9865": ["cr-cinderstone-1-step-3"],
            "9866": ["cr-cinderstone-1-step-4"]
          }
        }
      ],
      "cleanup": {
        "despawnAllOwnedObjects": true,
        "deactivateAllPlacements": true
      }
    }
  ]
}
```

The displayed `eventZoneId` is deliberately a validation placeholder, not an instruction to ship zero. At load time, the manager resolves each anchor through `IWorldManager.GetZoneId()` and `IZoneManager`, verifies the configured zone group, and records the actual zone key. Publishing fails if resolution disagrees with the manifest.

Manifest requirements:

- `key` is stable and unique across retail and custom events.
- `towerDefId` must exist in the appropriate server and client target artifacts.
- A site has a stable `spotId`, anchor, resolved zone key, and bindings from compact spawn target IDs to world placements.
- Site choice is seeded from the occurrence key, so a restart selects the same site.
- Trigger types are `TimeOfDay`, `ZoneState`, `DependencyCompleted`, and `Manual`. New trigger handlers are registered explicitly rather than instantiated by arbitrary type names.
- Concurrency is explicit: one occurrence per event key, optional mutual-exclusion group, and optional maximum active occurrences per zone group.
- Restart and cleanup policies are explicit.
- Optional completion actions use registered handlers and a persisted idempotency key. Normal NPC loot and quest hooks remain the default reward path.

## Stable world placements

Extend the NPC world-spawn JSON model with optional event metadata:

```json
{
  "UnitId": 8828,
  "NpcSpawnerIds": [9846],
  "StartInactive": true,
  "EventPlacementId": "cr-cinderstone-1-initial",
  "EventSiteKey": "cinderstone-1",
  "Position": {
    "X": 15480.82,
    "Y": 11883.66,
    "Z": 200.98602
  }
}
```

Rules for event placements:

- `EventPlacementId` is unique within a world template.
- `NpcSpawnerIds` binds the placement to the compact target or skill-effect group ID.
- `StartInactive` must be true.
- Event placements live only in the pinned/event spawner registry, never in the normal always-tick registry.
- Several placement entries may share one `NpcSpawnerIds` value to form a group.
- Each member of a formation has its own placement ID, position, rotation, optional path, and NPC template. Do not rely on the currently unused `NpcSpawner.Count` field to create a formation.
- Spawn resolution is by the selected site's explicit placement IDs. It must never activate every placement with the same template ID across all sites.

Add `SpawnManager` indexes for event placement ID, site key, and spawner-template/group ID. Indexes are built when a world instance loads and validated for uniqueness.

## World clock and occurrence identity

The current `ITimeManager` exposes only a display hour, which is insufficient for day intervals and idempotent crossings. Add a read-only world-clock sample containing:

- Display hour in `[0, 24)`.
- Monotonic source hours.
- World day ordinal.
- Previous and current source-hour boundaries for crossing evaluation.

Keep the existing float observer for client time updates. Add a typed subscription for server systems.

Scheduler behavior:

1. Detect a scheduled hour by crossing, not by float equality.
2. Calculate `occurrenceKey = eventKey + worldInstanceId + scheduledWorldDayOrdinal`.
3. Apply `dayInterval` and explicit `dayPhase`.
4. Ignore a repeated or backward clock window already below the monotonic high-water mark.
5. On startup, restore a persisted active occurrence or start a just-missed occurrence only inside its configured catch-up grace.
6. A manual time change follows the same crossing and idempotency rules; GM commands can bypass the schedule only through an explicit manual start.

Crimson and Grimghast use `dayInterval = 1`. The explicit phase is still required for future multi-day custom schedules.

## Runtime state machine

Create `ITowerDefenseManager` and `TowerDefenseManager` under `Core/Managers/TowerDefense/`, register both in `Program.cs`, and use constructor injection. The manager implements `ILoadable` for manifests and recovery state and `IInitializable` for clock/world subscriptions.

One `TowerDefenseOccurrence` exists per occurrence key and moves through:

```text
Scheduled -> Starting -> FirstWaveDelay -> StepActive -> StepTransition
                                  ^              |              |
                                  |              +--------------+
                                  |
                                  +-- repeated for each step

Any active state -> Succeeded | TimedOut | Failed | Cancelled -> Cleaning -> Ended
```

All state mutation is serialized per world instance. Clock ticks, task callbacks, deaths, world disposal, and GM commands enqueue transitions; they do not mutate occurrence state independently. Every callback carries the occurrence key and state generation, so a late timer from an earlier step is harmless.

### Start sequence

1. Reserve the occurrence key and concurrency group atomically.
2. Resolve and preflight every placement needed by the selected site.
3. Persist `Starting`, selected site, definition hash, schedule time, and hard deadline.
4. Spawn the compact `target_npc_spawner_id` through the site's `initial` binding when one is configured.
5. Create the initial spawn leases and identify the anchor target object.
6. Broadcast `SCTowerDefStartPacket` to relevant players.
7. Enter `FirstWaveDelay`; after `first_wave_after`, enter the first step.

If preflight or initial spawn fails, clean up and end as `Failed`; do not announce a playable event.

### Step entry and completion

On step entry:

1. Increment state generation and persist the step ordinal.
2. Run all typed entry actions in declaration order.
3. Create objective counters and timer criteria.
4. Broadcast `SCTowerDefWaveStartPacket` with the protocol-adapted step value.
5. Evaluate immediate criteria once after actions finish.

On completion:

1. Atomically mark the step complete.
2. Cancel its timers and unsubscribe or invalidate its objective callbacks.
3. Despawn action leases marked `despawn_on_next_step`.
4. Enter the next step, or end successfully after the final step.

The hard deadline can end any active state. Cleanup still runs exactly once.

## Event-owned spawning

Create a `TowerDefenseSpawnService` and per-world `EventSpawnOwnershipRegistry`.

Every spawn action returns a lease containing:

- Occurrence key and generation.
- Event key, site key, step ordinal, and action/placement ID.
- Spawned object IDs and their spawner handles.
- Creator object ID when applicable.
- Cleanup and respawn policy.

NPCs spawned for an event have respawn disabled unless the action explicitly requests event-managed respawn. The ownership registry is external to persistence-heavy NPC state and is removed during cleanup.

### Ownership propagation through effects

Controller NPCs intentionally use retail on-spawn skills. Those skills may create children through `NpcSpawnerSpawnEffect` or `SpawnEffect`, so the spawn path must propagate the controller's occurrence token to every child.

- Add an optional event-spawn token to the runtime NPC/spawn context.
- `NpcSpawnerSpawnEffect` filters candidate placements to the caster's event site when a token is present.
- The effect tags every child with the same occurrence and registers it under the action lease.
- Recursive skill-created descendants inherit the token.
- Implement `DespawnOnCreatorDeath` through a creator-child relationship in the ownership registry.
- Event cleanup is stronger than creator cleanup and removes all remaining occurrence-owned objects.

Normal, non-event skill behavior stays unchanged when no event token is present.

### Eliminate duplicate on-spawn skills

Keep `NpcEvents.OnSpawn` as the single execution path because it already registers NPC event skills and handles run-once cooldown behavior. Remove the separate on-spawn skill loop from `AI/v2/Behaviors/Common/SpawningBehavior.cs` and retain only the event invocation and AI transition.

Add a regression test with a zero-cooldown on-spawn skill proving one effect application per NPC spawn.

## Objective tracking

Subscribe once per world instance to `WorldEvents.OnUnitKilled`. Route a death to an occurrence only when the victim's object ID exists in the ownership registry.

For each matching objective:

- Verify occurrence key and active generation.
- Verify target type and ID.
- Increment at most once for the victim object ID.
- Log and expose current/required counts.
- Enqueue completion evaluation.

This prevents ambient NPC kills, another site, another occurrence, a respawned duplicate, or an unrelated GM spawn from advancing a rift.

The handler registry initially supports:

- `Npc` kill-count objectives.
- `DoodadAlmighty` destruction/count objectives.
- Timer criteria.
- Optional defended-object death and survive-until-deadline terminal conditions.

Custom manifest extensions may add tagged NPC groups, player-presence, escort arrival, or interaction objectives without changing the state machine.

## Client synchronization

Use the existing packet classes as a presentation adapter around authoritative occurrences.

- `SCTowerDefStartPacket`: send after successful initial spawn and state persistence.
- `SCTowerDefWaveStartPacket`: send after a step's actions and objective setup succeed.
- `SCTowerDefEndPacket`: send once when an announced occurrence ends for any reason.
- `SCTowerDefListPacket`: send a complete active snapshot after instance load, after character world spawn, and when a character changes zone group.

`TowerDefInfo` is populated from runtime state:

- `TowerDefKey.TowerDefId`: compact ID.
- `TowerDefKey.ZoneGroupId`: manifest and runtime-validated zone group.
- `ZoneId`: selected site's resolved zone key.
- `SpotId`: selected site's stable manifest spot ID.
- `TargetObjId`: live initial/anchor target or zero for a presentation-only notice.
- `Position`: selected site's anchor position.
- `CurrentStep`: protocol-adapted step ordinal.

Broadcasts should target online characters in the occurrence's world and applicable zone group. The list snapshot is the recovery mechanism for late joiners and must be sufficient without replaying every earlier packet.

Before enabling production scheduling, capture or replay one retail 1.2 event to confirm whether packet `step` and `CurrentStep` are zero- or one-based and how `SpotId` is assigned. Keep that conversion isolated in the packet adapter and cover it with byte-level packet tests.

## Persistence and restart recovery

Add an additive `tower_def_occurrences` table to `SQL/aaemu_game.sql` and a dated `SQL/updates/...aaemu_game_tower_def_occurrences.sql` migration.

Store:

- Occurrence key as the primary key.
- Event key, tower definition ID, world template/instance, zone group, and site key.
- Status and state generation.
- Scheduled, started, step-entered, hard-deadline, and updated UTC timestamps.
- Current step ordinal.
- Definition/manifest hash.
- Objective progress JSON for diagnostics and policies that can resume it.
- Terminal reason and completion-action idempotency markers.

Write state at occurrence start, step entry, terminal transition, and completion-action execution. Do not synchronously write on every individual kill; debounce progress snapshots because current-step recovery respawns the full current step.

Restart policies:

- `RestartCurrentStep`: Crimson and Grimghast default. Restore the same site and original hard deadline, clean any stale runtime leases, respawn the full current step, and reset that step's kill counters.
- `RestartOccurrence`: restart from initial actions with the original deadline.
- `AbortAndCleanup`: mark interrupted and wait for the next schedule.
- `ResumePersisted`: reserved for custom handlers that can reconstruct an exact entity roster.

Expired records are finalized during startup reconciliation. A definition-hash mismatch fails closed and requires a GM-authorized restart or abort; the manager must not resume changed gameplay under old state.

## Crimson Rift migration

Crimson uses three compact definitions and six physical site clusters. Convert every listed controller to a pinned, inactive event placement and remove its ambient/unpinned form.

| Event | Tower def | Zone group | Sites | Initial binding | Step bindings |
| --- | ---: | ---: | ---: | --- | --- |
| Cinderstone | 3 | 20 | 3 | `9846` / NPC `8828` | `9848`/`8830`, `9849`/`8831`, `9865`/`8847`, `9866`/`8848` |
| Ynystere | 5 | 17 | 2 | `8939` / NPC `8051` | `8940`/`8052`, `8941`/`8053`, `8942`/`8054`, `8943`/`8055` |
| Auroria | 6 | runtime-validated | 1 | `9998` / NPC `8953` | `9999`/`8954`, `10000`/`8955`, `10001`/`8956`, `10002`/`8957`, `10003`/`8958` |

Migration steps:

1. Group controllers by exact co-located X/Y cluster and assign stable site and placement IDs.
2. Set the correct `NpcSpawnerIds`, `StartInactive`, `EventPlacementId`, and `EventSiteKey` on every controller.
3. Validate the single Auroria site's zone key and zone group from loaded world-region data; do not choose one of the overlapping zone-group bounding boxes by inspection.
4. Configure deterministic uniform selection among the three Cinderstone and two Ynystere sites. The occurrence seed guarantees the same selection after restart.
5. Preflight that every selected site has exactly one binding for every compact spawn target.
6. Use the retail controller skills for visuals and subordinate spawns while propagating occurrence ownership.
7. Count only the event-owned kill templates required by compact steps.
8. Leave NPC `8056` and `8849` out unless a compact row or authoritative capture proves a role; the active Crimson definitions do not reference them.
9. At timeout or completion, deactivate all five or six site controllers and despawn every occurrence-owned descendant.

Crimson acceptance per occurrence:

- Exactly one physical site activates.
- No controller is visible before the scheduled occurrence.
- Cinderstone and Ynystere wait 170 seconds before their first combat step; Auroria waits 35 seconds.
- Each kill step advances only after all required owned NPC counts complete.
- Final-wave behavior lasts until its compact timer or the occurrence hard deadline, whichever valid transition happens first.
- No controller, wave NPC, task, or client-active marker remains after end.

## Grimghast Rift migration

Grimghast includes two advance-notice definitions and two combat definitions.

| Event | Tower def | Zone group | Initial binding | Step controller -> formation group |
| --- | ---: | ---: | --- | --- |
| Cinderstone notice | 16 | 20 | presentation anchor; compact target `12271` is optional | no combat steps |
| Ynystere notice | 17 | 17 | presentation anchor; compact target `12272` is optional | no combat steps |
| Cinderstone combat | 13 | 20 | `14335` / NPC `12911` | `14122`/`12720` -> `109220`; `14123`/`12721` -> `109221`; `14124`/`12722` -> `109223` |
| Ynystere combat | 15 | 17 | `14441` / NPC `12994` | `14438`/`12991` -> `111327`; `14439`/`12992` -> `111326`; `14440`/`12993` -> `111328` |

Migration steps:

1. Pin and deactivate the two main fog controllers, the six step controllers, and every wave NPC placement.
2. Remove the existing single ambient entries for NPCs `12718`, `12719`, `12723`, `12883`, `12901`, and `12902` in both regions. Reuse them only as members of validated event formations.
3. Add the missing six formation groups. Every member placement in a group shares the skill-effect group ID and site key but has a unique placement ID.
4. Cinderstone wave 1 must provide 15 owned `12718` and 15 owned `12901`; wave 2 must provide 15 owned `12719` and 15 owned `12902`; wave 3 must provide one owned `12723` and one owned `12883`.
5. Ynystere uses the same required NPC templates and counts through its three region-specific formation group IDs.
6. Preserve each recovered formation member's exact position, rotation, path, and behavior. Validate walk paths load successfully and lead toward the intended defense area.
7. Start combat definitions at world time `0.1`, spawn the fog controller, wait 20 seconds before the first progress step, then honor the compact's 600-second opening delay before wave 1.
8. Run notice definitions at world time `20.0` for 1,200 seconds. They may use a zero target object with a manifest anchor if no authoritative placement exists for compact dummy spawners `12271` and `12272`.
9. The final boss pair completes the combat event; the 3,600-second hard deadline remains the failure/timeout bound.

### Grimghast formation data gate

The repository does not contain the placements referenced by the six skill-effect group IDs. Required kill counts prove minimum membership but do not reveal exact positions, rotations, routes, or formation timing.

Use the following evidence order:

1. An authoritative retail/server world-spawn export for r208022.
2. A retail packet capture that records the spawned NPC templates, transforms, paths/movement, and timing.
3. A reviewed custom re-authoring using the existing region markers as anchors and the exact compact objective counts.

Option 3 can produce a correct custom gameplay implementation, but it must be labeled custom rather than retail-exact. Grimghast cannot pass the retail-accuracy release gate until the formations are sourced or the product owner explicitly accepts the re-authored layout.

## Custom event authoring

Add tower-defense support to the canonical Content Studio project instead of creating a second project.

Suggested source layout:

```text
Content/projects/custom/
  tower-defense/
    event.custom_example.json
  world-placements/
    event.custom_example.main_world.json
```

A custom event manifest contains presentation, trigger, sites, actions, objectives, cleanup, and restart policy. The compiler allocates and writes target-specific records:

- Server compact: `tower_defs`, `tower_def_progs`, `tower_def_prog_spawn_targets`, `tower_def_prog_kill_targets`, and any referenced custom NPC/skill/doodad rows.
- Client compact: only tables and presentation/localization rows present in the client schema. It must not receive the server compact artifact.
- Server runtime bundle: the validated orchestration manifest and world-placement output linked to the server compact build hash.

The Content Studio validator must prove:

- IDs are allocated through `Content/projects/custom/id-registry.json`.
- Every client-visible tower definition and step exists in the client target.
- Every server action and objective exists in the server target.
- Every site resolves to one world, zone key, and zone group.
- All event placements are stable, unique, pinned, and initially inactive.
- Every compact spawn target has a binding at every selectable site.
- A kill objective is satisfiable by the action graph, including type and minimum count.
- Routes, NPC templates, doodads, skills, and localization references exist.
- No concurrency group creates an impossible overlap at a shared placement.
- The runtime manifest hash matches the compact build manifest.

The initial typed action registry should support:

- Spawn or activate an NPC placement/formation.
- Spawn a doodad or activate a doodad placement.
- Deactivate or despawn leased placements.
- Broadcast an event message/presentation beat.
- Change a zone/event flag through an approved handler.
- Run a registered world-script/AI command-set hook.
- Execute an idempotent completion action.

Adding a future action or objective requires one handler, schema support, validation, and tests; it must not require a new event-specific manager.

## GM and operations interface

Replace the packet-only `towerdef` command with manager operations:

```text
towerdef list [active|enabled|invalid]
towerdef status <event-key-or-id>
towerdef validate [event-key-or-id]
towerdef start <event-key-or-id> [site-key]
towerdef next <occurrence-key>
towerdef end <occurrence-key> [reason]
towerdef reload --validate-only
```

Rules:

- `start` runs full preflight and lifecycle logic.
- A supplied site key is a deliberate test override and is logged.
- `next` is restricted to high-access testing, generation-safe, and still performs transition cleanup.
- `end` is idempotent and performs normal cleanup and client notification.
- Runtime reload validates a new immutable catalog first and refuses to replace definitions used by active occurrences.
- Commands show occurrence, site, step, deadline, objective counts, leased object counts, and last transition reason.

## Logging and diagnostics

Use structured, searchable fields in every lifecycle log:

- Event key, tower definition ID, occurrence key, world/instance, zone group, site, step, state generation, and terminal reason.
- Action target, resolved placement count, spawned object IDs, and failures.
- Objective target, current/required count, and ignored-death reason.
- Timer due time, cancellation, and stale-generation suppression.
- Recovery policy and definition hash.

At startup, emit a compact validation summary and write the full report to logs. `towerdef validate` exposes the same report in game. Maintain in-process counters for starts, successes, timeouts, failures, recoveries, active leases, ignored duplicate deaths, and cleanup leftovers; external metrics integration can be added without changing event logic.

Any nonzero owned-object or scheduled-task count after cleanup is an error and fails the relevant integration test.

## Test plan

### Unit tests

Add tests under `AAEmu.UnitTests` that mirror the new source layout.

Loader and validation:

- An orphan progress row before a valid definition does not truncate loading.
- Orphan spawn and kill targets are reported and skipped.
- Progress ordering and zero-based ordinals are deterministic.
- Nullable root fields remain nullable.
- Unsupported target types disable only their definition.
- Duplicate IDs, missing bindings, invalid sites, and unsatisfiable objectives fail closed.

Clock and scheduler:

- Crossing works across normal ticks and midnight.
- `tod_day_interval` and explicit phase work.
- Backward time and repeated DST windows do not double-start.
- Catch-up grace, manual time set, and occurrence-key idempotency work.
- Simultaneous clock and GM starts create one occurrence.

State machine:

- First-wave delay, timer-only, kill-only, AND, OR, immediate, and hard-timeout steps.
- Simultaneous final kill and timeout create one terminal transition.
- Late tasks and deaths from an earlier generation are ignored.
- All terminal paths call cleanup once.
- Restart policies restore the same deterministic site.

Spawning and ownership:

- Site filtering never activates sibling sites sharing a spawner target ID.
- Controller ownership propagates through nested spawn effects.
- Ambient and cross-occurrence kills do not count.
- A victim object counts once.
- `DespawnOnCreatorDeath`, `despawn_on_next_step`, and event-end cleanup work.
- Zero-cooldown on-spawn skills execute once.

Packets:

- Byte-level start, wave, end, and list serialization with captured step/spot conventions.
- Late join and zone-entry snapshots match active state.
- No start packet is sent for a failed preflight.
- End is sent once only for an announced occurrence.

Persistence:

- Additive migration and base schema match.
- Active, expired, hash-mismatched, and already-ended rows reconcile correctly.
- Completion actions remain idempotent after a simulated crash.

### Integration tests

Create synthetic compact and world fixtures, then add real-data-gated tests when a local r208022 compact is available.

- Run every Crimson site from start through timeout/completion and verify only the selected cluster activates.
- Run both Grimghast regions and verify every wave produces the exact objective templates and counts.
- Disconnect/reconnect and change zone during each step; verify list state.
- Restart during first delay, each combat step, final step, and cleanup.
- Fail one placement during preflight and prove no partial event remains.
- Start mutually exclusive events concurrently and prove the configured policy.
- Exercise manual start, next, end, and validate commands through the manager.

### Soak and client validation

- Run at least seven accelerated world days with all seven scheduled definitions enabled: three Crimson, two Grimghast notices, and two Grimghast combat events.
- Repeat starts and forced ends at every site.
- Confirm zero growth in event objects, spawners, subscriptions, or tasks.
- Observe client markers, title/messages, step UI, target location, late-join state, and end removal on an r208022 client.
- Confirm NPC paths, aggro, quest credit, loot, and event cleanup in a multi-player session.

## Delivery phases

### Phase 0: Lock protocol and missing data

- Capture one retail tower-defense start/wave/end/list sequence or derive the client convention from a trusted r208022 trace.
- Recover the six Grimghast formation groups, or obtain explicit approval for a custom re-authored layout.
- Record expected Crimson site-selection policy if authoritative evidence is available; otherwise ship deterministic uniform selection as a documented server policy.

Exit gate: packet step/spot conventions and the Grimghast formation source decision are documented.

### Phase 1: Static data foundation

- Repair and harden `TowerDefGameData`.
- Make definitions immutable and add validation/accessors.
- Add synthetic loader/validation tests.
- Add the runtime-manifest schema and retail-rift overlays with all events disabled.

Exit gate: all valid rift graphs load despite orphan unrelated rows, and invalid events are reported individually.

### Phase 2: Clock, manager, state, and recovery

- Add typed world-clock samples.
- Implement scheduler, occurrence identity, serialized state machine, task generation guards, persistence, and recovery.
- Register the manager in DI and startup/shutdown lifecycle.

Exit gate: state-machine and recovery tests pass without world spawning.

### Phase 3: Placement, spawn, and objective runtime

- Add stable event placement metadata and indexes.
- Implement spawn leases, ownership propagation, creator-child cleanup, site filtering, and typed objective handlers.
- Remove duplicate on-spawn skill execution.

Exit gate: synthetic multi-site and nested-effect tests pass with zero cleanup leftovers.

### Phase 4: Client and operations integration

- Implement packet adapter, scoped broadcasts, reconnect/zone snapshots, and manager-backed GM commands.
- Add packet fixtures and command tests.

Exit gate: a synthetic event is fully playable and visible through start, steps, reconnect, end, and cleanup.

### Phase 5: Crimson migration

- Convert all Crimson controllers to pinned event placements.
- Add and validate all six site bindings.
- Run unit, integration, soak, and client validation for tower defs 3, 5, and 6.

Exit gate: all Crimson acceptance criteria pass at every site.

### Phase 6: Grimghast migration

- Convert notice, fog, stage-controller, and wave entries.
- Add and validate the six recovered or approved formation groups.
- Run both notices and both combat definitions through all recovery and client cases.

Exit gate: every required formation/count/path is verified, duplicate spawns are absent, and both regions clean up completely.

### Phase 7: Custom authoring support

- Add Content Studio schemas, ID allocation, target-specific compact compilation, runtime-bundle generation, validation, preview, and diff support.
- Add one small custom sample event using existing NPCs/assets as an end-to-end fixture.

Exit gate: a custom event can be authored without server code, produces distinct valid server/client artifacts, and passes the same runtime preflight.

## Rollout and rollback

Add configuration with conservative defaults:

```json
{
  "TowerDefense": {
    "Enabled": false,
    "DryRun": true,
    "EnabledEvents": []
  }
}
```

Rollout order:

1. Deploy code, migrations, manifests, and pinned spawn data with the subsystem disabled.
2. Enable dry-run validation only; confirm definitions, sites, paths, and artifact hashes.
3. On a staging/local server, manually run every site with automatic schedules still disabled.
4. Enable one Crimson event, then all Crimson events.
5. Enable Grimghast notices, then one combat region, then both.
6. Disable dry-run only after one full soak window and client validation.

Rollback:

- Set `TowerDefense.Enabled` false. Initialization must reconcile and clean active occurrences before unsubscribing.
- Because all migrated placements are pinned and inactive, disabling the manager leaves them dark instead of restoring the current ambient bug.
- The MySQL table is additive and can remain for audit/recovery.
- Revert source/manifests/world data and rebuild target-specific compact artifacts if the content bundle itself must be rolled back.

## Definition of done

The project is complete only when all of the following are true:

- Loader orphans cannot truncate valid tower definitions.
- Crimson runs at exactly one selected site per occurrence in all three regions.
- Both Grimghast notices and combat events run on schedule in both regions.
- Every Grimghast formation has approved positions, routes, templates, and exact objective counts.
- A zero-cooldown on-spawn controller fires once.
- Only occurrence-owned deaths advance objectives.
- Success, timeout, manual stop, restart, and failure leave no event NPCs, doodads, active placements, subscriptions, or tasks.
- Late joiners and zone-changing players see the correct client state.
- Restart recovery is deterministic and cannot duplicate an occurrence or completion action.
- All unit/integration tests, the accelerated-day soak, `dotnet build`, and `dotnet test` pass.
- The deployed server source, server compact, client compact, runtime bundle, and world placement data have matching reviewed build manifests for their respective targets.
- A sample custom tower-defense event is authorable and runnable without adding event-specific server logic.

## Decisions that must not be guessed

These are explicit gates, not implementation loopholes:

1. Confirm the r208022 packet step base and `SpotId` convention before enabling production client presentation.
2. Recover or approve re-authoring of Grimghast groups `109220`, `109221`, `109223`, `111326`, `111327`, and `111328`.
3. Runtime-resolve and validate the Auroria site's zone key and zone group against loaded world-region data.
4. Treat deterministic uniform Crimson site selection as a documented custom policy unless retail weighting evidence is obtained.
