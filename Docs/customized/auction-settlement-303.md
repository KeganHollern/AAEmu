# Auction listing and settlement integrity (#303)

Issue: <https://github.com/KeganHollern/aaemu-cluster/issues/303>

Posting, bidding, buyout, seller cancellation, and expiry share
`SaveManager.PersistenceSyncRoot`. Each successful operation checkpoints its
wallet, original item/container, auction row/deletion, and generated mail through
`TryCommitEconomy` before publishing inventory or auction notifications.

Posting requires the exact registered item in the seller's bag, a positive valid
stack, a tradable binding, valid start/buyout prices, and one of the four supported
durations. The listing fee uses wide arithmetic and the existing one-million
copper cap. Client auctioneer fields keep their existing behavior; this change
does not infer an unverified packet contract for those fields.

All bid prices, seller identity, bidder identity, and item references come from
the server's current listing. A leading bidder increases their existing escrow
by the difference. A displaced bidder receives one refund mail. An offered price
above buyout is capped at the server's buyout price. Completed sales deliver the
same item instance to the buyer and 90% of the sale price to the seller through
separate mail records. Cancellation requires the seller and an unbid, unexpired
listing. Cancellation and unsold expiry return the original item, preserving its
ID, count, grade, details, UCC, and expiration state.

Known checkpoint rollback restores every prepared participant, including dirty
flags and deletion queues, before an error response. A commit exception has an
uncertain outcome: prepared state remains together and no success notification
is sent. Notification failures after an accepted checkpoint cannot restore the
settled listing or remove its mail. Published auction IDs stay reserved during
the server lifetime so delayed requests cannot act on a different listing.

`AuctionSettlementTests` exercises the public operations with real inventory
and mail mutation objects, controlled checkpoint outcomes, notification ordering,
malformed/foreign/repeated requests, price limits, insufficient funds, and racing
buyout/bid/cancel calls. Reservation regressions verify rejected posts restore
their fee and source item, reserved wallet funds cannot bid or buy out a listing,
and bids can spend the remaining unreserved balance.
`AuctionSettlementPersistenceTests` uses the disposable
GameMySql fixture and the production save/load paths to check listing failure,
bid escrow across restart, failed-buyout retry, terminal settlement reload,
wallets, item identity, and mail proceeds/refunds.

Focused gameplay validation: list a distinctive graded/UCC/expiring item, bid
from two characters, raise the leading bid, buy out, and inspect seller/buyer/
refund mail. Also cancel an unbid listing and let both bid and unbid listings
expire. Check the auction search and bid list around each transition. Mail
attachment claim crash atomicity remains the separate #246 workstream.
