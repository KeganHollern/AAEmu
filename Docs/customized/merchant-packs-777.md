# NPC merchant packs, issue 777

The r208022 `merchants` table can assign several packs to one NPC. The former
loader retained only the last row. The purchase handler then rejected goods
from the other packs. NPC 626 also has an empty personal pack, which can hide
all 26 entries in its shared pack.

The loader now retains every assigned pack in `merchants.id` order, including
empty-pack metadata. NPC purchase validation combines their available goods.
The original packs remain unchanged for doodad and remote shops. A missing
pack does not hide a valid assigned pack or authorize missing goods. The
current multi-pack NPCs all use one currency. A mixed-currency assignment is
rejected instead of charging the wrong currency. Existing distance, price,
grade, inventory, transaction, and basket-size checks still apply.

The server compact with SHA-256
`636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`
contains 38 multi-pack NPCs. Seven reference a missing pack as well as valid
packs. NPC 626 assigns packs 9 and 154. Pack 9 is empty, and pack 154 has 26
goods. The two items repeated across packs have matching grades. This fix
changes no compact data or packet layout.

`NpcMerchantTests` covers the loader, empty and missing packs, merged goods,
unchanged shared packs, and all 38 multi-pack NPCs when
`AAEMU_MERCHANT_COMPACT` points to that read-only compact. `CSBuyItemsPacketTests`
covers a basket from both packs, unrelated goods, and a distant merchant.

For real-client validation, open NPC 626's shop and check its 26 authored
entries. Buy item 23091 from the shared pack. Expect grade 0 and a charge of
120 copper for one item. Check an ordinary single-pack merchant and a doodad
shop as well. The client owns the shop display, so these visible checks remain
in HUMAN VALIDATION #573 until completed with the deployed release.
