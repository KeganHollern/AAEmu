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
ID, count, grade, details, UCC, and expiration state while the item remains valid.

Issue [#772](https://github.com/KeganHollern/aaemu-cluster/issues/772) adds checks
for the item's own absolute expiry. An expired item cannot be posted, bid on,
or bought out. The auction timer also checks item expiry before the listing
deadline. It deletes expired escrow and refunds the held bid once in the same
transaction. The seller receives no sale proceeds. Cancellation also deletes
an already expired item instead of returning an unusable attachment.
Unexpired timed items remain tradable, and their lifetimes do not pause or reset.

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
The expiry tests also cover refused bids and buyouts, refund rollback, and
restart before retry. They check that the deleted item cannot reappear and the
refund cannot repeat.

Focused gameplay validation: list a distinctive graded/UCC/expiring item, bid
from two characters, raise the leading bid, buy out, and inspect seller/buyer/
refund mail. Also cancel an unbid listing and let both bid and unbid listings
expire. Check the auction search and bid list around each transition. Mail
attachment claim crash atomicity remains the separate #246 workstream.
For #772, let an eligible timed item expire in an active listing with a bid.
Check that the listing disappears, the bidder receives 1 full refund, and the
seller receives no proceeds. Reconnect and check that neither the item nor a
second refund appears. Also check an expired listing without a bid and reject
a bid or buyout after item expiry. These checks need 2 disposable characters.
