# Character Info currency-shop shortcuts

This customization adds small buttons to the Honor Points and Vocation Badges rows in the ArcheAge r208022 Character Info panel.

## Why a server bridge is required

The r208022 Lua API can toggle a store that was already activated through an NPC, but it has no native method for opening an arbitrary merchant pack. Later clients added that capability together with the Character Info buttons.

The compatibility bridge keeps the stock 1.2 store UI and protocol:

1. `character_info_shop_shortcuts.lua` wraps the two existing currency-label constructors and adds one button to each value widget.
2. A click calls `X2Chat:ExpressEmotion` with reserved signal 100 for Honor or 101 for Vocation.
3. `CSExpressEmotionPacket` consumes those two signals instead of broadcasting an emote.
4. `CurrencyShopManager` creates the appropriate legacy merchant as a temporary virtual NPC:
   - Honor: NPC template 7054, merchant pack 192.
   - Vocation: NPC template 9785, merchant pack 164.
5. The NPC is registered in the player's world so existing purchase validation still checks the correct merchant pack and distance. Its spawn is sent only to the requesting player at 0.1% scale.
6. The server sends the stock `UseStore` interaction response, which initializes the existing client store window. A new click replaces the old virtual merchant, and inactive sessions expire after ten minutes.

The two signal IDs are valid but rarely used express-text entries in this client build. They are private protocol signals for a client and server that both include this customization.

## Client source and packing

The maintained Lua source is:

`Tools/ClientPatcher/Sources/character_info_shop_shortcuts.lua`

ArcheAge r208022 uses Lua 5.1 bytecode with 32-bit `size_t` and 32-bit floating-point numbers. Compile with Lua 5.1, then convert the standard chunk with:

```powershell
python Tools/ClientPatcher/lua51_chunk.py convert `
  character_info_shop_shortcuts.standard.luac `
  character_info_shop_shortcuts.alb `
  --size-t-size 4 `
  --number-size 4
```

The compiled module is added as:

`game/scriptsbin/x2ui/chracterinfo/shop_shortcuts.alb`

The misspelling `chracterinfo` is the stock client path. The module's TOC line, `shop_shortcuts.lua`, must follow `common.lua` and precede `character_info_table.lua` so the wrapper functions are active when the row table captures them.

Use `Tools/ClientPatcher/Invoke-AAPakModulePatch.ps1` for the archive change. It saves the stock 512-byte header and encrypted file-table tail before adding the module, verifies the module hash and TOC entry after applying, and can restore the exact prior archive structure.

## Verification

Before an in-game test:

1. Build `AAEmu.Game` and restart the game server.
2. Validate the structural client patch with `Invoke-AAPakModulePatch.ps1 -Action Validate`.
3. Open Character Info and confirm a button appears at the right of each currency row.
4. Click Honor and confirm the store uses Honor Points.
5. Close it, click Vocation, and confirm the store uses Vocation Badges.
6. Purchase one inexpensive item from each store to verify the entire request/validation/currency path.
