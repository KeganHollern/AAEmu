# Doodad currency rewards

[Cluster issue 310](https://github.com/KeganHollern/aaemu-cluster/issues/310)
covers three fish-sale functions and nine currency loot functions in the retained
r208022 compact data. Their reward must change the wallet once. A rejected reward
must not advance the loot phase or count toward loot achievements. Fish payment
and the removal of its equipped pack must commit before their callbacks run.

## Authored reward rows

The client and server snapshots contain identical rows in
`doodad_func_buy_fishes` and `doodad_func_loot_items`:

| Function | ID | Reward source | Minimum | Maximum | Chance |
| --- | --- | --- | --- | --- | --- |
| BuyFish | 1 | Equipped pack's `items.refund` | — | — | — |
| BuyFish | 4 | Equipped pack's `items.refund` | — | — | — |
| BuyFish | 6 | Equipped pack's `items.refund` | — | — | — |
| LootItem | 380 | Currency item 500 | 1 | 10 | 10,000 |
| LootItem | 556 | Currency item 500 | 10 | 50 | 7,000 |
| LootItem | 738 | Currency item 500 | 1,000 | 10,000 | 10,000 |
| LootItem | 750 | Currency item 500 | 100 | 1,000 | 10,000 |
| LootItem | 1017 | Currency item 500 | 1 | 10 | 10,000 |
| LootItem | 1819 | Currency item 500 | 100 | 300 | 10,000 |
| LootItem | 2412 | Currency item 500 | 100 | 1,000 | 10,000 |
| LootItem | 2414 | Currency item 500 | 100 | 1,000 | 10,000 |
| LootItem | 2415 | Currency item 500 | 100 | 1,000 | 10,000 |

The three BuyFish rows have null `item_id`; that field does not supply a fixed
reward. For a concrete refund fixture, equipped fish-pack item 27457 has refund
150,000 and maximum stack count 1. The tests run all three function IDs with
the real exchange path and their authored doodad/skill associations:

| BuyFish row | Doodad template | Skill |
| --- | --- | --- |
| 1 | 6507 | 21904 |
| 4 | 7133 | 21904 |
| 6 | 1713 | 13789 |

Fish removal and its checked refund are prepared under the persistence lock.
The inventory mutation publishes packets and item callbacks after both changes
are ready. If removal or the refund cannot be prepared, disposal restores the
same pack and its slot, count, owner, flags, expiration, and dirty state before
an error is sent. The doodad's displayed item changes only after successful
preparation.

## Loot boundaries

`count_min` and `count_max` are inclusive. A zero count grants nothing and leaves
`ToNextPhase` false so a multi-item phase can consider its next loot function.
`percent` specifies successful outcomes out of 10,000: draw an integer from
0 through 9,999 and succeed when the draw is strictly less than `percent`.
Therefore 0 never succeeds, 10,000 always succeeds, and 7,000 succeeds for
exactly 7,000 possible draws.

This boundary definition is derived from retained data and current server loot
behavior; it is not a claim of native-client verification. Both compacts contain
1,763 LootItem rows, including 53 positive-chance ranges from 0 to 1 and 1,350
positive fixed counts where minimum equals maximum. The current general
`LootPack.GeneratePackNewV2` likewise uses inclusive counts and a strict
less-than chance threshold.

Three authored 0-to-1 rows connect to doodad functions and would never grant an
item with an exclusive upper bound:

| Loot row | Function | Doodad template | Item | Chance |
| --- | --- | --- | --- | --- |
| 2763 | 12730 | 5964 | 25994 | 10,000 |
| 2764 | 12731 | 5963 | 25994 | 8,000 |
| 2765 | 12732 | 5962 | 25994 | 6,000 |

The old boundaries originate in `6a5fa7d838761bbc25f5ac43e1483a5f99e1f139`
(2019). The later random-library replacement
`ae0602f031b6af46d07a37451698f58037eecb2d` preserved those calls. The prior
integer random helper also had an exclusive maximum; it does not supply a
different earlier contract.

## Reproduction and validation

Source baseline: `a0b286fb76b9c7ee022b6c3dd8ff8a3654f8a965`.
Read-only compact identities used on 2026-09-07:

| Snapshot | SHA-256 |
| --- | --- |
| Client | `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4` |
| Server | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |

```sql
SELECT * FROM doodad_func_buy_fishes ORDER BY id;
SELECT f.actual_func_id, g.doodad_almighty_id, f.func_skill_id
FROM doodad_funcs AS f
JOIN doodad_func_groups AS g ON g.id = f.doodad_func_group_id
WHERE f.actual_func_type = 'DoodadFuncBuyFish'
ORDER BY f.actual_func_id;
SELECT * FROM doodad_func_loot_items WHERE item_id = 500 ORDER BY id;
SELECT COUNT(*) AS total,
       SUM(count_min = 0 AND count_max = 1 AND percent > 0) AS zero_one,
       SUM(count_min = count_max AND count_min > 0) AS fixed_positive,
       MIN(percent) AS minimum_chance, MAX(percent) AS maximum_chance
FROM doodad_func_loot_items;
```

`DoodadFuncLootItemTests` calls the public reward path with controlled random
draws for every currency row. It checks both count endpoints, the exact chance
boundary, one encoded money change, achievement progress, repeated actions,
the last representable wallet value, rejected overflow, concurrent credits,
zero counts, and an inclusive `Int32.MaxValue` count.

`DoodadFuncBuyFishTests` exercises all three authored function/doodad/skill
associations, including an exact wallet limit, overflow, and repeated sales.
It checks the encoded pack removal and one refund, restoration of the original
pack including its remaining lifetime on failure, rejected removal, reentrant
callbacks, and concurrent requests.
Both success packets and item callbacks must observe the complete exchange;
failure packets must observe the restored state.

Gameplay confirmation after publication should check a fish sale removes one
equipped pack and adds one displayed refund, and a currency loot action reports
one money change. That gameplay check does not replace the deterministic tests
for maximum amounts and rare chance boundaries.
