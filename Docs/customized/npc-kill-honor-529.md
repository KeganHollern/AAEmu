# NPC kill honor

This change resolves [aaemu-cluster#529](https://github.com/KeganHollern/aaemu-cluster/issues/529).

## Source and data

The review used source commit `474c2436276b352ca9edb5cada47b2cfd80940e8`.
The server compact SHA-256 was `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.
The review used read-only queries. This change does not change the compact or the SQL schema.

`NpcManager` loads `npcs.honor_point`, but the NPC death path did not use it.
The compact has 54 NPC templates with positive honor rewards.
Kraken 7607, Nisrok 11038, and Hanure 11180 each specify 100 honor points.

`unit_modifiers` supplies the NPC kill attributes 128 and 129.
The compact has no limits for these attributes and no dynamic modifiers for them.
ArcheLife buff 6442 and custom buff 8000007 each add 1 to attribute 128.
Test buff 6266 adds 100 to attribute 128. Test buff 6267 adds 100 to attribute 129.

## Reward rules

The NPC grants honor to its current tagged recipients.
Each eligible party or raid member receives the full reward. XP share factors do not reduce honor.
An untagged kill uses the killer's character owner, which includes a pet owner.
An absent tagged team keeps its claim. The final attacker does not receive that team's honor.
The quest tag-share list does not grant honor to other contributors.

A recipient must be online, in the same world instance, and within the current loot range.
The reward has no XP level penalty. This change does not change XP, loot, or quest recipient rules.
NPCs with no positive base honor reward still grant no honor.

The calculation follows the server's current flat-plus-percentage attribute convention:

```text
round(max(0, base honor + flat NPC bonus) * max(0, 100 + NPC percentage bonus) / 100 * World.HonorRate)
```

The normal bonus calculator applies static and dynamic bonuses to each attribute.
`World.HonorRate` applies after those bonuses, as its config description states.
The separate PvP rate does not apply. Invalid or nonpositive rates grant no honor.
The balance cannot exceed `int.MaxValue`.
The normal game-point update path sends the change to the client and retains the normal character save path.

The NPC takes the current death gate before rewards or script callbacks.
Repeated, concurrent, and reentrant death calls cannot repeat the rewards.
The current transition from zero HP to positive HP resets that gate for the next life.
Base death events, loot, and cleanup keep their current order.

## Automated checks

`NpcHonorTests` covers the Kraken reward, tag ownership, quest-only contributors, party and raid recipients,
distance, instance isolation, offline recipients, pet owners, repeated death, concurrent death, and another life.
It also covers flat and percentage bonuses, the server rate, invalid rates, zero base rewards, and balance overflow.
The Kraken fixture uses the reviewed compact values. It does not need a live boss encounter.

The Release build passed. All 61 focused tests passed, with no skips.
The focused check also includes the current unit death-event, NPC XP, NPC achievement, and quest team-share tests.
Automated checks do not prove an encounter script or a client display.

## Pending human checks

1. Use a controlled test spawn of Kraken 7607 with the default honor rate and no honor bonus.
2. Record the character's honor balance, then earn its kill credit and kill the NPC.
3. Check that the balance increases by 100 and remains correct after login.
4. Kill an ordinary NPC with zero base honor. Check that the balance does not change.

A separate party or raid check needs 2 characters near the same honor NPC.
Each eligible member must receive the full reward. A member outside the loot range must receive no honor.
Record these checks in HUMAN VALIDATION #573 after release.
