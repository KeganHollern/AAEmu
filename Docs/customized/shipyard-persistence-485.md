# Shipyard persistence and salvage

This change resolves cluster issue #485.
The related [placement rules](shipyard-placement-484.md) resolve issue #484.

## Durable construction

The additive `2026-10-02_aaemu_game_shipyards.sql` update creates the `shipyards` table.
The normal database updater applies this update before the server loads player objects.
The base schema contains the same table definition.

The table stores the owner, world, position, placement time, health, construction step, and action count.
The step and action count record the build materials that the owner already consumed.
The table also stores the ceremony deadline and the exact reward item ID.
The existing shipyard ID allocator reserves saved IDs at startup.
Object IDs remain temporary and change after a restart.

Placement holds the persistence lock and then the owner's account lock.
Design consumption, gold, reagents, products, and the new shipyard row share one database transaction.
A known transaction failure restores the assets and releases the allocated IDs.
A failed inventory notification does not undo a committed placement.

Each construction action saves its progress with its materials and labor in `SkillLaborBatch`.
A failed transaction restores the construction state and the payment.
The launch action saves the reward item and ceremony marker in one transaction.
Repeated completion calls cannot grant another item.
The completion task uses the exact reward item, not another item of the same type.

Startup restores the saved construction model and progress.
A ceremony resumes from its saved deadline.
The server removes an expired ceremony even when its owner is offline.
The owner keeps the saved reward item and can summon the ship after login.
Automatic summon needs an online owner in the same world.

Autosave stores health.
Protection uses the original placement time and the authored `tax_duration` value in milliseconds.
Restart does not renew protection.
The saved `state_hp` value preserves the last field of `SCShipyardStateData` without a new interpretation of that field.

## Destruction and salvage

Destruction creates the debris from the compact `shipyard_rewards` rows.
It does not refund the consumed construction materials.
The current data defines public doodad 1330, with the count and radius for each shipyard template.
Each debris object uses the normal doodad phases, permissions, loot, and expiry timers.
The reward rows do not define a reduction for incomplete construction.

The debris rows and the deletion of the shipyard row share one transaction.
A paid combat skill uses its current `SkillLaborBatch` for that transaction.
Death events, debris, and visible removal occur only after commit.
A known failure restores the shipyard's health from before the lethal hit.
A later hit can retry the destruction.
Repeated removal calls cannot duplicate debris or release IDs twice.

Persistent debris uses the current main-world doodad loader.
Placement limits this feature to that persistent world.
A restart restores saved debris through the same loader as other persistent doodads.
Saved phase times keep the original expiry schedule.

## Validation

The database tests cover placement, construction progress, completion, restart, destruction, transaction failure, and repeated callbacks.
Human checks must cover construction appearance, ceremony, automatic summon, and public salvage interaction.
The shared HUMAN VALIDATION issue records these checks after release.
