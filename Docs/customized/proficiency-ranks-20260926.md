# Proficiency ranks and expansion payments

Issue: KeganHollern/aaemu-cluster#465.

## Result

Rank upgrades use the next visible row in `expert_limits`.
Each rank cap counts every proficiency at that rank or above it.
`ExpandedExpert` adds the purchased slots to each nonzero cap.
A zero cap means no limit.
The server rejects hidden ranks, insufficient points, and unknown proficiency identifiers.
Downgrades lower the point cap without adding points.

The current unlocked rank supplies the labor, production time, and experience multipliers.
The previous tables applied the labor and time benefit one rank late.
Experience gains now use the authored `exp_mul` percentage bonus.
Loot rates and the custom Commerce negotiation rule do not change.

Expansion consumes the exact seal count for the current expansion row.
A future nonzero `life_point` cost subtracts Vocation points.
The current data has no Vocation cost.
The normal economy checkpoint saves the seals, Vocation points, and expansion count together.
A known failed save restores all 3 values.
An unknown commit outcome follows the current Game consistency-stop rule.
The expansion cannot consume items reserved for trade.

No SQL migration, compact edit, client patch, or change to packet fields is needed.
The change does not automatically reduce previously saved proficiency ranks.

## Exact-client evidence

The client history reports `version 208022`.
The runtime dump and source DLL share the PE timestamp `543cb835`, image base `38ff0000`, entry point `008c5d6d`, and image size `01cb0a00`.
The dump is the preserved local research input at `.tools-re/dumps/x2game.dumped.dll` in the cluster workspace.
Its SHA-256 is `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0`.
The original dump capture method is not part of this evidence record.

The exact X2UI script is `game/scriptsbin/x2ui/skill/tab_actability.alb`.
Its SHA-256 is `de7e2d9e2f5dc7db1eee8cbcc82f846568c17cc4fbb7117373499f35bd9e1339`.
The compiled Lua lines identify these confirmed rules:

- Lines 48–53 and 354–357 sum `GetActabilityCountByGrade` from the requested grade through `GetMaxGrade`.
- Lines 410–411 subtract `advantage` and `castAdvantage` from 100 for the cost and time display.
- Native `GetGradeInfo` at `0x3943b170` adds the character expansion byte to the authored `expertLimit` field.
- Native compact loading at `0x395b45d0` reads `show` and `exp_mul`.
- The branch at `0x395b4789` skips hidden rows before their insertion and visible grade increment.

The client and server compacts contain the same proficiency rows.
The first row is saved step 0, with a 10,000 point cap.
The visible rows have these values:

| Saved step | Point cap | Proficiency cap | Labor reduction | Time reduction | XP bonus |
| --- | ---: | ---: | ---: | ---: | ---: |
| 0 | 10,000 | Unlimited | 0% | 0% | 0% |
| 1 | 20,000 | 7 | 5% | 5% | 20% |
| 2 | 30,000 | 6 | 10% | 10% | 40% |
| 3 | 40,000 | 5 | 15% | 15% | 60% |
| 4 | 50,000 | 4 | 20% | 20% | 80% |
| 5 | 70,000 | 3 | 23% | 23% | 100% |
| 6 | 90,000 | 2 | 23% | 23% | 120% |

The additive interpretation of `exp_mul` is inferred from its zero novice value and 20-point increments.
The server calculates this multiplier as `1 + exp_mul / 100`.
No exact-client XP calculation or retail capture confirms that formula in this record.
The XP field does not supply a loot bonus, so `GetLootMultiplier` stays at 1.
The 14 expansion rows charge item `29656` in quantities 1 through 14.

## Automated checks

`CharacterActabilityTests` covers each visible multiplier and cumulative cap.
It also checks hidden ranks, insufficient points, unknown identifiers, downgrade behavior, and point caps.
Expansion tests cover missing seals, split stacks, Vocation costs, trade reservations, the last row, save failure, and unknown commit outcomes.
The full unit suite checks the current Commerce and paid-skill callers.

## Human validation

These checks remain pending for the shared `HUMAN VALIDATION` issue.
A test character needs enough proficiency points and expansion seals for the rank and payment checks.

- Reach a proficiency cap and check that points stop until an upgrade succeeds.
- Fill a rank cap and check that another upgrade fails. Higher ranks must count toward that cap.
- Buy one expansion and check the displayed seal cost, consumed seals, and increased slot counts.
- Reconnect and check the saved expansion count, proficiency rank, and seal count.
- Try an expansion without enough seals and check that no seal or slot count changes.
- Downgrade a proficiency and check that its points respect the lower cap. Check that the higher rank slot becomes available.
- At a promoted rank, check the labor cost, craft duration, and character XP against the authored values above.
- At 90,000 points, check that the client does not offer a hidden rank.
