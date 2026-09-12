# Cash shop settlement

Cluster issues #470 and #85 use one cart transaction.
The server checks every line before it creates the cart outputs.
It rejects AA Point prices because the server has no AA Point balance.
It uses the same discounted price for credits, loyalty, and coins.
Stock and purchase limits count item units across all cart lines.

The persistence lock covers cart preparation, SQL commit, and notification.
The SQL transaction stores mail, exact attachment IDs, coin balances, account charges, stock, and sale records.
Conditional account updates prevent a negative credit or loyalty balance.
Offer row locks make the purchase-limit check and stock update safe across concurrent SQL transactions.
A failed preparation or SQL write restores all prepared mail, items, coin balances, and stock.
A lost commit acknowledgement stops all persistence and the Game process.
A new process reads the durable state before it accepts more purchases.
The server does not send a success response before commit.

## Schema effects

The update adds nullable `audit_ics_sales.item_count`.
Every old row keeps NULL. Current SKU values cannot prove historical quantities.
The update changes no balance, stock, item, or mail.
New sales write the exact purchased quantity.
A limited offer treats an unknown old quantity as exhausted for that buyer.
This rule also applies after a SKU changes or disappears.
The supported sold-out mask provides the client display. No guessed `0x1d5` packet is sent.

## Checks

The tests use a disposable local MySQL schema.
They check persisted gift mail after reload, concurrent carts, failed debit, failed mail insert, and failed audit insert.
They also check migration repeatability and unknown legacy quantities.
Unit tests check cart overflow, quantities, discount prices, unsupported currency, and gift restrictions.

After publication, buy one self item and one gift for an offline character.
Check the balance, stock display, and mail attachments after relog.
Check a limited offer after its account limit is consumed.
These client checks do not yet have human validation.
