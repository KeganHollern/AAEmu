# Quest item groups and interaction objectives

This change resolves cluster issues [#265](https://github.com/KeganHollern/aaemu-cluster/issues/265)
and [#268](https://github.com/KeganHollern/aaemu-cluster/issues/268).

## Evidence and scope

The audit used source baseline `c50c7a6e` and the deployed r208022 server compact.
The compact SHA-256 is `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.
The compact data and current server event paths confirm the following facts.
No packet fields, client files, compact rows, or MySQL schema change.

| Quest | Gather act | Group | Required count | Completion cleanup | Abandonment cleanup |
| --- | --- | --- | --- | --- | --- |
| 5490 | 20 | 9 | 10 | Yes | Yes |
| 6578 | 23 | 14 | 1 | No | No |
| 6600 | 25 | 15 | 1 | No | No |
| 6615 | 26 | 16 | 1 | No | No |

Group 9 contains items `28557`, `28558`, and `29288`.
Group 14 contains items `34602` through `34618`.
Group 15 contains items `34619` through `34635`.
Group 16 contains items `34636` through `34652`.
Test quest `5489` uses group 10, which contains items `8518` and `29173`.
All 4 live gather acts set `drop_when_destroy=false`.

Gather objectives use the same held-item model as ordinary item gather.
They count inventory, warehouse, and equipment contents, including items owned before acceptance.
Positive and negative item events cause a new count from current inventory.
This avoids false progress from removal, internal movement, repeated notifications, and duplicate group rows.
The existing objective cap and saved quest objective fields still apply.

Completion uses `cleanup` and abandonment uses `destroy_when_drop`.
One removal count applies across all distinct group members.
The quest does not remove the required count from every member template.
The existing manual destruction path still applies `drop_when_destroy`.
Group-use objectives receive successful item-use events from all 3 character item-use methods.
Ordinary item acquisition or consumption does not count as a use.

The interaction effect supplies its server-loaded `WorldInteraction` value.
The event captures the target template and the resulting `Doodad.FuncGroupId` after the action.
Craft effects use the same event publisher.
Each objective checks every nonzero doodad, interaction, and phase constraint.
A zero constraint permits any value for that field.

Paid skills keep the event until the transaction commits.
The event stores values before later effects or callbacks can change the target.
Failed commits and cancelled skills do not publish the event.
Team delivery preserves the complete event and the existing single-delivery marker.
The existing team eligibility and distance checks remain in force.

Some existing `Doodad.Use` refusals return without a cancellation result.
This change does not classify every such refusal as a failed interaction.
The confirmed rejection checks cover the configured fields, cancellation, and failed transaction commits.

These conclusions concern the server event contract and the authored compact fields.
This work does not claim a new native packet contract or completed human client tests.

## Automated checks

- The Release build passed with no errors. All 79 focused tests passed.
- `QuestItemGroupObjectiveTests` exercises all 4 live gather definitions with member items.
- The group tests cover preexisting items, mixed stacks, negative changes, excess items, duplicate rows, and reconnect state.
- Cleanup tests cover both flags together through `CharacterQuests.CompleteQuest` and `DropQuest`.
- Manual destruction tests cover both values of `drop_when_destroy`.
- Group-use tests cover all 3 character use paths and handler removal.
- `QuestInteractionObjectiveTests` rejects the wrong doodad, function, and phase.
- Team tests keep the function and phase during eligible team delivery.
- Skill labor tests preserve the interaction snapshot and reject failed commits or cancelled actions.
- Existing interaction tests still check skill-end ordering for instance exits.

## Pending human checks for HUMAN VALIDATION #573

These checks need the published release. None is complete from automated test results.
A GM-prepared test character can use a quest when normal prerequisites prevent access.
Do not reset Kegan's saved quests or remove his items to prepare a test.

- [ ] **HV265-1.** Use quest `5490` on a GM-prepared test character. Its authored 2013 schedule prevents normal acceptance. Collect 10 total group-9 items across available member types. The objective reaches 10 and permits completion. Completion removes only 10 matching items.
- [ ] **HV265-2.** Accept quest `6578`. Acquire 1 item from `34602` through `34618`. The objective completes and keeps the item after turn-in.
- [ ] **HV265-3.** Accept quest `6600`. Acquire 1 item from `34619` through `34635`. The objective completes and keeps the item after turn-in.
- [ ] **HV265-4.** Accept quest `6615`. Acquire 1 item from `34636` through `34652`. The objective completes and keeps the item after turn-in.
- [ ] **HV265-5.** Use a fresh attempt of quest `5490` with partial progress. Reconnect and compare its count with the held group items. Remove a matching item. The count decreases. Abandon the quest. Cleanup removes matching items once and leaves unrelated items.
- [ ] **HV265-6.** Use GM test quest `5489` on a test character. Use either group-10 item successfully. The use objective advances once. Acquisition without use gives no credit. This is test content, not a normal player quest.
- [ ] **HV268-1.** Use a phase-specific quest interaction, such as quest `2417` with doodad `2914`. The correct `Use` interaction reaches phase `6553` and gives 1 credit. An action with another resulting phase gives no credit.
- [ ] **HV268-2.** Use a test doodad with multiple functions for an active interaction objective. The configured function gives credit. A different function on the same doodad gives no credit. This check can need GM setup.
- [ ] **HV268-3.** With a function-specific objective active, complete an unrelated craft at the target when that route exists. The craft gives no interaction credit. Then complete the configured action and cancel another attempt. Only the completed configured action gives credit. This check can need GM setup.
- [ ] **HV268-4.** Repeat a team-share interaction with 2 nearby online characters. Each eligible character receives 1 credit. A distant or different-instance character receives none. This check needs a second character.
