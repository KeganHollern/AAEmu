# Mail records in an economic settlement

`MailMutation` prepares new mail and removal of exact existing mail records while
the caller holds `SaveManager.PersistenceSyncRoot`. Attachment movement belongs
to the same operation's `InventoryMutation`; a staged attachment must be the
registered item in the receiver's mail container. Slots remain container slots,
and the mail body retains the ordered references to its exact attachments.

Use `TryAdd` for a new mail with ID zero and `TryRemove` for an existing source
mail. These methods update pending state without delivery notifications. The
strict save checkpoint must commit this state together with the associated
wallets, items, auction, or property state before `Complete` publishes notices.

Disposal before completion restores the original mail dictionary and pending
deletion entries and releases newly allocated mail IDs. Removed source mail IDs
remain reserved after success so a delayed tax request cannot hit a replacement.
If committing throws with an uncertain outcome, call `PreservePreparedState` on
every prepared participant before unwinding. This operation is idempotent after
completion too, allowing a notification failure to preserve later participants.
It publishes no success notifications and releases no IDs.

Existing mail delivery, list, read, attachment, deletion, and return paths share
the same lock. They cannot observe staged records from a different thread while
the database checkpoint is running. This change does not alter attachment-claim
or sender-receipt behavior.

Validation: 11 staged-mail cases, 14 inventory-mutation cases, and 26 existing
mail ownership, return, receipt, and tax cases passed in the local Release build.
The staged objects do not themselves establish database durability; the strict
checkpoint and feature callers require their own integration tests.
