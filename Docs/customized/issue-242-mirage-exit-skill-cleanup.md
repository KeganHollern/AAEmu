# Mirage Isle exit skill cleanup

## Target

- Packet name: `SCSkillEndedPacket`
- Direction: G2C
- Packet level: `1`
- Opcode: `0x0a3`
- Intended lifecycle: End the active client skill before an instance load replaces the world.
- Task: `KeganHollern/aaemu-cluster#242`

## Exact client

- Client revision: `208022`

| Module | Role | Path | Size | SHA-256 | PE time | Preferred base | Entry RVA | Image size | Runtime base |
| --- | --- | --- | ---: | --- | --- | --- | --- | --- | --- |
| `x2game.dll` | source | `~/archeage/client/bin32/x2game.dll` | 18,483,712 | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` | 2014-10-14 00:44:21 | `0x38ff0000` | `0x008c5d6d` | `0x01cb0a00` | not applicable |
| `x2game.dumped.dll` | dump | `~/archeage/.tools-re/dumps/x2game.dumped.dll` | 30,085,120 | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` | 2014-10-14 00:44:21 | `0x38ff0000` | `0x008c5d6d` | `0x01cb0a00` | same |

- Dump tool and command: This information was not recorded. This is a provenance gap.
- Analysis tool: Ghidra `12.1.3 PUBLIC`.
- Analysis script: `.tools-re/issue242/DecompileIssue242.java`.
- Analysis script SHA-256: `decb25864970c0877f25c2347a341ffc3a4359d3e272b4b5c5786b55bddc1749`.
- Opcode map: `AAEmu.Game/Core/Packets/G2C/SCOffsets.cs`.
- Opcode map SHA-256: `3ff0c0052d272cccd5d77444b15329a5c1113e6786e540e60c59af60f38fdca5`.
- Opcode generation method: Existing AAEmu r208022 offset table.

## Prior research

- Worktree: `fix/issue-242-mirage-exit-fx` with deployment commits through `a3b0c0d6`.
- Forward-correction worktree: `fix/issue-242-character-exit-guard` from `49170c21`.
- Ignored artifacts: Ghidra project data and the decompiler helper under `.tools-re`.
- The issue image shows the pale body glow and ground markers from FX group `428`.

## Starting server evidence

- Packet class: `SCSkillEndedPacket` writes one `ushort` TL ID.
- Mirage portal skill: `17838`, `Return to Previous Location`.
- Exit icon skill: `26152`, through special effect `ExitArchemall`.
- Portal route: `InteractionEffect -> Use -> DoodadFuncExitIndun`.
- Load route: `RequestLeaveInstance -> OnDungeonLeave -> LeaveSystemInstance -> SCLoadInstancePacket`.
- Starting order: `SCSkillFiredPacket`, `SCLoadInstancePacket`, then `SCSkillEndedPacket`.

The server compact maps skill `17838` through these records:

| Object | ID or value |
| --- | --- |
| Skill FX group | `428` |
| Skill effect | `15790` |
| Effect | `19756` |
| Interaction effect | `2526` |
| World interaction | `19`, `Use` |
| Exit doodad template | `4895` |
| Doodad function group | `12205` |
| Doodad function | `10530`, `DoodadFuncExitIndun` |
| Exit function data | `3` |

FX group `428` contains item `628`, asset `ability_skill_table_k.p_skill_love.overhill_form`.
Its `overhill_form_glow` node is continuous and binds to the emitter.

## Git history

| Commit or ref | Finding | Authority for r208022 |
| --- | --- | --- |
| `23045c7a` | Added the doodad exit with load before skill end. | Comparison only |
| `4e5b8018` | Added the exit icon with the same order. | Comparison only |
| `0d89a74e` | Separated effect application and normal skill end. | Comparison only |
| `c3b9d408` | Moved both exits to `RequestLeaveInstance` without an order change. | Comparison only |
| `6c6514c8` | Removed a second full `EndSkill` call because it used labor twice. | Confirmed design constraint |
| `fcfbeb75` | Initial research base. | Confirmed |
| `36c52263` | Base for the rebased local tests. | Confirmed |
| `a3b0c0d6` | Later deployment commit merged into the PR branch. | Confirmed |

## Wire contract

- Wire frame: `DD 01 00 00 A3 00 <tlId:uint16-le>`.
- Packet body size: `2` bytes.
- Total encoded size before transport framing: `8` bytes.

| Body offset | Wire type | Field | Meaning | Native limit | Evidence | Confidence |
| --- | --- | --- | --- | --- | --- | --- |
| `0` | `uint16-le` | `tlId` | Active client skill identity. | `65535` | AAEmu writer and native handler | Confirmed |

No packet field changes are part of this fix.

## Native evidence

- `OnSkillEnded` wrapper: preferred VA `0x391d5bf0`.
- `OnSkillEnded` consumer: preferred VA `0x39397ff0`.
- `OnLoadInstance` wrapper: preferred VA `0x391de970`.
- `OnLoadInstance` consumer: preferred VA `0x3936f1c0`.
- The skill-end consumer finds the active skill by TL ID.
- It runs the skill cleanup and visual-effect paths before it removes the active state.
- The load consumer starts the instance and world replacement path.

These paths confirm that packet order controls when the client can clean the active skill.
The exact cause of the retained emitter remains an inference until a client packet trace confirms it.

## Server lifecycle and state

- Send the early skill-end packet immediately before either Mirage exit requests the world change.
- Keep the normal `EndSkill` call after effect application.
- Let normal `EndSkill` use labor, run callbacks, record achievements, and release the TL ID once.
- Keep the normal later skill-end packet. Existing special effects use the same early-notification pattern.
- Use the selected `DoodadFuncExitIndun` type for the portal guard.
- Require a character caster because `DoodadFuncExitIndun` ignores other caster types.
- No database, compact, config, or persistent-state change is needed.

## Validation

- `InteractionEffectTests` checks the early packet order for `DoodadFuncExitIndun`.
- The same test checks opcode `0x0a3`, packet level `1`, body size `2`, and the exact TL ID.
- A negative test checks that another doodad function does not get an early skill-end packet.
- A negative test checks that a non-character caster does not get an early skill-end packet.
- `ExitArchemallTests` checks the icon route order.
- The AAEmu unit-test project passed all `1638` tests on .NET `10.0.11`.
- Manual r208022 test: Pending after deployment.

## Conclusion

- Confirmed: The server sends the instance load before the skill end on both Mirage exit routes.
- Confirmed: Portal skill `17838` owns the continuous FX group shown in the issue image.
- Confirmed: The client uses the skill TL ID to run its skill cleanup path.
- Inferred: The intervening instance load leaves the continuous emitter attached to the character.
- Next experiment: Exit through the portal twice and check the character after each world load.
