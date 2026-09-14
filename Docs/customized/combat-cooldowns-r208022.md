# Combat cooldowns for r208022

This change resolves aaemu-cluster issues #444 and #449. The research started
from source commit `2ef0c9140bf847de83b850375b522993139c82ee`.

## Exact client evidence

`client/history.txt` states `version 208022`.

| Input | SHA-256 |
| --- | --- |
| Original x2game.dll | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| Dumped x2game.dll | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| Original CryNetwork.dll | `2e98d25290dd86bffeb48699ec84a9c8d219494ca47ed3fa264f508bf63f0438` |
| Dumped CryNetwork.dll | `5115de707d457a85bd5ea95d78b6e069cba9d1c3f7b7b82dd41c8188995dbe11` |
| Server compact | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |
| Client compact | `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4` |

The source and dumped x2game DLLs have the same PE identity. The preferred
image base is `0x38FF0000`, and the timestamp is `2014-10-14 00:44:21`.
The entry RVA is `0x008C5D6D`, and the image size is `0x01CB0A00`.
The earlier dump command remains unknown. This change reuses the exact dump
from the packet research skill's cooldown example.

The ignored evidence directory is
`.tools-re/combat-20260913/cooldowns` in the cluster workspace.
It contains binary identities, compact query results, native disassembly,
and test logs. A reproducible disassembly command is:

```sh
objdump -d -M intel --start-address=0x39899ce0 --stop-address=0x3989a480 \
  .tools-re/dumps/x2game.dumped.dll
```

## Confirmed native rules

- `0x3956e842` maps `global_cooldown_mul` to attribute `0x4A`, or `74`.
- `0x398a0bc0` calculates the attribute and calls the clamp at `0x395729f0`.
- Both compacts set attribute `74` to the range `[-800, 4000]`.
- `0x39899f30` starts the default GCD with `1000000 / (bonus + 1000)` milliseconds.
- `0x39899df0` scales a supplied duration with `duration * 1000 / (bonus + 1000)`.
- Both functions guard the denominator with a minimum bonus of `-999` after the authored clamp.
- The caller at `0x3939aaa1` selects the default timer or a positive custom timer.
- `0x3989a400` checks a positive remaining timer. It honors the skill's ignore-GCD flag at template offset `0xDC`.
- `0x39899f90` writes either a cooldown tag or a skill ID. A tagged skill uses the tag bucket.
- This writer keeps the longer remaining timer. A shorter second timer does not reduce it.
- `0x3989a290` checks all tag memberships of the skill, then its individual skill timer.
- `0x391d6495` resets the requested skill and tag independently. Its boolean also clears the GCD.

The compact has 39 skills whose `cooldown_tag_id` lacks a matching
`tagged_skills` row. The server checks the declared cooldown tag as well as
the authored tag memberships. This follows issue #449's shared-tag requirement
without a compact change.

## Confirmed packet contract

`SCCooldownsPacket` uses server-to-client opcode `0x4A` at level `1`.
All fields are little-endian `u32`. Durations use milliseconds.

```text
skillCount
  skillId, totalDurationMs, remainingMs
  ...
tagCount
  cooldownTagId, totalDurationMs, remainingMs
  ...
```

Each group has a native maximum of 150 entries. There is no charge group.
Parser `0x397B2350` and import function `0x39899D40` confirm both groups.
The native object allocates `0xE24` bytes. This contains a 12-byte object prefix
and two `4 + 150 * 12` byte groups.
The object prefix is not part of the wire body.
Tick `0x39899E50` reduces remaining time. Getter `0x3989A290` keeps total duration separate.

The writer takes one timestamp for both buckets. It removes expired entries,
sorts IDs, and limits each bucket before serialization.
The current instance-completion path sends the snapshot after client readiness.
The earlier packet research confirms the parser and consumer. The visible
reconnect result still needs a manual client test.

## Server behavior

The server checks skill timers, shared timers, and the GCD before cast side effects.
A second check under `GcdLock` reserves the GCD after target and mana validation.
Buff cancellation, authored unmount actions, and plot dispatch follow that reservation.
The delayed cast task does not restart an already reserved GCD.
An ignore-GCD skill cannot clear another timer with a zero custom duration.
Internal linked casts can bypass the GCD, but they still check shared timers.
NPC skill selection through the ID-only API also checks authored cooldown tags.

The GCD getter clamps the raw bonus before conversion. It preserves fractional
multipliers instead of rounding the multiplier to a whole percentage.
Normal casts, cooldown special effects, and plot completion write actual modified
durations. A tagged cast writes the tag bucket.
Reset effects and character cooldown commands change server state before the response.
The reset response retains the current native packet body.
GM ignore-cooldown cleanup sends the authored tag ID after the server clears that tag.
A skill-only reset with tag ID `0` preserves unrelated shared timers.

The server admission allowance is at most 50 milliseconds and at most 5 percent
of the actual duration. This is an explicit server policy. The client evidence
does not establish a server latency allowance.
A 1000-millisecond GCD admits the next cast at 950 milliseconds.
A 200-millisecond GCD admits the next cast at 190 milliseconds.
The old interval helper and its unrelated short throttle do not authorize casts.

## Persistence and migration

`2026-09-13_aaemu_game_character_cooldown_tags.sql` adds an independent table:

```text
character_cooldown_tags
  character_id  INT UNSIGNED
  tag_id        INT UNSIGNED
  duration_ms   INT UNSIGNED
  expires_at    DATETIME(3)
  PRIMARY KEY (character_id, tag_id)
```

The base schema contains the same table. The Game updater creates it before
character state loads. This change does not alter either compact.

The character transaction saves both buckets under the current cooldown savepoint.
A failed save restores both buckets in SQL. Expired entries do not remain in SQL.
Old skill rows with an authored cooldown tag become shared timers on load.
When several old rows share a tag, the latest expiry wins with its original duration.
An old zero-duration row uses its remaining duration, as the earlier migration specified.
The next save writes the shared row and removes the old individual rows.

## Automated checks

The focused tests cover:

- A second cast at 200 milliseconds, expiry boundaries, and concurrent GCD admission.
- Default and custom durations, raw bonus caps, and the allowance for short durations.
- Ignore-GCD and internal bypass behavior with an active shared timer.
- Real `Skill.Use` rejection before mana or plot execution.
- Two potion grades with tag `30`, full tag membership, reset isolation, and timer extension.
- Plot completion, interrupted plots, and modified shared durations.
- Empty, 1-entry, 150-entry, and 151-entry packet inputs for both groups.
- Stable ordering, exact tuple fields, a single timestamp, maximum duration, and no trailing bytes.
- MySQL save/load, legacy rows, expiry cleanup, and the idempotent migration.
- Outer transaction rollback and an injected shared-write failure that preserves both saved buckets.

The full solution Release build passed. All 354 Game MySQL tests passed with
0 skips. This includes the 7 cooldown persistence tests. The suite took
44.048 seconds and removed its random disposable database. A read-only check
found no `aaemu_game_test_*` schemas after the run.

The test command used the compiled assembly, without a build:

```sh
AAEMU_GAME_TEST_MYSQL_CONNECTION='<approved loopback admin connection without a database name>' \
  /home/kegan/archeage/.tools/dotnet/dotnet \
  AAEmu.IntegrationTests/bin/Release/net10.0/AAEmu.IntegrationTests.dll \
  --filter-trait Category=GameMySql
```

The ignored log is `cooldowns/mysql-full.log` under the evidence directory.
The final unit test result follows the complete batch check.

## Manual r208022 check

1. Cast two ordinary skills less than 200 milliseconds apart.
2. Make sure that the second cast fails without mana or effect changes.
3. Use a potion with tag `30`, then try a different grade.
4. Make sure that both potion buttons show the shared timer.
5. Reconnect before the timer ends and repeat the second potion attempt.
6. Make sure that the original total duration and remaining time remain correct.
7. Test one skill reset, one shared-tag reset, and one GCD reset.
8. Repeat the timer check after an instance change.

Human gameplay validation remains open. No live database operation was part
of this implementation work.
