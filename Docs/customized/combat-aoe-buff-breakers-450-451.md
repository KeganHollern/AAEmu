# AoE diminishing and buff breakers

This record covers aaemu-cluster issues #450 and #451 against AAEmu base
`3409b265a632cd0512bb3db36df402816ccca6dd`. The changes use the current compact
data. They do not change packets, a database schema, or client content.

## Evidence inputs

The client history identifies version `208022`.

| Input | SHA-256 |
| --- | --- |
| Packed `client/bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| `.tools-re/dumps/x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| `compact/server.sqlite3` | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |

Both PE files have timestamp `0x543cb835`, image base `0x38ff0000`, and image size
`0x01cb0a00`. The earlier dump has no separate capture record. This work used
Ghidra 12.1.3 and the GNU disassembly of that dump. It did not capture live packets.
Addresses below use the dump's virtual address space. Local exports are under
`.tools-re/combat-rules-20260926/aoe-buffs/` in the cluster workspace.

## Confirmed data and plot structure

The client loader at `39620e90` reads `SELECT id, rate FROM aoe_diminishings`.
It clears an 11-entry array at the data object's offset `+0xa434`, then stores
each rate at its authored index. The compact contains IDs 1 through 10.

| Target number | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Rate, percent | 100 | 90 | 80 | 70 | 55 | 40 | 25 | 10 | 10 | 10 |

There are 13 plot events with `aoe_diminishing=t`. Twelve contain a direct
`DamageEffect`. Event 5511 contains an `InteractionEffect`, which does not use
the damage multiplier. The client loader at `39896210` reads the plot-event
flag. The string reference is at `39896262`.

There is 1 authored reset route. `plot_effects` row 5680 calls
`ResetAoeDiminishingEffect` 2 from event 246 in plot 1. The native reset loader
is `3972fc20`. Both the general effect loader at `39730620` and the plot effect
loader at `39895590` recognize the reset effect type.

Event 246 repeats every 500 milliseconds. Its other branch reaches events
1159 and 3, then calls event 2723 separately for each selected target.
Event 2723 has diminishing enabled. A counter local to one node call would
give every target 100 percent in this route.

Other marked events repeat without a reset. For example, event 239 has 10
tickets, event 1141 has 5 tickets, and event 2903 has 15 tickets.
The current `skills` and English `localized_texts` rows identify useful test
routes: Searing Rain 11939 uses plot 1, Missile Rain 13281 uses plot 6, and
Whirlwind Slash 13282 uses plot 133.
The current plot engine keeps one `PlotState` for a cast and passes it to
all child nodes. Source history added the unfinished behavior in
`c7d201bf` and the reset stub in `f64e9df2`. It contains no completed rule
that resolves the counter questions below.

## Inferred server rules for #450

The native search found array initialization and load sites, but no damage
consumer for the rate array. The client evidence does not confirm the retail
server's counter lifetime, repeated-hit rule, target priority, or tail rate.

The server uses these explicit inferences from the requested target-count rule
and the authored plot graph:

- One cast remembers each distinct target and its assigned rate in `PlotState`.
- Marked direct damage effects assign rates in the current target traversal order.
- Child nodes share the same state. Another cast has separate state.
- Repeated hits on the same target keep its assigned rate.
- The authored reset effect clears that cast's target map.
- Targets after number 10 use the final authored rate of 10 percent.
- Unmarked events and non-damage effects neither change nor use the map.

The issue request authorized target-count diminishing. These choices preserve
that behavior through the authored fan-out and reset paths. They are not a
claim of confirmed retail server behavior. No new target sort or custom rate
is introduced.

`EffectSource` carries the multiplier for that effect call. `DamageEffect`
applies it before integer damage, absorbed damage, and life-steal calculations.
Damage events use the reduced damage. Ordinary skills, buff ticks, and proc
effects keep the default multiplier of 1 unless a marked plot event supplies it.

## Confirmed breaker direction and inferred application order

The client loader at `395d17b0` reads
`SELECT buff_id, buff_tag_id FROM buff_breakers`. It finds the bound buff and
appends the tag to that buff's vector at `+0x204`. This confirms the direction:
the bound buff names the tags that break it.

The compact contains 661 rows, 92 bound buffs, and 14 distinct breaker tags.
For example, glider buff 2098 and song buff 656 both list fear tag 12 and
knockdown tag 107. Fear buff 156 carries tag 12 in `tagged_buffs`.

The server indexes those rows by incoming tag. After an incoming buff passes
the current stack and refresh checks, `Buffs.AddBuff` ends every active bound
buff before it starts or refreshes the incoming buff. This ordering is a
server inference. The native loader does not prove callback ordering.

The change uses the normal `Buff.Exit` path, including timeout and dispel
callbacks, modifier removal, dependent buffs, and the glider landing path.
It does not add a new skill-interruption rule. The same bound buff exits only
once when several incoming tags match it. Each active stack can end.

A missed, immune, or rejected effect never reaches accepted buff addition.
A shorter rejected refresh does not end another buff. Adding a bound buff
does not remove an earlier breaker buff. The relation is one-way.

Paid skills keep the current labor transaction. The detached buff preview
removes matching buffs and records their IDs for persistence. It does not
run live callbacks. A successful commit runs the normal additions and exits.
A failed commit keeps the live buffs and labor unchanged.

## Automated checks

The focused tests cover authored rates through 12 targets, per-target child
calls, repeated targets, reset isolation, concurrent casts, unmarked effects,
cancellation, reduced damage, life steal, and damage events.

Breaker tests cover fear against a glider and a song, multiple stacks, the
one-way relation, rejected refresh, immunity, loader reload, detached preview,
and both successful and failed labor commits.

The Release build passed with 0 errors and 76 warnings. The warnings include
unavailable NuGet vulnerability data and current repository warnings. No warning
names a changed source or test file. The 7 focused test classes passed all
93 tests, with 0 failures and 0 skips. These include 16 new test cases.
The focused classes are `AoeDiminishingTests`, `BuffBreakerTests`,
`SkillLaborTests`, `BuffTests`, `PlotTreeTests`, `PlotTargetInfoTests`, and
`BuffGameDataTests`. The release owner runs the integrated suite separately.

Commands from the source root:

```sh
dotnet build AAEmu.UnitTests/AAEmu.UnitTests.csproj -c Release -m:1 -p:UseSharedCompilation=false
dotnet AAEmu.UnitTests/bin/Release/net10.0/AAEmu.UnitTests.dll --treenode-filter '/*/*/AoeDiminishingTests/*'
dotnet AAEmu.UnitTests/bin/Release/net10.0/AAEmu.UnitTests.dll --treenode-filter '/*/*/BuffBreakerTests/*'
dotnet AAEmu.UnitTests/bin/Release/net10.0/AAEmu.UnitTests.dll --treenode-filter '/*/*/SkillLaborTests/*'
```

## Pending human checks for HUMAN VALIDATION #573

These checks need the published server release and the r208022 client.
Automated tests do not complete these items.

- [ ] Use Missile Rain, skill 13281, against one NPC. Check that repeated hits within one cast do not progressively lose damage.
- [ ] Use Whirlwind Slash, skill 13282, against one NPC, then against a controlled group. Compare damage with defenses and critical hits accounted for.
- [ ] Use Searing Rain, skill 11939, against several NPCs. Check that repeated waves start fresh diminishing groups instead of progressively losing damage.
- [ ] With a second player or suitable NPC, receive fear during glider flight. Check that flight ends and the fear remains.
- [ ] Receive crowd control during a bard song. Check that the affected song stage ends and does not continue its bonuses.
- [ ] Receive an immune or resisted crowd-control effect. Check that it does not end the glider or song.

The AoE checks need access to the named skills. Use the normal ability system
or GM test access. Group checks need several NPCs with the same level and
defenses. A GM test setup can provide them. Capture server damage events when
combat text cannot separate targets or waves. Fear and song checks need a
second player or an NPC with the correct crowd control. Exact retail server
parity remains unconfirmed.
