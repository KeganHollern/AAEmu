# Tower-Defense Rifts Flowchart and Test Runbook

This is the implemented lifecycle for Crimson Rift, Grimghast Rift, and future manifest-defined tower-defense events. The subsystem and every retail schedule remain disabled by default. GM manual starts are allowed so the first deployment can be tested without enabling automatic schedules.

## Lifecycle flow

```mermaid
flowchart TD
    A[World-clock crossing or GM manual start] --> B{Runtime and event enabled<br/>or authorized manual start?}
    B -- No --> Z[Ignore without spawning]
    B -- Yes --> C[Build stable occurrence key]
    C --> D{Already active or present<br/>in persistence ledger?}
    D -- Yes --> Z
    D -- No --> E{Concurrency group available?}
    E -- No --> Z
    E -- Yes --> F[Select one site deterministically]
    F --> G{Compact graph, zone, site,<br/>bindings, and placements valid?}
    G -- No --> X[Fail closed; log reason]
    G -- Yes --> H[Persist Starting, selected site,<br/>definition hash, and hard deadline]
    H --> I[Spawn event-owned initial target]
    I --> J{Initial spawn succeeded?}
    J -- No --> X
    J -- Yes --> K[Broadcast start and schedule hard deadline]
    K --> L{First-wave delay?}
    L -- Yes --> M[Wait with generation-checked task]
    L -- No --> N[Enter next compact step]
    M --> N
    N --> O[Increment generation; clean prior step leases]
    O --> P[Spawn this site's bound actions only]
    P --> Q[Create owned kill counters and timer criteria]
    Q --> R[Persist step and broadcast wave]
    R --> S{AND/OR criteria complete?}
    S -- No --> T[Owned NPC death or timer callback]
    T --> U{Current occurrence and generation?}
    U -- No --> S
    U -- Yes --> S
    S -- Yes --> V{Another step?}
    V -- Yes --> N
    V -- No --> W[Succeeded]
    K -. hard deadline .-> Y[Timed out]
    R -. GM end or runtime failure .-> Y2[Cancelled or failed]
    X --> AA[Idempotent terminal cleanup]
    W --> AA
    Y --> AA
    Y2 --> AA
    AA --> AB[Cancel tasks; deactivate placements;<br/>despawn every owned descendant]
    AB --> AC[Persist terminal reason and broadcast end]
    AC --> AD[Ended]
```

## Spawn ownership flow

```mermaid
flowchart LR
    A[Occurrence step action] --> B[Stable placement in selected site]
    B --> C[Controller NPC gets occurrence token]
    C --> D[NpcSpawnerSpawnEffect]
    D --> E{Placement belongs to<br/>the same event site?}
    E -- No --> F[Reject]
    E -- Yes --> G[Child inherits occurrence,<br/>generation, action, creator]
    G --> H[Per-world ownership registry]
    H --> I[Only registered current-owned deaths count]
    H --> J[Creator death, next step, event end,<br/>world cleanup, or shutdown despawns child]
```

## Restart reconciliation

```mermaid
flowchart TD
    A[Server starts] --> B[Load non-terminal occurrence rows]
    B --> C{Original hard deadline expired?}
    C -- Yes --> D[Finalize TimedOut; no respawn]
    C -- No --> E{Manifest, compact graph, world,<br/>site, and placement preflight pass?}
    E -- No --> F[Finalize Failed; no respawn]
    E -- Yes --> G{Definition hash unchanged?}
    G -- No --> F
    G -- Yes --> H[Use persisted site and original deadline]
    H --> I[Respawn initial target and current step]
    I --> J[Reset current-step counters]
    J --> K[Broadcast current state to present players]
    K --> L[Continue normal lifecycle]
```

## First in-game validation

Keel should deploy the committed image normally. Do not enable automatic schedules for the first pass. With a level-100 GM character, test one occurrence at a time:

1. Run `tower_def list` to review every loaded event, compact validity, schedule state, and available site key.
2. Run `tower_def start rift.crimson.cinderstone cinderstone-1`, travel to the announced marker, and verify exactly that Cinderstone cluster appears. Repeat later with `cinderstone-2` and `cinderstone-3`.
3. Run `tower_def next rift.crimson.cinderstone` to shorten each wait during smoke testing. Use `tower_def list` after each transition.
4. Run `tower_def end rift.crimson.cinderstone smoke_test` and verify the marker, controllers, waves, and descendants all disappear.
5. Repeat for both Ynystere sites, the Auroria site, `rift.grimghast.cinderstone`, and `rift.grimghast.ynystere`.
6. For each Grimghast combat event, verify wave counts are 30, 30, and 2 and that no formation spawns twice.
7. Test the notices with `rift.grimghast.cinderstone.notice` and `rift.grimghast.ynystere.notice`.
8. During one active combat step, relog and cross a zone boundary; the active marker and current wave must be restored.
9. For the restart test only, set the global tower-defense runtime to enabled while leaving every event manifest schedule disabled. During an active step, let Keel restart the pod. The same site and original deadline must recover without duplicating NPCs. With the global runtime disabled, persisted occurrences are deliberately finalized instead of resumed.

The six missing Grimghast formation groups use the approved custom re-authoring path: exact compact objective templates/counts with reviewed formation grids around the existing region anchors. They are intentionally labeled custom, not retail-exact, until an authoritative r208022 placement export or packet capture replaces them.

## Future custom events

Add one or more manifest files under `Content/projects/custom/tower-defense/`. Content Studio validates required event fields, rejects duplicate event keys, includes source hashes in its build manifest, and emits a separate `tower-defense.<project>.json` runtime bundle. The game server loads every non-schema JSON manifest in `Data/TowerDefense`, so adding a reviewed bundle does not require event-specific server code.

Compact rows and referenced assets continue to use the existing Content Studio records/raw-SQL and ID-registry paths. World placements remain stable `npc_spawns*.json` overlays with `StartInactive`, `EventPlacementId`, and `EventSiteKey` metadata.
