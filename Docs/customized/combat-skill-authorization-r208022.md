# Player skill authorization for r208022

Issues: `KeganHollern/aaemu-cluster#443` and `#474`.

The packet handler now checks explicit skill grants before it starts a player skill.
`AutoLearn`, `NeedLearn`, and equal ability levels do not establish ownership.
Server-originated quest, plot, and doodad child skills keep their normal server call paths.

## Inputs and evidence

The source base is `2ef0c9140bf847de83b850375b522993139c82ee`.
The official upstream base is `b34db3e5f5bce0b9b9bac2a6916a936f0e946a1f`.
The research uses client revision `208022` from `client/history.txt`.

| Input | SHA-256 |
| --- | --- |
| Original `client/bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| Retained runtime dump `x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| Server compact, 119054336 bytes | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |

Native addresses use the dump image base `0x38ff0000`.
The retained dump supplies static evidence, not a new gameplay capture.
Ignored exports and SQL results are under `.tools-re/combat-20260913/auth` in the cluster workspace.

The native loader at `0x395b16e0` reads `SELECT buff_id, skill_id FROM buff_skills`.
It resolves both templates and appends each skill reference to the buff at offsets `0x284` and `0x288`.
The current compact contains 567 relations for 295 distinct skills.
Skill `17663` has grants from buffs `1463` and `2098`.

The former common-skill selection admits 9975 non-default rows in this compact.
This count treats both numeric and text SQLite boolean values consistently.
That set includes NPC and system skills, including fixed-damage skill `16927`.
The earlier ticket count uses an older snapshot and a broader selection.

## Admission rules

| Source | Required grant |
| --- | --- |
| Character Unit | The caster object is the authenticated character. The skill is a default, basic attack, or exact learned skill. |
| Buff | An active, unexpired buff grants the exact skill through `buff_skills`. Ended and inactive buffs do not grant skills. |
| Item | The owned item grants its exact use skill. The established portal-book exceptions keep their exact item, target, and object checks. |
| Doodad interaction | The target exists in the same world and instance. Its current phase explicitly names the skill or the applicable fake/use skill. |
| NPC interaction | The target exists in the same world and instance. Its authored interaction set names the skill. |
| Priest prayer | The exact native Unit/self action for skill `17063` passes the nearby priest checks. |
| Mount or vehicle | The separate mount authorization checks the real unit, owner or operator, seat, and authored mount-skill pair. |
| Combo follow-up | The authenticated character consumes the currently armed combo stage. Other source kinds cannot consume it. |

The doodad rule does not use `GetFunc` as proof of authorization.
That resolver deliberately has a zero-skill fallback for server function dispatch.
A zero-skill function must not grant every client-selected skill.
Quest item actions keep their item grants, and ordinary quest doodad actions keep their phase grants.
Quest component skills remain server actions and do not become client grants.

The old ability-level variant shortcut also disappears.
For example, learned Charge `11918` does not grant internal child skill `12028` to a client request.
The server can still call the child skill from its authored execution path.
Existing combo-state checks continue to authorize their exact next stage.

The handler reports unauthorized requests through the established failed `SCSkillStartedPacket` body.
It logs the character object ID and requested skill ID.
It does not deduct resources, start cooldowns, or run effects for those requests.
The packet body and opcode do not change.
`CSStartSkillPacket` remains C2G level 1, opcode `0x052`.

## Passive learning

`CSLearnBuffPacket` remains C2G level 1, opcode `0x093`, with one unsigned 32-bit passive ID.
The native Lua registration for `LearnBuff` points to `0x394385d0`.
That wrapper calls `0x39384430`, which resolves the passive and checks its selected ability, level, total points, and invested points.
The ability checks call `0x398a13f0` and `0x398a1410` for the active flag and level.
The request stores the passive ID through `0x397abf90`.

The server now rejects unknown passives and passives outside the character's active player abilities 1 through 10.
It returns the established `InvalidTarget` error for an invalid selection.
It also checks the authored passive level before application.
The existing point and duplicate checks remain in the same purchase lock.

The compact contains 125 general passives.
Passive record `173` grants NPC/test buff `3590`, while record `188` grants buff `4433`.
The request carries the passive record ID, not the resulting buff ID.
Tests cover both records and an inactive player ability.
Load also ignores invalid saved passive selections, so a reconnect cannot apply them.
This change does not delete persistent rows or add a passive schema migration.

## Research boundaries

The exact client confirms the grant relations, passive request, and priest action.
The server owns the authorization checks and failure behavior for forged requests.
The priest exception has its own [native evidence record](combat-priest-prayer-r208022.md).
The broad maximum-range changes for doodads and large vehicles remain issue `#456`.

The current r208022 compact has no `character_default_skills` table.
The newer upstream racial-default change cannot be copied with its later schema assumptions.
This release uses the current `default_skills` table and the existing authored requirements.

## Validation

`PlayerSkillAuthorizationTests` covers raw rejected requests, exact ownership, buff expiry and removal, current doodad phases, and NPC sets.
It also covers invalid passive packets, normal passive requirements, and an optional complete compact load.
Existing item-source and combo tests remain part of the complete unit suite.
The [release validation record](combat-release-validation-20260914.md) gives the complete build and test results.

Human validation remains necessary after publication.
Check normal attacks, class skills, quest items, harvest actions, buff-granted skills, and priest prayer.
The packet tests cover forged NPC skills and passive requests without a modified live client.
