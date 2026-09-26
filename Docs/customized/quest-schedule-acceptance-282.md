# Quest schedule acceptance, issue 282

## Result

The server checks `game_schedule_quests` before a normal quest acceptance or a failed main-quest restart.
A quest with schedule links needs at least 1 active linked window.
A missing schedule cannot make a quest available.
A quest without links keeps its normal acceptance rules.

The check uses the current schedule engine and its UTC clock.
The start boundary is inclusive.
The end boundary is exclusive.
This change does not change schedule dates, recurring windows, compact data, or event activation.

The check occurs before runtime ID allocation, quest supplies, event subscriptions, or persistence.
A rejected restart preserves the failed attempt and its supplies.
The existing GM `forcibly` option still bypasses normal acceptance checks.
Human checks must use the normal quest route.

Accepted quests keep their normal progress, report, and timer rules after a schedule closes.
The schedule does not delete a quest or start a new expiry timer.
The server sends the system message `This quest is not available at this time.` when it rejects acceptance.
No packet body changes are necessary.

## Exact client evidence

The client history records `version 208022`.
The packed DLL and runtime dump have PE timestamp `543cb835`, image base `38ff0000`, and image size `01cb0a00`.
The dump predates this task and contains the unpacked code.
This task did not make a new runtime dump.

| Input | SHA-256 |
| --- | --- |
| `client/bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| `.tools-re/dumps/x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| Client compact | `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4` |
| Server compact | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |
| `game/scriptsbin/x2ui/questcontext/common.alb` | `10f92a9708142b47495b88d41726f83ff39182d6c998a1e516a5974a95e17b58` |
| `game/scriptsbin/x2ui/baselib/locale_helper.alb` | `944dc8076da065746f4a65f767bd9870c1bb5138b641004edea2fc89e49321b9` |

The native loader at `0x39835a50` reads `id`, `game_schedule_id`, and `quest_id` from `game_schedule_quests`.
The loader at `0x39835f30` attaches each valid association to its schedule descriptor.
The descriptor has a vector of quest associations at offsets `0x50` through `0x58`.
These facts are **confirmed**.

The native `IsDailyQuest` function at `0x3949e200` calls `0x3949e190`.
That helper walks schedule descriptors and their quest associations.
For a matching quest, it returns the authored weekday.
It returns `8` when no specific weekday applies.
The binding points to `0x3949e200` at `0x394a7af9`.
These facts are **confirmed**.

The X2UI `questcontext/common.alb` function at source lines 1305–1332 reads `IsDailyQuest` and passes the weekday to the daily marker.
The marker at source lines 15–61 uses `day_quest_tip` when a weekday applies.
Client `ui_texts` row `6281` supplies that key.
Its English localization says, `You can perform this quest every $1.`
The connection between the association and the visible quest weekday is **confirmed**.

The native `OnQuestContextFailed` handler at `0x391dbcd0` passes a quest ID and failure reason to `0x3937a130`.
That consumer sends `QUEST_ERROR_INFO` to X2UI.
The X2UI handler at source lines 258–260 indexes `locale.questContext.errors` with that reason.
The error table in `baselib/locale_helper.alb`, source lines 187–224, ends at reason `36`, `player_trade`.
The server enum also names reason `37`, `GameSchedule`, but this client has no corresponding error text.
The new check uses the established system-chat path instead of that unsupported reason.

## Scope and confidence

The weekday label confirms that schedule links describe quest availability.
The exact retail server transition is not present in the client binary.
The **acceptance-only rule is an inference** from that availability evidence.
It places the gate at the server-owned start of a new attempt.
The implementation also checks restart because restart starts a new attempt.

The union of linked windows is an **inference** that matches the current NPC and doodad schedule engine.
The client weekday helper returns the first matching specific weekday.
That display helper does not prove an intersection rule for server acceptance.
The tests cover both independent windows and the gap between them.

Automatic expiry, removal, and a post-acceptance progress restriction remain **unknown**.
This change adds none of those rules.
The client controls its quest offer markers from its current data and state.
The new server check does not promise that an unavailable quest loses its client marker.

## Current data

The compact has `96` links for `95` quests.
Of those, `42` links for `41` quests reference absent schedule IDs `26` through `32`.
Quest `5823` references both missing schedules `26` and `27`.
Those associations cannot grant acceptance.
This change does not reconstruct the absent schedule rows.

The valid links include expired event windows from 2013 and recurring weekday quests.
Examples include quest `5490` under schedule `23`, quest `6361` under schedule `108`, and quest `6064` under schedule `120`.
Schedule `108` allows Monday from `00:00` through the exclusive `23:59` UTC boundary.
Schedule `120` allows Monday from `00:00` through the exclusive `23:58` UTC boundary.
The existing engine supplies those boundaries without a new timezone conversion.

## Automated checks

The Release builds for `AAEmu.UnitTests` and `AAEmu.IntegrationTests` passed.
The focused quest run passed `310` tests without skips.
The new cases cover these results:

- Either linked window allows acceptance.
- Each start boundary allows acceptance.
- Each end boundary rejects acceptance.
- The gap between windows rejects acceptance.
- A missing schedule cannot grant acceptance or hide an active alternative.
- UTC weekday recurrence remains independent of the host's local timezone.
- A quest without schedule links keeps its normal rules.
- A new association load replaces the old index.
- Rejected item starters allocate no quest ID and write no quest row.
- A rejected restart preserves its failed state and makes no database write.
- An accepted quest receives objective credit and completes after its window closes.

The branch's full test run reached `4167` passed tests and `19` optional skips.
The sandbox denied sockets in `4` unrelated network tests.
The release must run the full integrated suite with local socket access.

## Pending human checks

Record results in [HUMAN VALIDATION #573](https://github.com/KeganHollern/aaemu-cluster/issues/573).
No human client check occurred for this change.
Use an accessible scheduled quest and a character that meets its normal requirements.
Record the quest ID, schedule ID, UTC time, source release, and client revision.
Do not use GM forced acceptance or enable a closed zone for these checks.
If the required quest is inaccessible, keep its check pending.

1. Before the window, use the normal acceptance route. Expect the system message and no new quest or supplied items.
2. Inside the window, use the same route. Expect normal acceptance and exactly one set of quest supplies.
3. After the window, try a new acceptance. Expect rejection without items, progress, or rewards.
4. Accept before closure, then progress and report after closure. Expect normal completion unless the quest's own timer expires.
5. Reconnect with an accepted quest after closure. Expect the saved quest and its progress to remain available.
6. Check an ordinary unscheduled quest. Expect normal acceptance and completion.
7. If a normally available scheduled quest supports failed-attempt restart, test restart outside its window. Expect the failed attempt to remain unchanged.

No current valid compact quest has 2 schedule links.
The automated fixture checks both alternatives without a compact change.
A manual multiple-window check needs separately reviewed content and remains pending.
