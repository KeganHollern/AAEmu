# Purchase contracts for r208022

This record supports cluster issues #85, #316, #317, and #475.
All addresses are virtual addresses in the mapped `x2game.dll` at base `0x38ff0000`.
The client version is 208022. Both PE headers have timestamp `2014-10-14 00:44:21 UTC`.
The source DLL SHA-256 is `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205`.
The mapped DLL SHA-256 is `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0`.
The mapped dump method is not in the local record. Matching PE identity and native pointer references support the address base.
The server compact SHA-256 is `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.

## Skill purchases

`GetAbilityChangeCost` binds at `0x3943d019` to `0x3943c460`.
It calls `0x398a19d0`, which multiplies constant-table entry `0x27` by the player level.
The constant is 200 copper. The same player byte at offset `0x4b4` supplies `UnitLevel` and `GetLevel`.
The cost at level 50 is 10,000 copper.
`SwapAbility` at `0x394386e0` resolves the current target and checks the NPC ability changer field.
The swap path at `0x39386600` needs 3 selected abilities. The old ability must be selected. The new ability must not be selected.
The server keeps free initial choices at levels 5 and 10.

`GetResetSkillsCost` binds to `0x39438d60`.
The cost function at `0x398997b0` counts learned active entries and passive entries for the selected ability.
It multiplies the count by constant-table entry `0x26`, which is 1,000 copper.
The count does not use skill-point weights. The reset path does not need an NPC.

| Packet | Serializer | Body |
| --- | --- | --- |
| `CSResetSkills`, `0x94`, level 1 | `0x397ba160` | `u8 ability`, `bool ausp` |
| `CSSwapAbility`, `0x96`, level 1 | `0x397ba1d0` | `u24 bc`, `u8 old`, `u8 new`, `bool auap` |

The Boolean fields are not prices. The server calculates the coin charge from its state.
The client has optional AA Point settings. The server does not provide an AA Point balance or a fallback purchase route.
The tests cover strict packet lengths, invalid flags, exact funds, no funds, initial slots, and invalid service use.

## The unused ICS opcode

The exact-client registration function spans `0x391eb9d0` to `0x391ed864` and registers 428 handlers.
Each registration writes a handler into the slot at `4 + 4 * opcode`.
There is no registration for `0x1d5`, whose slot is `0x758`.

| Opcode | Registration | Slot | Meaning |
| --- | --- | --- | --- |
| `0x1d4` | `0x391cb9f0` | `0x754` | `OnInGameCashShopSyncGood` |
| `0x1d6` | `0x391cba90` | `0x75c` | `OnInGameCashShopCashPoint` |
| `0x1d7` | `0x391cd4b0` | `0x760` | Exchange ratio |
| `0x1d8` | `0x391cd550` | `0x764` | Premium service list |

The packet factories at `0x391b6550` and `0x391b65d0` independently identify `0x1d4` and `0x1d6`.
There is no intervening factory or handler for `0x1d5`.
UI strings such as `GetBuyPerAccount` do not prove that a missing packet exists.
The server uses the supported per-viewer sold-out mask for account purchase limits.
It must not send a guessed `0x1d5` packet.

## Premium purchases

Issue #316 permits an explicit disabled service. The server does not advertise the premium purchase feature.
It returns an empty catalog and the defined `PremiumServiceBuyFail` error for a direct purchase request.
Account patron benefits remain separate from this disabled product route.

The list parser at `0x397c49b0` reads `isEnd`, `size`, one product structure, and `exchangeRatio`.
It reads the product structure even when `size` is zero.
The structure parser at `0x397bf920` reads all current `PremiumDetail` fields.
The empty response includes this zero structure. It does not include a placeholder product.

## Priest purchases

The client binds `BuyPriestBuff` to `0x3949ac20`.
It selects a row from the priest list, checks the current NPC priest field, and multiplies the row cost by the player level.
`CSBuyPriestBuff`, opcode `0xb2`, level 1, has a 7-byte body.
Its serializer at `0x397cb080` writes a `u32` priest row ID and a fixed `u24` NPC object ID.
The packet does not contain a price.

The server compact row is `priest_buffs(1, buff_id=239, cost=100, position=1)`.
Buff 239 lasts 1,800,000 milliseconds and has `resurrection_health=10`, `resurrection_mana=10`, and `resurrection_percent=true`.
It has `remove_on_death=true` and `save_rule_id=1`.
Its text describes one in-place resurrection with 10 percent health and mana, plus recovery of the experience lost on that death.
The server captures this offer before death removes the buff.
A server skill or the free low-level offer uses the same one-use state.
A client cannot request in-place resurrection without a current server offer.
The resurrection packet uses the offer location, not a temple location.

## Human checks

1. Open an ability changer service at levels 10, 50, and 55.
2. Check the displayed coin charge against the balance change.
3. Reset a selected tree from the skill window without an NPC.
4. Check that no funds prevent both the reset and the swap.
5. Check that premium purchases are absent from the client service menu.

These focused client checks need the published server. Native research and automated tests do not replace them.
