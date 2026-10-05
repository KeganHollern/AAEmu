# r208022 FakeDeath combat escape, issue #321

## Scope

The zero-argument FakeDeath effect removes incoming NPC threat. It does not
change HP, call real death, create loot, grant kill credit, or revive a unit.
It leaves the original buff and its duration intact. NPCs use their normal
retarget or return path after removal of the threat entry.

Kegan approved separate research for the Downfall impact variant on
2026-10-04. [Issue #635](https://github.com/KeganHollern/aaemu-cluster/issues/635)
tracks that variant. This release does not apply combat escape to Downfall.

## Exact inputs

| Input | SHA-256 |
| --- | --- |
| r208022 `client/bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| Matching native memory dump | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| Client compact | `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4` |
| Server compact | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |

The native image base is `0x38ff0000`. The research reused the existing
matching dump. It did not make a new client dump.

## Data contract

| Special row | Wrapper | Caller | Values | Result |
| --- | --- | --- | --- | --- |
| 1036 | 5408 | Buff 1010, Play Dead for Survival | `0,0,0,0` | The normal buff tick removes incoming NPC threat. |
| 7008 | 21367 | Skill 18433 | `0,0,0,0` | The same combat-escape rule applies. The Korean description marks this old clone skill unused. |
| 16829 | 40107 | Skill 26966 | `0,0,0,0` | The Korean description specifies escape from aggro through fake death. |
| 925 | 4659 | Skill 12279, Downfall | `700,70,0,0` | Deferred to #635. This hostile impact has an unconfirmed reaction contract. |

The other raw FakeDeath rows have no current effect wrapper.
Buff 1010 has a 7,000 ms duration, a 1,000 ms tick, `ragdoll = true`,
silence, and cripple. The server leaves that authored presentation in place.
The client data selects the ragdoll presentation, but a human still needs to
check the visible pose and recovery. No new death packet is appropriate for
an alive unit.

Skill 26966 appears as the base skill of NPCs 14864, 14880, 14881, and 14882.
The implementation does not depend on those content placements. It applies
the confirmed general combat-escape behavior to the selected unit.

## Native research boundary

The special-effect loader at `0x3972c390` reads the type followed by 4 integer
values. The loader does not explain the server meaning of those values.
SCSkillFired factory `0x391ab7c0` uses opcode `0xa2`. Handler `0x391d58a0`
calls `0x3939b010`, whose direct special-effect cases cover types 10, 69,
and 97. That path does not prove client handling of FakeDeath type 18.
OnUnitDamaged handler `0x391d5fb0` calls `0x393978e0` and `0x39398c80`.
The inspected consumers did not prove the Downfall impact contract.
The implementation does not treat this negative result as proof that the
client owns that variant.

## Tests and limits

The regression tests use the real SpecialEffect wrapper and the normal
BuffTemplate tick path with no Skill instance. They check repeated ticks,
new threat between ticks, preservation of other targets, unchanged HP and
buff state, no death packet, no death event, and continued world membership.
The skill-path test checks reverse threat links and preserves player selection.
The dead-target test checks that this effect does not revive or alter a corpse.

The effect removes NPC threat at each application. It does not make the unit
immune to new damage or threat. It does not clear another player's selection.
The normal target-clear packet contract appears in
`r208022-aggro-special-effects-321.md`.

## Human validation

Use the published server and r208022 client. Record results in #573.

1. Apply Play Dead for Survival, buff 1010, during a controlled NPC fight.
2. Check that the NPC stops its current attack or selects another threat target.
3. Check that HP does not drop to zero and no death screen or kill reward appears.
4. Check that the authored pose ends when the buff expires.
5. Check that combat can start again after recovery.

This check does not validate Downfall or a complete dungeon encounter.
