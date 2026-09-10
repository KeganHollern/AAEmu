# Craft completion and trade reservations

Issue: <https://github.com/KeganHollern/aaemu-cluster/issues/306>

Craft admission checks trade reservations and marks the character busy together
under `SaveManager.PersistenceSyncRoot`. Completion keeps that busy state through
product callbacks, material consumption, and scheduling or cancellation. A
reentrant completion callback cannot grant a second product.

The final material check counts only available bag stacks owned by the crafter,
subtracts reserved quantities, and combines repeated material requirements. This
prevents a reserved stack, a bank-only material, or duplicate requirements from
passing a check that the subsequent bag consumption cannot satisfy. Completion
and inventory mutation use the same persistence gate.

Admission failures, skill rejection, completion errors, and matching skill
cancellation clear the craft state. `CancelFromSkill` provides the cleanup hook
for the separately reviewed skill execution change; this commit does not wire
that hook into `Skill.cs` or claim to cover asynchronous skill plot effects.

Validation: Release build and 18 focused `CharacterCraft*Tests` pass. Nine new
reservation tests cover admission by item or money, reservation at completion,
missing/bank/duplicate materials, reentrant product callbacks, matching skill
cancellation, and completion exception cleanup. Trade admission tests belong to
the trade manager change and must verify it rejects a character while crafting.

Gameplay checks after the combined trade/skill release: try crafting with an
offered ingredient, try offering an ingredient while its craft is running, cancel
a craft and start another, and complete a repeated craft sequence. Products and
material consumption must remain one-for-one.
