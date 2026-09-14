# Caster states for r208022

Issue: `KeganHollern/aaemu-cluster#445`.

`Skill.UseCore` checks caster states before it changes skill state, mana, cooldowns, buffs, labor, or world objects.
The caller receives the normal `SkillResult` and its detail value.
The same check applies to player requests and server skill calls.
`bypassGcd` does not bypass caster states.

## Exact-client evidence

The research uses the retained r208022 runtime dump and `objdump -d -Mintel`.
It does not use a new runtime capture or a gameplay test.
Native addresses use image base `0x38ff0000`.

| Input | SHA-256 |
| --- | --- |
| `client/bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| `.tools-re/dumps/x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| `compact/server.sqlite3` | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |

The cluster workspace keeps ignored assembly excerpts, SQL queries, and compact results in `.tools-re/combat-20260913/states/`.

The native skill SELECT starts at `0x39a06a90`.
Its loader starts at `0x39731870` and uses a temporary template at `[ebp-0x2c8]`.
The following fields connect the compact schema to the native caster check at `0x390b7b00`.

| Field | SELECT ordinal | Template offset | Native check and result |
| --- | --- | --- | --- |
| `source_alive` | 94 | `0x148` | `0x390b7b30`. False rejects a living caster with `SOURCE_ALIVE` (`3`). |
| `source_dead` | 96 | `0x14a` | `0x390b7b56`. False rejects a dead caster with `SOURCE_DIED` (`2`). |
| `source_stun` | 102 | `0x150` | `0x390b7bff`. True bypasses the Stun check only. |
| `damage_type_id` | 44 | `0x98` | `0x390b7c9d`. Magic (`2`) uses the Silence check. Melee (`1`) and Ranged (`4`) use Crippled. |

The native buff SELECT starts at `0x39a07550`.
Its loader uses a temporary template at `[ebp-0x340]`.
The visitor at `0x390b4670` copies these flags into the unit state array at `unit+0x3a70`.

| Buff field | SELECT ordinal | Template offset | Unit offset | Caster result |
| --- | --- | --- | --- | --- |
| `stun` | 149 | `0x1a4` | `0x3a70` | `CANNOT_CAST_IN_STUN` (`0x13`), unless `source_stun` is true. |
| `sleep` | 142 | `0x191` | `0x3a71` | `CANNOT_CAST_IN_STUN` (`0x13`). |
| `blank_minded` | 11 | `0x28` | `0x3a73` | `BLANK_MINDED` (`0x16`), with the blocking buff ID. |
| `silence` | 139 | `0x189` | `0x3a74` | `SILENCE` (`0x17`) for Magic only. |
| `crippled` | 19 | `0x39` | `0x3a75` | `CRIPPLED` (`0x18`) for Melee and Ranged only. |
| `pacifist` | 90 | `0x12a` | `0x3a77` | The separate native attack-target predicate at `0x390b8010` returns false. |

The result-name table at `0x39897990`, with its jump table at `0x39897d4c`, confirms these numeric results.
The `BLANK_MINDED` branch at `0x390b7c43` finds the blocking buff and returns its ID in the detail field.
The server keeps this detail instead of replacing it with zero.

## Authored exceptions

The compact has 14,913 alive-only skills, 211 dead-only skills, and 2 skills for either state.
`SkillTemplate.SourceAlive` now stores the column that the old loader omitted.
Its default is true for synthetic templates, while the compact loader reads each authored value.
For example, skill `10502` is dead-only, and experience recovery skill `26611` accepts either state.
The gate preserves these authored cases.

Skill `11429`, Shrug It Off, sets `source_stun=true` and uses Magic damage type.
The native exception permits Stun but does not permit Sleep, BlankMinded, or Silence.
The server uses the same separation.

Combat knockdown buff `1318` sets both `knock_down` and `stun`.
The Stun check covers this control effect.
Eight compact buffs set `knock_down` without Stun, including fall-damage protection buff `3784` and animation effects.
The gate does not treat every knockdown, ragdoll, or displacement flag as Stun.
Root alone does not block skill use.

## Server rules from issue 445

Two requested checks need server rules beyond the confirmed native caster function.
These rules are not claims about an unseen original server.

Fear buff `1178` links to controller `5683`, kind `3` (`Wandering`).
It sets none of Stun, Sleep, BlankMinded, Silence, or Crippled.
Character movement controllers run in the client, so the server must check the active buff link.
The gate rejects an active Wandering buff or a running NPC Wandering controller with `BlankMinded`.
It returns the source buff ID when available.
Neither `SourceStun` nor `EndSkillController` supplies an unconfirmed Fear exception.
A Leap or Dash controller alone does not trigger this rule.

The requested Pacifist caster rule rejects hostile skill templates with `NoPerm` (`0x2d`).
Hostile target type, hostile target relation, or an exclusively non-friendly target effect marks a hostile template.
This includes position-based area skills with hostile effects.
Source-only effects and friendly or self skills remain available.
The native evidence confirms the Pacifist state, but its separate attack-target predicate does not establish this caster rule or result.

Only buffs that are in use, not ended, and not expired affect the gate.
Permanent buffs remain active through their normal duration-zero representation.
The gate reads a snapshot of the buff collection and does not remove buffs or start controllers.

## Validation

`SkillCasterStatesTests` covers both life-state flags, native Stun exceptions, Sleep, BlankMinded details, and combat knockdown data.
It also covers exact Silence and Crippled damage types, Fear controller links, and the Pacifist rules for direct and area skills.
Inactive and expired buff cases protect normal skill use after control ends.
The `Skill.Use` regression checks rejection before mana, cooldown, buff removal, or world access, with either `bypassGcd` value.

The parent combat release records the coordinated build and test results.
Focused gameplay checks after publication need a normal attack, Shrug It Off under Stun, a spell under Silence, and a skill under Fear.
Also check normal skill use after control ends and hostile versus friendly skills under Pacifist.
