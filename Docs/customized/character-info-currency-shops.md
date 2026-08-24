# Character Info currency shops

## Summary

The r208022 Character Info panel exposes the existing Honor Point and Vocation
Badge merchant catalogs without requiring a physical NPC. The client opens the
stock store UI; the game server remains authoritative for the transaction.

## Protocol and trust boundary

The client sends `/aaemu_shop honor` or `/aaemu_shop vocation` before opening
the native store. The public command creates a five-minute, character-local
authorization for merchant pack 192 or 164. A purchase without an NPC or doodad
is accepted only while that authorization is valid, and a successful purchase
refreshes it.

`CSBuyItemsPacket` does not trust the client catalog. It reloads the merchant
pack and item templates, rejects unsupported currencies, invalid or duplicate
lines, non-positive quantities, client-selected grades, missing pack entries,
price overflow, insufficient balances, and insufficient bag capacity. Currency
is derived from `merchant_packs.kind_id`; item grade is derived from
`merchant_goods`; unit price is derived from the server item template.

The same validator is used for physical merchants. Remote merchant packs cannot
be selected through the legacy doodad path. Buyback remains limited to a nearby
physical money merchant.

Point spending subtracts the exact authoritative price and never applies
vocation-earning modifiers. Purchase inventory and wallet changes execute under
a character-local lock. The bag, buyback container, and balances are restored
if any item grant or currency commit fails, and acquisition events and item
lifespan packets are deferred until the transaction succeeds.

## Client desired state

The managed client release adds native store buttons to the Honor Point and
Vocation Badge rows, plus catalog/display adapters for the stock store UI. The
embedded list is display-only. See the matching `shop.r20260824` recipe in the
deployment repository for deterministic Lua and `game_pak` construction.
