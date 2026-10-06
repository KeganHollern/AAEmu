# Doodad shops, issue #646

## Result

The r208022 client opens a doodad shop locally. The trace-only server handler
does not prevent that window from opening. Purchases failed because the server
used the packet's third field as a merchant pack ID. The normal client writes
zero to that field. The old server also rejected packs 164 and 192 on doodads,
although StoreUi functions use those Vocation and Honor packs.

`CSBuyItemsPacket` now gets the catalog from the current `DoodadFuncStoreUi`
template. It checks the function permission and the live object before a
purchase. A client field cannot select another catalog. The standard catalog
validator still controls item membership, grade, currency, price, and quantity.
The purchase lock now covers these checks and the transaction. This lock is
also `SaveManager.PersistenceSyncRoot`, which protects doodad phase changes.

No new packet or server interaction session is needed. `DoodadFuncStoreUi.Use`
keeps `ToNextPhase` false if a server skill path calls it. The authored
`next_phase=-1` does not remove the shop. This change does not add SQL updates,
compact changes, or client patches.

## Exact-client evidence

The research used the deployed source baseline
`1b35411e93185946a4757c29cba8748d7d626838` and client revision 208022.

| Artifact | SHA-256 |
| --- | --- |
| Original `bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| Local native dump, `x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| `scriptsbin/x2ui/store/store.alb` | `4b9a980553ed710b7f2b2de1f57686f8f7133797bd755f8f59222043f2465172` |
| `scriptsbin/x2ui/npc_interaction.alb` | `e24a28723d88749f9e0e801067947f2194079ead57bc43e46805f033d257a800` |

The original DLL and dump have the same PE timestamp `543cb835`, image base
`38ff0000`, entry RVA `008c5d6d`, and image size `01cb0a00`.
The following addresses are virtual addresses in that dump.

1. The schema loader at `39673b90` maps `DoodadFuncStoreUi` to native type `0x57`.
2. The local dispatcher at `393bc260` checks the function permission and calls
   `393b8cc0` for type `0x57`.
3. `393b8cc0` records interaction kind `0x11` and the doodad object ID. It raises
   event `0x6b` with `"store"`, then returns 1.
4. The activation function at `393b3ec0` sends the normal server skill only when
   this local dispatcher returns 0. StoreUi therefore skips that skill path.
5. The X2UI `NPC_INTERACTION_START` handler maps `"store"` to `STORE_NPC`.
   `ToggleStore` then shows the store window.
6. `394fb390` resolves the current local store object. `398a9dd0` finds a StoreUi
   function and gets its merchant pack through `395ac1d0`.
7. `394fcc10` produces the purchase. `393b7de0` sets opcode `0xae`.
   `397ac3c0` writes the NPC ID, doodad ID, and the zero value at `3a5e1160`.
8. The serializer at `397bac90` writes those IDs, then the third uint under the
   field name `"type"`. It then writes purchase counts, entries, and `useAAPoint`.
   That field is not the authored StoreUi merchant pack ID.

These observations confirm the local open path and the catalog defect. They
do not define a new packet field or a new server session.

Upstream `AAEmu/AAEmu:develop` at `6e2f40734` still contains a TODO for doodad
purchase authorization. It does not contain a fix to port.

## Authored content and state

Both compact snapshots contain these StoreUi functions. All use skill 12087
and `next_phase=-1`.

| Function ID | Group ID | Merchant pack | Permission |
| --- | --- | --- | --- |
| 4 | 20212 | 171 | Any |
| 7 | 20214 | 164 | Any |
| 8 | 20215 | 192 | Any |
| 9 | 20218 | 164 | Any |
| 10 | 20219 | 192 | Any |
| 11 | 21823 | 274 | Any |
| 12 | 21933 | 145 | Any |
| 14 | 15916 | 164 | SameAccount |
| 15 | 15917 | 164 | SameAccount |

The direct group links resolve to doodad 7911, doodad 7961, and the Farmer's
Workstation, doodad 6090. The workstation uses the two SameAccount rows.
The other rows do not have a direct doodad link in this snapshot. Their
presence alone does not prove that an object spawns in the world.

The server checks `CurrentFuncs` at purchase time. A previous phase does not
authorize the purchase. `DoodadPermissionRules.Allows` applies the authored
permission, including account ownership for the workstation.

`Despawn > DateTime.MinValue` rejects a pending removal. This matches
`Doodad.Use` and `Doodad.DoFunc`. Doodad assignments set this field during
vehicle removal or housing furniture removal, not ordinary furniture lifetime.
The world lookup must also return the same live object in the same instance.

## Distance limit

The server keeps its existing 3-metre shop limit and now measures all 3 axes.
This prevents purchases through another floor. It does not add a 4-metre range
from the skill metadata.

The client uses `max_interaction_doodad_distance` for both shop opening and
purchases. The registration at `390fa0a1` reads `2.1f` from `399a7660` into
config offset `0x2a0`. The open check is at `393b3ec0`; the purchase check is at
`394fb490`. Both call `39093800` through engine position helpers. The server's
3-metre center check remains an approximation. This change does not claim that
it reproduces the full client geometry.

## Automated checks

`CSBuyItemsPacketTests` sends real packet bodies through the purchase handler.
It checks valid Money, Vocation, and Honor purchases with the native zero field
and no `CurrentInteractionObject`. It checks the authored grade and exact cost.
It also checks another character on the owner's account, a different account,
changed permission, missing current function, missing catalog, forged catalog
items, forged currency, range, vertical distance, pending removal, and stale
world state. NPC and remote Honor purchase checks preserve their normal paths.

Run the focused tests with the repository's .NET SDK:

```sh
dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj -- --treenode-filter '/*/*/CSBuyItemsPacketTests/*'
```

## Human checks for issue #573

These checks need the published server change. They do not need a client patch.
They still need human validation. Record the selected release with the results.

Prerequisites: use an owned Farmer's Workstation, enough Vocation points, and
free bag space. Use a character on the owner's account.

1. Stand beside the workstation and open its shop action.
2. Make sure the shop window shows the authored Vocation catalog.
3. Buy 1 listed item. Make sure the bag gains that item and the displayed cost leaves the wallet once.
4. Close the window and open it again. Make sure another purchase works.
5. Move away with the window open. Make sure the client closes it or prevents a purchase outside interaction range.
6. If another character exists on the same account, repeat the purchase with that character.

If a public StoreUi object such as doodad 7961 is available, also check its
Money purchase. A separate account can check the workstation permission denial.
Those optional prerequisites do not imply that a public shop or another
account is available for the solo check.
