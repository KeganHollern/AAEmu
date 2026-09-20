# Mining phase notifications

This change addresses [cluster issue #567](https://github.com/KeganHollern/aaemu-cluster/issues/567).
The source base is `edd8031f2b8e85809b5f45c49b5d1c57e8f43b50`.

The user received ore and lost labor, but the iron vein disappeared without its break effect or depleted model.
Both r208022 compact databases give iron vein `1671` the intermediate phase `3150`, with prefab `mineral_series.fin`.
That client prefab contains rubble mesh `m04.cgf` and particle effect `X2_EFFECTS.doodad_effect.copper_broken_04`.

The mining interaction advances from `3054` to `3150`, then automatically collects loot and advances through `17069`.
The common branch ends at `17070`, which schedules removal after 3000 milliseconds.
Economy commit `4f058388` deferred phase notifications until the labor transaction committed.
Those callbacks read the final mutable doodad state, so the server could omit phase `3150`.
The packet also read mutable fields when it serialized.

The server now captures each phase packet before it queues the callback.
The packet keeps the object ID, phase ID, time left, and item template ID from that point.
The wire fields and opcode remain unchanged.
Callbacks retain their order and run only after a successful transaction.
World sensors and scripts still observe the current committed doodad state.
The server does not replay historical world states.

The regressions use the production mining functions and a deterministic common end branch.
They check phase `3150` before `17070`, labor and loot results, failure recovery, and notification order before deletion.
A packet test checks every body field and confirms that later doodad changes do not alter the packet.
Before the fix, the focused run failed both the intermediate-phase and packet-snapshot checks.
The other 31 tests passed.

This change does not modify SQL, compact data, client content, loot rates, or removal delays.
After deployment, mine an Iron Ore vein and check the break effect, depleted model, ore reward, and labor charge.
The manual client check remains pending.
