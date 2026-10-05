# Buff special effects for r208022 (#321)

This record covers `BuffSteal`, `RedeemBuff`, and `ExplodeBuff`.
The source baseline is `4268f2f9310405d1b4e6463f331f435fcf5a898c`.

## Exact client evidence

The client history reports `version 208022`.

| Input | SHA-256 |
| --- | --- |
| Original stock client compact | `784d362434a2a0fd0a29fbc1bbb7f771d9ffbe2c8568339ac54d6fdcf44d2ed7` |
| Current client compact | `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4` |
| Current server compact | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |
| Installed `bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| Research runtime `x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |

The 3 compacts contain the same 7 relevant special-effect rows.

| Special row | Type | Values 1-4 | Caller |
| --- | --- | --- | --- |
| 200 | BuffSteal (16) | 1, 0, 0, 0 | Leech 10104, effect 1102, 70% chance |
| 1899 | BuffSteal (16) | 1, 0, 0, 0 | Leech 10104, effect 7433, 50% synergy chance |
| 13928 | BuffSteal (16) | 1, 0, 0, 0 | Leech 23707, effect 34076, 100% chance |
| 763 | RedeemBuff (44) | 4000, 10, 15, 0 | Absorb Effect 11988, effect 3823, source application |
| 5009 | ExplodeBuff (46) | 1000, 20, 20, 0 | Focal Concussion 16410, effect 15588 |
| 9531 | ExplodeBuff (46) | 1000, 20, 20, 0 | Focal Concussion 20650, effect 27256 |
| 6411 | ExplodeBuff (46) | 2000, 50, 50, 0 | Buff 449, timeout trigger 2194, effect 20330 |

Confirmed localized descriptions:

- Leech interrupts the enemy spell, applies Sleep, and has a high chance to steal a buff.
- Absorb Effect consumes 1 self-buff, restores mana, and grants 1 Inspired stack.
- Focal Concussion cancels enemy buffs and applies Magic Damage. An enemy without buffs takes no damage.

Absorb Effect has a separate `BuffEffect` row for Inspired.
The handler must not apply that buff a second time.
The Leech skill-effect rows own their chance rolls.
The handler must not add another chance roll.

The native loader at `3972c390` reads the 5 integers into a 20-byte descriptor.
The field order is type, value1, value2, value3, value4.
The native tooltip route `3941f0d0` calls `3941b7c0` for damage placeholders.
That lookup accepts `DamageEffect` descriptors, not these special effects.
The tooltip therefore does not establish the missing numeric formulas.

## Buff theft

`BuffSteal` moves the authored count of eligible effects through the normal buff lifecycle.
Eligible effects are active, unexpired, beneficial, non-system buffs that are not passive.
The recipient must satisfy buff requirements and immunity checks.
The transferred buff keeps its remaining duration, ability level, and charges.
The recipient becomes its caster and owner.
The transfer also preserves `IsItemProc` and `ItemProcLevel` so ticks retain proc origin and level.
The new buff uses the thief's current duel context and does not retain the original skill.
The normal lifecycle sends the removal and creation packets and updates stat modifiers.
A claim under the buff collection lock selects 1 special-effect consumer for each buff instance.
It rejects competing special-effect consumers and claims from removal callbacks.
The handler releases that lock before the normal buff exit and its callbacks.
An observed expiry before exit rejects consumption.
The claim does not make all current buff timer transitions atomic.
A longer copy on the recipient remains active when the enemy loses its shorter copy.
The shared miss check now rejects `SpellMiss` and `SpellResist`.
The old check omitted both outcomes.

Item `28426` supplies proc `93`, which casts skill `22707` and creates buff `6286`.
That active Good, non-system buff runs tick effect `31731`, which references `HealEffect` `439`.
These rows match in all 3 inspected compacts.
The transfer test captures the next tick's source and checks its proc marker, item level, and level modifier.

The server selects eligible buffs by instance index, as the current dispel path does.
This order is a server policy. The exact client does not establish retail selection order.
The client also does not establish transfer timer and original-caster rules.
The implementation preserves remaining time and assigns the thief as caster to prevent a new full duration.
Human validation must check the buff icon, duration, and resulting stats.

## Approved custom formulas

Kegan approved these temporary server rules on 2026-10-04.
They are custom rules, not confirmed retail formulas.
[Research issue #633](https://github.com/KeganHollern/aaemu-cluster/issues/633) tracks the exact retail behavior at Low priority.

- Redeem consumes 1 eligible self-buff. It restores 10-15% of maximum mana, capped at 4000.
- Explode consumes 1 eligible target buff. The direct skill deals 20% of maximum health, capped at 1000.
- The buff 449 timeout variant deals 50% of maximum health, capped at 2000.

The handler reads the cap and percentage range from values 1-3.
It samples an integer percentage inclusively and truncates the calculated resource amount.
It reads maximum health or mana before consumption changes any buff modifiers.
The cap applies before normal Magic Damage mitigation and critical-hit rules.

Redeem uses the normal mana restoration effect and its feedback packets.
It cancels later skill effects when no eligible buff exists.
This prevents the separate Inspired effect from granting a free stack.
At full mana, successful consumption still permits the authored Inspired effect.

Explode uses the normal Magic Damage effect and its combat, immunity, event, and packet paths.
It keeps the original `EffectSource`, including item-proc and area-damage metadata.
The timeout caller has no skill instance.
The handler recovers its duel context from `CastBuff` when the generic trigger omits that context.
A finished duel rejects both consumption and damage.

## Human checks

These checks need 2 opposing characters in an allowed combat area or an active duel.
One character needs Leech. The other character needs a visible beneficial buff.
Use the source and image release recorded in HUMAN VALIDATION #573.

1. Apply a timed beneficial buff to the target.
2. Use Leech until its authored chance succeeds.
3. Check that the target loses 1 buff and the caster receives that buff.
4. Check that the received buff keeps its remaining time and stat effect.
5. Repeat with no eligible target buff. Check that no buff appears on the caster.
6. Repeat with a longer copy on the caster. Check that the longer copy remains.

No human gameplay result is claimed by the automated tests.

## Automated checks

The shared Debug build passed, and all 22 buff cases passed in the 41-case special-effect run.
They cover packet bodies, duration, level, charges, eligible buffs, and repeated transfer.
They also cover dead targets, spell misses, recipient requirements, and a longer recipient buff.
The consumption tests cover concurrent calls and a removal callback.
A forced lock interleaving checks that exit does not hold the collection lock while it waits for the buff lock.
Another test rejects an observed expiry without callbacks.
The consumption tests cover the actual 2-effect Absorb skill and both Focal Concussion variants.
They check caps, mana limits, Magic Damage mitigation, and the absence of damage without a buff.
The timeout tests cover a null skill and a finished duel.
Another test checks that the original area-damage multiplier reaches the damage effect.
The packet tests read every field and check that no bytes remain.

Absorb Effect has no authored prerequisite for a beneficial buff.
Its Inspired effect follows Redeem in the normal skill-effect list.
The old stub lets that Inspired effect run even when no buff exists.
A failed Redeem must stop later effects to enforce the consumption dependency.

More human checks need the same release recorded in #573:

1. Use Absorb Effect with a beneficial buff and depleted mana.
2. Check that 1 buff disappears, mana increases within the approved range, and 1 Inspired stack appears.
3. Remove all beneficial buffs and repeat. Check that no mana or Inspired stack appears.
4. Test Focal Concussion with a controlled hostile NPC or an administrator test skill.
5. Compare a target with a beneficial buff against a target without one.
6. Check that only the buffed target loses a buff and takes the approved Magic Damage.

A separate timeout test needs buff 449 from an administrator test setup.
That test must check the approved 50% variant and the absence of damage after a duel ends.
