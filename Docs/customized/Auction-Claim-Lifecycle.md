# Auction claim lifecycle

This change resolves cluster issue 246 and completes cluster PR 356.
It uses the settlement release at `61f535c0c0953f1c8da249c77ac74c330a74c2a4`.
The original claim code remains in fork PR 109.

## Durable state and concurrency

Auction post, bid, buyout, expiry, and cancellation use the shared economy checkpoint.
The checkpoint saves the buyer debit, auction removal, attachment, and both result mails before success.
Each new claim also checks this checkpoint before it prepares achievement changes.
If this checkpoint fails, the claim keeps its original mail and inventory state.

Claims hold `SaveManager.PersistenceSyncRoot` from the mail lookup through the final notifications.
The same lock protects autosave and real inventory acquisition, consumption, and movement.
The claim plan therefore cannot use a stack that another thread changes before commit or live application.
The account lock also protects the sale labor update.

The claim transaction writes the receipt, mail, item or money, related character state, and achievements.
The receipt key contains the mail ID and claim type.
Receipt and item ID retention prevents a later object from reusing a claim identity.
A repeated request returns the receipt without another grant or achievement increment.
Claim packets and achievement packets follow the commit.
A packet failure closes the session so the next connection reads durable state.
An uncertain claim commit stops Game before another save can overwrite its result.

## SQL

`2026-09-01_aaemu_game_auction_mail_claims.sql` adds the `auction_mail_claims` InnoDB table.
The base schema contains the same table.
The application updater applies this previously unpublished script before manager loading.
The release keeps `Connections__AutoApplyUpdates=true`.
This change does not alter current rows or either compact snapshot.

## Tests

The unit tests cover buy and sale claims, repeat requests, failed commits, restarts, retained IDs, and lost packets.
The MySQL store tests check the real schema, receipt reads, full transaction failure, and concurrent receipt insertion.
The lifecycle tests use the real auction manager, mail manager, item manager, and economy checkpoint.
They claim buyer and seller results in both orders before autosave, with and without a destination stack.
They reload money, items, mails, auction state, labor, and achievement progress from MySQL.
Other tests pause a claim and start real item acquisition, consumption, or movement on another thread.
The competing action must wait until the claim commits and enters live state.

The test sale-plan factory supplies fixed labor and experience values.
These lifecycle tests check persistence and concurrency. They do not test the content formula values.
The release record gives the exact build and test results.

## Player checks after deployment

1. Buy an auction item and claim its mail before the next autosave.
2. Claim the seller money and check labor and auction achievement progress.
3. Repeat the item check with a matching inventory stack.
4. Reconnect and check both inventories, balances, mails, and achievement progress.
5. Repeat the same claim request and check that no value increases again.

Human gameplay validation remains pending until these checks pass.
This release targets the downstream r208022 server. An upstream submission needs separate approval.
