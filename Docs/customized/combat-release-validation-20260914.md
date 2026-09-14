# Combat release validation

This release addresses cluster issues #325, #443, #444, #445, #449, #474, #480, and #481.
The source base is `2ef0c9140bf847de83b850375b522993139c82ee`.
The official base remains `b34db3e5f5bce0b9b9bac2a6916a936f0e946a1f`.

## Source evidence

- [Player skill grants and passives](combat-skill-authorization-r208022.md).
- [Mount ownership, equipment, and seats](combat-mount-authorization-r208022.md).
- [Caster states](combat-caster-states-r208022.md).
- [Priest prayer](combat-priest-prayer-r208022.md).
- [Global and shared cooldowns](combat-cooldowns-r208022.md).

Two source reviews checked the complete change after the corrections.
The reviews found no further blocker.
Tests found a text-boolean loader error in pet equipment data before publication.
The corrected loader passed the complete server compact check.
The auction test fixture also used an expired fixed date.
It now uses one current UTC snapshot per test instance and preserves all assertions.

## Local checks

| Check | Result |
| --- | --- |
| Complete Release solution build | Passed with 0 errors. |
| Runtime script compilation | Passed with 0 errors and 0 warnings. |
| Complete unit suite | 4,069 passed, 0 failed, 16 optional external-asset skips. |
| Game MySQL suite | 354 passed, 0 failed, 0 skipped. |
| ContentStudio suite | 50 passed. |
| Python quest-sphere audit | 16 passed. |
| Combat compact and seat checks | All passed with the current server compact and extracted client models. |
| Compact integrity | Both snapshots returned `ok` and retained their recorded hashes, sizes, and table counts. |

The unit suite used `AAEMU_COMBAT_TEST_COMPACT` and `AAEMU_COMBAT_TEST_MATE_ASSETS`.
The model extraction checked all 134 pet models and found every file.
The MySQL fixture used port 33306, created a random schema, and removed that schema after the tests.
Its 7 new cooldown tests include savepoint recovery after an injected shared-table write failure.

```sh
dotnet build AAEmu.slnx --configuration Release --no-restore -m:1 -p:UseSharedCompilation=false
dotnet run --configuration Release --no-build --project AAEmu.Game/AAEmu.Game.csproj compiler-check
dotnet test --project AAEmu.UnitTests --configuration Release --no-build
dotnet test --project AAEmu.IntegrationTests --configuration Release --no-build -- --filter-trait Category=GameMySql
dotnet test --project Tools/AAEmu.ContentStudio.Tests --configuration Release --no-build
python3 -m unittest discover -s Tools/tests -p 'test_quest_sphere_audit.py'
```

## SQL review

`2026-09-13_aaemu_game_character_cooldown_tags.sql` adds `aaemu_game.character_cooldown_tags`.
Its SHA-256 is `b9bc2b21352a68fac479f32e6e4c6e404057820c0e5561e29a667056859a245d`.
The base schema contains the same definition.
The live precondition check found the individual cooldown table and no tag table or migration ledger row.
The update adds 4 columns and a composite primary key. It does not delete data.
The normal application updater creates the table before character state loads.
The current Recreate strategy stops the old Game writer before the new pod starts.
This additive pre-release change needs no direct SQL action or destructive-data backup.
Post-release checks must find the exact table definition, a successful ledger row, and Game readiness.
The cluster source-selection PR records publication and live checks.

## Focused gameplay checks

Human gameplay validation follows publication.

1. Use normal attacks, class skills, quest items, harvest actions, and buff-granted skills.
2. Use priest prayer near a priest and check the normal labor cost.
3. Equip and remove pet gear. Check owner commands and passenger exit.
4. Enter pet and vehicle seats nearby, then try remote and cross-instance requests.
5. Use vehicle skills as the operator and test a stance change.
6. Test skill rejection during death, crowd control, silence, and pacifist protection.
7. Start two ordinary skills within 200 milliseconds and check rejection of the second skill.
8. Use two potion grades with the same cooldown tag, then reconnect before expiry.
9. Check the shared timer after an instance change and after a reset effect.

The automated tests cover forged skills, general passives, foreign mounts, and invalid equipment requests.
This record does not claim a new client capture or a completed human gameplay test.
