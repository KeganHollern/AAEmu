# HUD auction button fix

This client-only patch targets ArcheAge 1.2.4.13 (r208022). It fixes the auction button at the lower-right of the HUD without changing the marketplace mail or marketplace buttons.

## Root causes

The button is created by:

`game/scriptsbin/x2ui/hud/main_menu_bar/right_button_set.alb`

The stock hover handler passes the Korean literal `경매장` directly to `SetTooltip`. This bypasses the client's localization tables even when the active client locale is English.

The button uses the `BUTTON_HUD.TOGGLE_AUCTION` skin. Its stock atlas rectangles are:

| State | Stock rectangle |
| --- | --- |
| Normal | `{284, 352, 59, 57}` |
| Hover | `{343, 351, 59, 57}` |
| Pressed | `{402, 351, 59, 57}` |
| Disabled | `{461, 351, 59, 57}` |

Pixel inspection of `game/ui/common/en_us/hud.dds` shows that the normal and hover artwork is centered at atlas Y `335`. Starting those two rectangles at `352` and `351` crops off their upper portion and leaves transparent space below, making the resting icon appear raised. The pressed and disabled artwork lives in different cells and is already aligned correctly.

## Patch behavior

`Tools/ClientPatcher/lua51_chunk.py patch-hud-auction-button` makes two guarded bytecode changes to `right_button_set.alb`:

1. Before the buttons are created, it changes only the second coordinate of the normal and hover rectangles to `335`.
2. It changes the hover handler to resolve `auction_title` directly through `X2Locale:LocalizeUiText` when the pointer enters the button.

The lookup uses the stock localization category and key:

`X2Locale:LocalizeUiText(AUCTION_TEXT, "auction_title")`

Resolving it at hover time is intentional. The cached `locale.auction.auction` path is not reliably populated when this early HUD handler first runs and can yield no tooltip.

The existing compact database contains localized values for `auction_title`, including `Auction House` for `en_us`, `Auktionshaus` for `de`, and `Hôtel des ventes` for `fr`. The database is queried read-only during investigation and is not modified by this patch.

The pressed and disabled rectangles, the button anchor, its click handler, and the other two HUD buttons are unchanged.

## Build and apply

Build from an exported stock r208022 module:

```powershell
pwsh -NoProfile -File Tools/ClientPatcher/Build-HudAuctionButtonFix.ps1 `
  -OriginalModulePath "D:\path\to\right_button_set.alb"
```

The stock module SHA-256 must be:

`99677CFFF60DAE8509E1359AC0ABADC8402D987F42AB53248AC2AF46401C2DEE`

The generated module SHA-256 is:

`B4AEF486CB97F4FA1EFBAF85323B657C745E4C662F934B1FF1B1A2DD9AF36CB5`

Apply it after closing ArcheAge:

```powershell
pwsh -NoProfile -File Tools/ClientPatcher/Invoke-AAPakFileReplacementPatch.ps1 `
  -PackPath "D:\path\to\client\game_pak" `
  -AAPackerDllPath "D:\path\to\AAPacker.dll" `
  -ReplacementFilePath Tools/ClientPatcher/Compiled/hud_auction_button.alb `
  -ModuleVirtualPath game/scriptsbin/x2ui/hud/main_menu_bar/right_button_set.alb `
  -PatchName archeage-r208022-hud-auction-button-fix-v2 `
  -ExpectedOriginalModuleSha256 99677CFFF60DAE8509E1359AC0ABADC8402D987F42AB53248AC2AF46401C2DEE `
  -Action Apply
```

Use `-Action Validate` to verify the state. Use `-Action Restore` to restore the exact previous module and file table. The guarded patcher stores the structural backup in `.aaemu-client-backups` beside `game_pak`.

## In-game verification

1. Start the client and enter the world.
2. Confirm the left auction icon is fully visible and vertically aligned with the other two buttons while idle.
3. Hover it and confirm the glowing hover art remains aligned.
4. Confirm the tooltip reads `Auction House` on an English client.
5. Press and release the button, then confirm the pressed art and auction window still behave normally.
