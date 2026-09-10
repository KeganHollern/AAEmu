# Housing sale authorization and settlement — issue 308

This change addresses [aaemu-cluster issue 308](https://github.com/KeganHollern/aaemu-cluster/issues/308) with authoritative sale checks and one database transaction for the property, inventory, wallet, mail, tax offer and furniture changes. It requires the shared strict persistence and inventory/mail mutation commits. Validation and publication status are recorded below.

## Implemented checks

- Listing and cancellation require the current `House.OwnerId`. `CoOwnerId` is serialized as the original-builder field; it does not grant sale rights. Same-account characters and family, guild, or public interaction permissions do not grant them either.
- Both cancellation packets pass the acting character to the manager. Unknown or recycled target links and detached house records fail without dereferencing an absent house.
- Listings must use an owned, completed, sellable, unexpired property. A listing cannot replace an existing listing and charge more certificates. A designated buyer must exist and differ from the seller.
- A listing price must be positive and fit the signed 32-bit seller-proceeds mail field. Purchase requires the exact current price, the designated buyer when present, a different current owner, and enough buyer funds. The actual conversion is checked.
- List, cancel, and purchase validate under the existing `SaveManager.PersistenceSyncRoot`. Rename, permission, and demolition requests recheck the current owner under that gate. A queued tax-expiry demolition rechecks the protection deadline before acting.
- `/house setforsale` uses the same paid, current-owner path. There is no null-caller certificate exemption that can later become a normal cancellation refund.

`HousingSalePlan` captures the pre-sale property state and proposed new owner, account, faction, private/public permission, and cleared listing. It preserves `ProtectionEndDate`, so previously paid protection time transfers with the property. `HousingSaleState` can install or restore those exact fields without notifications. This is feature data for enlistment, not an independent transaction coordinator.

## Durable settlement

Listing consumes exact appraisal-certificate stacks and changes the listing in one prepared operation. It skips quantities reserved in a trade and uses the remaining available stacks without changing the trade offer. Cancellation grants exact certificate attachments, splits them into legal ten-attachment mails, and clears the listing in the same transaction. A repeated cancellation cannot create another refund.

Purchase prepares the buyer debit, an offline-capable seller proceeds mail, a buyer confirmation, new property owner/account/faction/permissions and a cleared listing. It removes prior-owner tax offers and creates a recalculated buyer bill when taxes are due. Previously paid `ProtectionEndDate` transfers unchanged. Purchase, cancellation, old-owner actions and tax-expiry work serialize through `PersistenceSyncRoot`; a second buyer observes the completed first purchase and cannot pay again.

Furniture settlement uses exact backing-item identities:

- Transferable decorations and their system-container items belong to the buyer after purchase.
- Bound decoration items and all coffer contents return by mail to each item's actual original owner, including a guest who placed the item. Item IDs, flags, grades, UCC and details are retained.
- A retained coffer is empty, has the buyer as container owner, and has no active opener. A returned coffer's empty container and doodad rows are deleted in the settlement transaction. Container registration/ID cleanup occurs only after commit.
- Non-decoration objects such as plants detach at their world position and retain their character owner. Children of returned decorations are reparented without moving them.
- Persistent doodad ownership, item reference and parent/transform changes are written through the same database connection and transaction as the house and items. Visual deletion runs after success with item destruction disabled because those exact items already belong to mail.

`House.Save(PersistenceSaveContext)` propagates row failures and acknowledges only the exact committed snapshot. A known pre-commit failure restores the property, wallet, inventory, mail, furniture transforms and coffer metadata before returning an error. A commit with an uncertain outcome preserves the prepared state, retains identifiers and emits no success. Post-commit notification failures cannot roll memory back over committed rows.

Doodad use, phase/timer transitions, deletion, saving, coffer access and coffer permission changes share the persistence gate. `DoodadSpawner` uses that same gate to avoid a phase callback holding a spawner lock while waiting for an interaction that needs to despawn through that spawner. This serializes spawner transitions during a checkpoint; NPC spawner locks are unchanged. The affected production paths schedule/cancel tasks without waiting for another thread's task to complete.

There are no compact, client-content or schema changes in this feature. The shared character-deletion guard already rechecks house ownership before mutating a previously enumerated property.

## Validation

The previous authorization layer passed all 51 housing tests and runtime script compilation. The 15 added settlement tests cover successful and failed list/cancel/buy paths through the real inventory/mail mutation layer, exact rollback, uncertain commit behavior, offline seller proceeds, replay, simultaneous buyers, current-owner tax offers, large refunds, reserved certificates, guest-owned furniture, coffer contents and preserved child positions. A post-commit notification exception test verifies that returned coffer items remain in mail and cannot be recovered again. A spawner test races actual interaction and phase work while despawning to detect inverse lock ordering.

Disposable MySQL tests use the existing `GameMySql` fixture. They cover house dirty-state acknowledgment, coffer deletion failure after item/house/doodad writes, successful exact item returns plus coffer deregistration, and persisted decoration ownership/plant detachment. The local Docker API is too old for this fixture's Testcontainers version; hosted CI runs the MySQL tests.

Current status: feature code and tests are authored and saved as a local review commit. Compilation and execution are pending integration of the shared strict persistence dependency; this branch does not include that dependency or replacement stubs. Only whitespace/diff checks have passed for the new settlement changes. This draft is not release evidence until compilation and tests finish.

After publication, check one listing and cancellation, one ordinary purchase with the seller offline, one designated-buyer rejection, and a purchase containing an unbound decoration, a bound guest decoration and a coffer with guest contents. Confirm the buyer receives only transferable decorations, each original item owner receives the correct mail, the old coffer window cannot move items, paid protection remains unchanged, and a due tax bill belongs to the buyer. These gameplay checks remain human validation.
