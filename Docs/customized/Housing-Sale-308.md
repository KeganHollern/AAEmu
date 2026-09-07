# Housing sale authorization and settlement — issue 308

This preparatory change enforces authoritative sale checks and supplies a property state plan. It does **not** yet complete [aaemu-cluster issue 308](https://github.com/KeganHollern/aaemu-cluster/issues/308): durable settlement of property, mail, tax state, and items remains pending the shared economy transaction implementation. Do not describe this commit alone as an atomic sale fix or close the issue on its basis.

## Implemented checks

- Listing and cancellation require the current `House.OwnerId`. `CoOwnerId` is serialized as the original-builder field; it does not grant sale rights. Same-account characters and family, guild, or public interaction permissions do not grant them either.
- Both cancellation packets pass the acting character to the manager. Unknown or recycled target links and detached house records fail without dereferencing an absent house.
- Listings must use an owned, completed, sellable, unexpired property. A listing cannot replace an existing listing and charge more certificates. A designated buyer must exist and differ from the seller.
- A listing price must be positive and fit the signed 32-bit seller-proceeds mail field. Purchase requires the exact current price, the designated buyer when present, a different current owner, and enough buyer funds. The actual conversion is checked.
- List, cancel, and purchase validate under the existing `SaveManager.PersistenceSyncRoot`. Rename, permission, and demolition requests recheck the current owner under that gate. A queued tax-expiry demolition rechecks the protection deadline before acting.
- `/house setforsale` uses the same paid, current-owner path. There is no null-caller certificate exemption that can later become a normal cancellation refund.

`HousingSalePlan` captures the pre-sale property state and proposed new owner, account, faction, private/public permission, and cleared listing. It preserves `ProtectionEndDate`, so previously paid protection time transfers with the property. `HousingSaleState` can install or restore those exact fields without notifications. This is feature data for enlistment, not an independent transaction coordinator.

## Remaining transaction integration

The current manager still calls its existing inventory, mail, furniture, and tax-update methods. Those publish or persist parts of a sale independently. A database or mail failure can therefore still leave a partial operation. The state plan is not yet used to claim durable commit/rollback.

The shared mutation and strict persistence APIs must provide one operation under the existing gate:

1. Recheck the registered house, listing, owner, price, designated buyer, funds, and all exact source item identities.
2. For listing/cancellation, stage the appraisal-certificate consume or refund and listing changes together. Refund mail must own the exact granted item IDs.
3. For purchase, stage buyer debit, seller proceeds mail, buyer confirmation, property fields, and replacement tax mail. Retain the paid protection deadline, remove previous-owner billing offers, and create a recalculated current-owner bill when due.
4. Stage furniture disposition before any visual deletion: move transferable backing items to the buyer's system container; return bound furniture and coffer contents by mail; transfer or remove coffer container ownership as appropriate; detach non-decoration objects while preserving their owner. Persistent doodad owner/parent changes and deletes must use the same database transaction.
5. Write the house snapshot with strict failure propagation and defer dirty-state acknowledgement until commit. Publish packets, markers, inventory callbacks, and visual removals only after success. Restore prepared state on a known failed commit.

`House.Save(PersistenceSaveContext)` is reserved for this feature owner but is not introduced in this preparatory change. No compact snapshot, SQL file, client content, or deployment input changes here.

One adjacent integration hook is assigned to the shared owner: in `CharacterManager.DeleteCharacterAssets`, recheck `house.OwnerId == character.Id` inside `lock (house.TaxPaymentSyncRoot)` before changing the collected house's permission/protection date. The house may have been sold after the original ownership enumeration.

## Validation

The Release unit-test project builds successfully. All 51 housing tests pass, including 43 new cases for cross-character authority, invalid/recycled links, missing callers, designated buyers, price limits, insufficient funds, state restoration, paid-tax preservation, former-owner actions, and superseded tax expiry. Runtime script compilation passes with zero errors and zero warnings, covering the debug command's changed cancellation call.

Still required after transaction integration: successful listing/cancel/buy paths through the real shared mutation layer; failed certificate/mail/row writes; replay after completion; two simultaneous buyers with one winner; concurrent cancel/buy and old-owner actions; and disposable MySQL failure/reload tests proving one durable property/item/mail/money result. Human sale and tax-mail checks follow the eventual published release.
