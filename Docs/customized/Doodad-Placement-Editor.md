# Doodad placement editor

`/doodad edit` is an admin-only placement tool for authored world doodads. It uses the real ArcheAge client renderer, so the preview keeps the client model, pivot, materials, scale, animation state, and surrounding geometry.

The editor is intended for coordinate tuning. It does not save changes itself. It emits paste-ready placement JSON for a reviewed source edit.

## Safety model

The editor does not move or change the authoritative server object.

- It reserves a collision-free runtime `ObjId` for a detached visual copy. The preview ID is not registered in the world, so interaction packets cannot resolve it to the authoritative doodad.
- It sends remove/create packets only to the admin who started the edit.
- Other clients continue to see the authoritative doodad during normal editing.
- World scripts, phase timers, spawners, collision checks, and MySQL continue to use the authoritative doodad.
- Normal `done`, `cancel`, dungeon leave, logout, or disconnect cleanup removes the preview and releases its ID.
- Only currently visible, unowned, unparented, non-persistent system doodads whose exact spawner is registered from world JSON can be selected. Runtime-created system doodads are rejected.

Do not interact with the detached preview. Some unrelated interaction handlers do not safely reject an unknown object ID and can disconnect the editor or broadcast invalid movement for climbable or quest-bearing templates. The Sharpwind door and rocks are not used through those paths. The phase command changes only the detached copy's visible func group and sends a private phase-change packet to replay its client visual. It does not run server phase funcs, phase timers, skills, persistence, or dungeon events.

This is intentionally a lightweight in-game debugging tool. Do not invoke it through the Web API. Run `cancel` before `/scripts reload`; active sessions are not transferred to a reloaded command instance. Session start/end logs include both object IDs so unmatched cleanup can be diagnosed later.

Access is explicitly restricted to level `100` for both `doodad edit` and its `doodad place` alias.

## Basic workflow

Stand near the doodad in its normal world or instance. Select it by runtime object ID:

```text
/findobj doodad 5541 1
/doodad edit select <ObjId>
```

Or select the closest safe world spawn for a template:

```text
/doodad edit nearest 5541 30
```

The optional radius defaults to 30 metres and is limited to 500 metres. Selection still requires the doodad to be in the client's current server visibility region.

Adjust the preview with small relative steps:

```text
/doodad edit nudge x 0.1
/doodad edit nudge y -0.05
/doodad edit nudge z 0.01
/doodad edit rotate yaw 1
/doodad edit rotate pitch -0.5
/doodad edit nudge scale 0.05
```

Set an absolute component when needed:

```text
/doodad edit set x 486.4
/doodad edit set y 327.8
/doodad edit set z 165.8
/doodad edit set roll 0
/doodad edit set pitch 0
/doodad edit set yaw -30
/doodad edit set scale 1
```

Positions use world metres. Rotations use degrees. The server keeps exact finite `float` values for output. Client position packets are quantized, so visual steps much below about `0.002` metres are not useful. The editor rejects coordinates outside the current world or the packet-safe ranges: `-32768 < X/Y < 32768` and `-100 <= Z < 4096`.

Inspect or preview available func groups:

```text
/doodad edit phases
/doodad edit phase 14240
/doodad edit phase original
```

Other session controls are:

```text
/doodad edit undo
/doodad edit reset
/doodad edit refresh
/doodad edit status
/doodad edit json
/doodad edit done
/doodad edit cancel
```

- `undo` reverts one edit operation.
- `reset` returns the preview to the transform and phase captured at selection.
- `refresh` resends the private preview if a visibility update replaced it with the authoritative view.
- `status` shows the current transform and paste-ready JSON.
- `json` emits and logs the result without ending the session.
- `done` emits and logs the result, restores the authoritative view, and ends the session.
- `cancel` restores the authoritative view and ends the session without emitting a result.

Results are logged with the marker `DOODAD_PLACEMENT_RESULT`.

## Sharpwind Mines examples

Guild Storage Door, template `5541`:

```text
/doodad edit nearest 5541 30
/doodad edit phases
/doodad edit phase 14240
/doodad edit nudge x 0.1
/doodad edit rotate yaw 1
/doodad edit phase 14241
/doodad edit phase 14298
/doodad edit done
```

Its known func groups are:

- `14240`: default/closed
- `14241`: open animation phase
- `14298`: opened state

Broken rock wall, template `5280`:

```text
/doodad edit nearest 5280 30
/doodad edit phase 13665
/doodad edit nudge z 0.05
/doodad edit rotate yaw 1
/doodad edit phase 13666
/doodad edit phase 15127
/doodad edit done
```

Its known func groups are:

- `13665`: intact `cuttingwind.brokenrock1`
- `13666`: break effect / `brokenrock2`
- `15127`: invisible state

The editor previews each selected func group directly. It intentionally does not reproduce the server-side timer sequence between those groups.

## Applying the result

The compact chat/log output contains the template and exact placement values:

```json
{"UnitId":5541,"Position":{"X":486.4,"Y":327.8,"Z":165.8,"Roll":0.0,"Pitch":0.0,"Yaw":-30.0},"Scale":1.0}
```

Copy `Position` and `Scale` into the matching entry in `AAEmu.Game/Data/Worlds/<world>/doodad_spawns.json`. Keep any existing metadata such as `Id`, `Title`, `RelatedIds`, `SpecialLink`, comments, and phase fields. The preview phase is intentionally not exported.

After editing the source JSON, use the normal review, test, release, and deployment process. A source reload or server restart is required to make the authored placement authoritative.
