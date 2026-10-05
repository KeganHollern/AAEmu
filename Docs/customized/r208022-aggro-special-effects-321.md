# r208022 aggro and NPC removal effects — issue #321

## Scope and evidence

This change covers `AggroCopy`, `AggroReset`, `LoseTarget`, and `NpcDespawn`.
It does not add a dungeon encounter or change a compact database.

The client revision is `208022`. The research used these exact inputs:

| Input | Size | SHA-256 |
| --- | ---: | --- |
| `client/bin32/x2game.dll` | 18483712 | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| `x2game.dumped.dll` | 30085120 | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| `compact/client.sqlite3` | 126415872 | `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4` |

The native dump uses image base `0x38ff0000`. This work reused the prior
r208022 dump. It did not repeat the dump process.
Both files have PE timestamp `0x543cb835`, entry point `0x008c5d6d`,
and image size `0x01cb0a00`. These fields match the prior NPC group research.
The task record contains the exact queries, native exports, and test logs.

## Compact callers

| Effect | Confirmed compact data | Meaning and limit |
| --- | --- | --- |
| `AggroCopy` | Type `104`, row `15867`, effect `36868`, skill `25100`. All 4 values are `0`. | The Korean skill description says allies in the forward area copy aggro and enter combat. The skill uses a 10-metre area, a 60-degree angle, and friendly effect targets. The server copies the caster NPC threat to each selected ally. |
| `AggroReset` | Type `105`, rows `14132`, `14133`, `14135`, effects `34351`, `34352`, `34354`, skills `23700`, `23710`, `23763`. Values are `(0, 1, 0, 0)`. | All 3 callers are NPC self skills. The compact does not name the 4 arguments. The server uses the user-approved custom reset rule below. Exact retail argument semantics remain unknown. |
| `LoseTarget` | Type `8`, row `8405`, effect `23862`, buff `4470`, trigger event `6`. All values are `0`. | The trigger applies to the buff owner. The handler clears that unit's selected target. It does not clear its threat table. |
| `NpcDespawn` | Type `109`, skill `24115`, buffs `6980`, `7337`, `7345`, `8189`, `8297`, `8342`, `11009`. All values are `0`. | The descriptions specify removal after a lifetime, replacement, or explosion. These callers do not request death rewards. |

The other compact rows for these special types have no current `effects`
reference. The audit does not treat those rows as live skill callers.

## Target-clear packet

The exact client confirms the existing `SCTargetChangedPacket` contract:

| Property | Confirmed value |
| --- | --- |
| Direction and level | G2C, level `1` |
| Opcode | `0x84` |
| Field 1 | Unit object ID, unsigned 3-byte `Bc` |
| Field 2 | Selected target object ID, unsigned 3-byte `Bc` |
| Empty target | `0` |
| Body length | `6` bytes, no trailing group |

Factory `0x391aae30` embeds opcode `0x84` and creates a `0x14`-byte object.
Parser `0x397c61f0` reads the unit and target fields at object offsets `0x0c`
and `0x10`. Both use `0x397b27d0`, which requests a 3-byte integer from
stream slot `0xcc`. The parser vtable is `0x399b3378`.
Registration `0x391be9e0`, at call site `0x391ec062`, selects handler
`0x391d5270`. The dispatch vtable is `0x399b4da0`.
The handler calls `0x394270a0` with the same 2 identifiers.
The consumer stores the selected target at unit offset `0x1aa0` and updates
the local player's target UI. A missing target selects the clear-target path.

The server already uses this packet for target changes and removed objects.
This change adds a send site. It does not add or change a packet contract.

## Server state and lifecycle

On 2026-10-04, Kegan approved this explicit server rule for `AggroReset`:
clear the affected NPC's own threat list and selected target, then use its
normal return behavior. This rule applies to the exact `(0, 1, 0, 0)` argument
set in the 3 active callers. Other argument sets do not infer another rule.
The client proves the stored values and caller targets, but not their retail
server interpretation. [Issue #634](https://github.com/KeganHollern/aaemu-cluster/issues/634) tracks
that retail research at low priority in the backlog. This implementation does not claim retail parity for the reset.


`AggroCopy` copies resolved damage and healing threat. The copy does not apply
threat modifiers again. It does not multiply healing threat by `0.6` again.
It preserves unrelated recipient entries and replaces the copied entries.
This merge rule is an inference from the ally-assistance skill description.
A manual test must check the final gameplay behavior.

The normal threat path owns healing and death subscriptions and reverse player
links. A copied entry does not grant damage tags, quest credit, or loot credit.
Shared-threat groups also receive the imported amounts in their group state.
A later damage or healing event therefore continues from the imported amount.
Dead, retired, unregistered, or protected participants cannot receive the copy.

`Unit.RemoveIncomingThreat` removes the exact unit from same-world NPC threat.
It clears only NPC selections that refer to that unit. Fake death can use this
helper without a death event or a change to hit points.
The cleanup also corrects an earlier defect in `ClearAggroOfUnit`: the empty
combat check used the removed target instead of the NPC that owned the table.

`NpcDespawn` uses the current spawner's respawn and group bookkeeping.
It then uses `SpawnManager.DespawnObject` for immediate removal, event cleanup,
visibility removal, and object-ID release. It preserves an already queued
replacement time. Repeated calls do not release the same object ID again.
NPCs without a spawner use the same removal path without a replacement.
The handler does not call `DoDie` or create death, experience, loot, or quest credit.
Group replacement still follows the user-approved rules for issue #527.
`DispelTask` stops when its NPC owner is retired or despawned. A pending
replacement can retain the removed NPC object. The old buff must not tick,
run a timeout effect, or schedule another task during that wait. This guard
does not force an early timeout or remove a buff from a live NPC.

## Automated checks

The focused tests cover exact copied amounts, modifiers, subscriptions, reverse
links, shared group continuity, unavailable targets, selected-target cleanup,
and the incorrect threat-owner regression. Packet tests consume both `Bc`
fields and check that no body bytes remain. NPC removal tests check immediate
removal, preserved hit points, no death event, single object-ID release,
already queued replacement time, and partial/whole group replacement rules.
The retirement tests also check that a live NPC still runs its timeout effect,
while a retired or despawned NPC does not run it.

Run the focused tests with the repository .NET SDK:

```sh
dotnet build AAEmu.UnitTests/AAEmu.UnitTests.csproj --no-restore
dotnet run --no-build --project AAEmu.UnitTests -- --treenode-filter '/*/*/AggroSpecialEffectsTests/*' --no-progress
dotnet run --no-build --project AAEmu.UnitTests -- --treenode-filter '/*/*/NpcGroupSpawnTests/EffectRemoval*' --no-progress
```

## Human checks

These checks need the published server release and the r208022 client.
Record the results in the single `HUMAN VALIDATION` issue #573.
Automated tests do not complete these checks.

- Select a target, then apply buff `4470` through the normal test controls.
  After 2 seconds, check that the target frame clears.
- In a controlled NPC test, trigger skill `25100` near an eligible ally.
  Check that the ally attacks the same hostile targets. Check that repeat
  copies do not create repeated credit or change the caster's threat.
- In a controlled combat test, trigger an `AggroReset` self skill such as
  `23700` on the NPC. Check that it clears its own threat and selected target.
  Check that its normal return behavior starts. This is the approved custom rule.
- Trigger a lifetime-removal buff such as `6980` on a controlled NPC.
  Check visible removal after 120 seconds and no kill reward.
  Check the normal replacement delay when the spawner permits replacement.
- Check a group occurrence with partial replacement enabled and disabled.
  Check that removal preserves the same rules as ordinary group removal.

These checks test general mechanics. They do not claim a complete dungeon
encounter or a retail server trace.
