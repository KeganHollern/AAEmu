# r208022 unit requirements

This record resolves the current requirement checks in
[cluster issue #458](https://github.com/KeganHollern/aaemu-cluster/issues/458).
The source base is `2a8b6371d8b25b40489ee5bd386b767715de9094`.
The audit date is 2026-10-08.

## Scope and decisions

| Kind | Decision |
| --- | --- |
| `CrimePoint` (44) | Compare the character's current crime points with the authored threshold. |
| `CrimeRecord` (46) | Compare the character's infamy points with the authored threshold. |
| `JuryPoint` (47) | Correct the same directional comparison used by jury qualification quests. |
| `VerdictOnly` (54) | Require nonzero jury points on the caster. The separate target buff requirement still applies. |
| `NotOnMovingPhysicalVehicle` (68) | Use the exact client's physical-vehicle and speed conditions. |
| `CanLearnCraft` (19) | Preserve the client's explicit success result. Do not invent permanent recipe learning. |
| Nation and Dominion ownership | Keep the ownership system in [#143](https://github.com/KeganHollern/aaemu-cluster/issues/143). |

The full bot arrest system remains in
[#147](https://github.com/KeganHollern/aaemu-cluster/issues/147).
The `VerdictOnly` permission check does not complete that system.
No compact, SQL, client-content, or persistent character-state change is needed.

## Input identity

| Input | SHA-256 |
| --- | --- |
| Packed `r208022` `x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| Runtime dump `x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| Server compact | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |
| Client compact | `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4` |

The PE timestamp is `0x543cb835`, image base `0x38ff0000`, and image size `0x01cb0a00`.
The dump is an earlier local capture. This audit checked its identity and did not create a new capture.
Native addresses below refer to that dump.
Read-only compact queries and native disassembly support the findings.
No client session or human gameplay test formed part of this audit.

## Crime and jury rules

`Value1` selects the comparison. It is not the lower end of a range.
A zero value requires `points >= Value2`.
Every nonzero value requires `points <= Value2`.
Both comparisons include equality.

| Native function | Evidence |
| --- | --- |
| `3936caf0` | Main player requirement dispatcher. |
| `3936be60` | Crime points use signed short field `+0x61c` and the directional comparison. |
| `3936bf50` | Crime record uses signed integer field `+0x620` and the same comparison. |
| `39496420` | The client exposes field `+0x620` as `crimeRecord`. The server stores it as `InfamyPoint` / `crime_record`. |
| `3936bfb0` | Jury points use signed integer field `+0x660` and the same comparison. |
| `3936c110` | `VerdictOnly` requires field `+0x660 != 0`. It reads the actor, not the target. |
| `391de280`, `3930afd0` | `OnJuryPointChanged` calls the setter for field `+0x660`. |

The crime, infamy, and jury failures use skill result IDs 103, 106, and 107.
Their result value is the authored comparison selector, `Value1`.
`VerdictOnly` uses skill result ID 113.
A missing character cannot satisfy these character requirements.
The native CrimePoint helper uses result 104 if its local player singleton is absent.
The server instead uses its CrimePoint result 103 for a missing or non-character owner.
That server case does not represent a valid client cast.

The compact contains 21 `CrimeRecord` rows:

- 13 jury-related quest components use `(1, 9)`, so infamy must be at most 9.
- 8 prison quest components use `(0, 1)`, so infamy must be at least 1.

Jury quest components 21905 and 21908 also require `(1, 0)` for `JuryPoint`.
Only a character with no jury points can start those qualification paths.
Executioner components 22064 and 22070 require `(0, 6)`.
Those paths need at least 6 jury points.
The earlier server compared jury points only with `Value1`, which reversed these gates.

The single `CrimePoint` row belongs to AI event 755 and uses `(0, 9)`.
The current fork has no production caller for `GetAiEventRequirements`.
This change supplies the correct predicate but does not add an AI event system.

Skill 20323 has the single `VerdictOnly` row.
Its separate `TargetBuffTag` 943 requires Prime Suspect buff 4863.
That target check cannot replace the caster's jury permission.
The native rule requires nonzero points, not a broader court eligibility calculation.

## Legacy craft learning

All 9 `CanLearnCraft` owners are plot-only skills with ability ID 0.
Their IDs are 10097, 10099, 10100, 10120, 10121, 10122, 10123, 10603, and 10764.
A scan of every compact column with a skill reference found only their own `skill_effects` rows.
No item, craft, default skill, NPC interaction, or quest supplies those skills.
They call legacy `TrainCraftEffect` records.

The main native dispatcher `3936caf0` handles kind `0x13` by returning success directly.
It does not invoke the older yard helper `390f0a80`.
That older helper calls `397f2350`, which looks up static craft descriptors, not character recipe ownership.
The earlier [design audit](r208022-design-recipe-audit.md) also confirms that current merchant designs are consumable craft materials.
The request for a new learned-recipe state rests on an obsolete premise.

## Physical vehicles

The authored row is requirement 45139 on Drop Back, skill 12049.
Both authored values are zero.
The native path is `3936c4b0` through `39345a20` and `39345970`.
The speed check sums the squares of all 3 linear velocity components.
It rejects a qualifying physical vehicle only above 25 square metres per square second.
A speed of exactly 5 metres per second passes.
Angular velocity alone does not fail the rule.

The vehicle packet scale differs from the server's existing ship encoding.
The native movement decoder `397c0470`, type 2, multiplies each signed short by `1f / 32767f`, then by `30f`.
The server preserves that float operation order.
Raw speed components 5461 and 5462 fall below and above the 5-metre-per-second boundary.
The C2G packet constructor `39404670` selects serializer `397c52f0`.
It calls the shared movement codec and writer `397ad8c0`.
That writer divides by 30, clamps to `[-1, 1]`, multiplies by 32767, and rounds.
Assembly confirms the decoder constants and signed conversion.

Only accepted driver movement updates the stored velocity.
The update and requirement check use the current attachment lock.
A new driver binding starts with zero velocity, so it cannot inherit the previous driver's last speed.

The native slave manager selects the vehicle from the driver's `OnSlaveBound` state.
The server therefore checks the direct parent slave, the driver attachment point, and the matching driver occupant.
A deck passenger or a character attached to a child object does not meet that condition.
The vehicle model must set `use_wheeled_vehicle_simulation`.
The reviewed server compact has 9 such models among 63 vehicle models.
Ships, ordinary mounts, and nonphysical vehicles do not use this restriction.

The failure contains skill result byte `0x7f`, localized error `0x0308`, and unsigned integer value 0.
The server must preserve the localized error through skill start failure packets.
A generic failure loses the client's vehicle-specific explanation.

## Automated and human checks

The final release record lists test counts and the deployed source commit.
Manual checks belong to [HUMAN VALIDATION #573](https://github.com/KeganHollern/aaemu-cluster/issues/573).
They cover jury qualification, executioner qualification, prison tasks, and Drop Back on physical vehicles.
Service readiness and automated tests do not complete those human checks.

### Local validation

The Release solution build passed with 0 errors.
The Game script compiler passed with 0 errors and 0 warnings.
The unit suite passed 5,902 tests and skipped 20 optional asset checks.
All 543 MySQL tests, 50 Content Studio tests, and 16 quest audit tests passed.
The unit run used the reviewed server compact through `AAEMU_COMBAT_TEST_COMPACT`.
This included all 63 vehicle model flags, both sides of the encoded speed boundary, and exact skill failure packet bytes.
The SQL tests used the isolated local test server and removed their random test databases.
Independent review found no blocker after the native scale and fixture corrections.

Commands:

```sh
dotnet build --configuration Release --no-restore
dotnet run --configuration Release --no-build --project AAEmu.Game/AAEmu.Game.csproj compiler-check
dotnet test --project AAEmu.UnitTests --configuration Release --no-build --no-progress
dotnet test --project Tools/AAEmu.ContentStudio.Tests --configuration Release --no-build
python3 -B -m unittest discover -s Tools/tests -p test_quest_sphere_audit.py
dotnet test --project AAEmu.IntegrationTests --configuration Release --no-build -- --filter-trait Category=GameMySql
```
