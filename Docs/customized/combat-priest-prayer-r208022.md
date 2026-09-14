# Priest prayer grant for r208022

The review of issues `#443` and `#474` found one missing grant in the new skill checks.
Prayer skill `17063` has no default, buff, NPC interaction, doodad function, or item grant in the compact.
The exact client still requests it through the priest recovery interface.

## Native producer

This trace uses the retained r208022 `x2game.dumped.dll` with image base `0x38ff0000`.
Its SHA-256 is `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0`.
The trace uses `objdump`, not a new runtime capture.

| Address | Evidence |
| --- | --- |
| `0x399e13d4` | Native `RecoverExp` method name. |
| `0x3949b02c` and `0x3949b071` | Registration connects `RecoverExp` to callback `0x3949a480`. |
| `0x3949a4ab` through `0x3949a4c6` | The callback creates a Unit caster and a Unit target with the player's object ID. |
| `0x3949a4cd` | The callback reads skill ID `17063` from global `0x39ac9a30`. |
| `0x3949a4e3` | The callback calls the skill-start dispatcher at `0x39397e50`. |

The retained X2UI disassembly `.tools-re/economy/npc-interaction-ui.txt` opens `PRIEST_RECOVER_EXP_NPC` for `priest_recover_exp`.
The native callback supplies neither an NPC target nor a skill-tree grant.
The cluster workspace keeps the focused assembly excerpts in `.tools-re/combat-20260913/states/priest-recovery-*.asm`.

## Server grant

`PriestSkillAuthorization` permits only this exact Unit/self request for skill `17063`.
The loaded template must contain a recovery effect with `NeedPriest=true`, `NeedLaborPower=true`, and `NeedMoney=false`.
A live priest must exist within 10 metres in the same world and instance.
`ServiceInteraction.CanReach` checks the current NPC object identity and the distance.
The check does not grant other skills from the same category, ability, or effect type.

`RecoverExpEffect` still checks the priest and settles the labor payment with experience recovery.
The new rule grants access to that path and does not grant free recovery.
Full recovery skill `26611` belongs to consumable scrolls in the compact.
It retains the owned-item route and receives no Unit grant.

The compact also maps generic recovery skill `11361` to 350 doodad functions.
Skill `15309` maps to 4 direct functions and 10 fake-use functions.
The new phase grants cover those authored routes without a generic recovery exception.

## Validation

`PriestSkillAuthorizationTests` covers the exact request, the inclusive 10-metre limit, and the loaded recovery effect.
It rejects absent, dead, stale, distant, or wrong-world priests.
It also rejects other caster types, other targets, and the full recovery scroll skill.
The parent combat release records the coordinated build and test results.
