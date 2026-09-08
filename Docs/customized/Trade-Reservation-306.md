# Trade quantities and prepared inventory exchange

Issue: <https://github.com/KeganHollern/aaemu-cluster/issues/306>

`TradeReservation` holds exact item quantities and offered copper while the
trade manager owns the offer. Item removal, moving, splitting, ordinary item
consumption, wallet spending, bank deposits, and inventory expansion respect
those reservations. Ordinary consumption can use the unreserved remainder of a
stack. Expiry invalidates the complete offer before deleting an expired item.
Disposal releases the offer without changing ownership or emitting item tasks.

For settlement, the trade manager releases its reservation while holding
`SaveManager.PersistenceSyncRoot`, then uses `InventoryMutation.TryExchange` to
prepare both directions. All outgoing quantities are removed before any incoming
item is placed, allowing a full-bag swap. Whole stacks retain their IDs; partial
stacks get one new ID and retain item-specific fields, timestamps, and independent
mutable detail arrays. Failed preparation restores original items, slots, dirty
flags, and wallets, and releases only IDs created by that attempt.

The trade manager must persist prepared state before calling `Complete(false)`
and send the captured item tasks through the existing trade completion packet.
An uncertain commit outcome must preserve prepared state. These helpers alone do
not save or complete a trade.

Validation on 2026-09-08: Release compilation passed; all 29 inventory mutation
tests and all 69 player mail send tests passed. The focused cases cover full bags,
partial quantities, later capacity failure, ID collision, equipment and fish
details, wallet reservations, ordinary consumption, destruction packets,
bag/bank expansion, and expiry cancellation. Trade manager, skill/craft race,
database restart, and in-client validation belong to the integrated issue change
and remain pending.
