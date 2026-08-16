# Client modding workflow

This note covers local client experiments for ArcheAge 1.2.4.13 (r208022). Client binaries and `game_pak` are local inputs and must not be committed to the AAEmu repository.

## What the camera investigation established

The shipped camera limits are data-driven in three packed files:

| Virtual path | Stock values |
| --- | --- |
| `game/config/cvargroups/option_camera_fov_set.cfg` | action `10`, classic `18` |
| `game/config64/cvargroups/option_camera_fov_set.cfg` | action `10`, classic `18` |
| `game/config/client.cfg` | initial `18`, maximum `18` |

A loose copy of the first file was not honored by the release client. This is consistent with CryEngine release builds disabling direct asset-file access by default. The client binaries also contain no readable `sys_PakPriority` CVar that would safely switch the whole client to file-first loading.

The packed entries are plaintext and uncompressed. Their file-table hashes are zero, and each replacement below changes two ASCII bytes without changing an entry's size, padding, file table, or overall `game_pak` length. This makes an exact-offset patch substantially smaller and less invasive than rewriting the archive through `AAPacker.ReplaceFile`, which also rewrites the encrypted file table.

The gameplay module `bin32/x2game.dll` is protected on disk: its executable and read-only data sections have near-random entropy and its ordinary import metadata is not usable for static analysis. If the packed configuration test still encounters a clamp, the next stage is runtime inspection after the module has unpacked in memory.

## Integrated camera controls

The patched basic Screen settings page exposes two persistent controls while the player is in the world:

| Control | Stored option | Applied CVar | Range |
| --- | --- | --- | --- |
| Maximum Camera Distance | `AAEmuCameraMaxDistance` | `camera_max_dist` | `10`–`35` |
| Field of View | `AAEmuCameraFov` | `cl_fov` | `40`–`120` |

`35` remains the camera-distance maximum established by the data patch. The FOV option preserves the stock first-use value for the selected camera mode (`60` for action mode and `42.75` for classic mode) until the user moves the slider. Both callbacks clamp manually edited stored values before applying them. The named values use `OL_SYSTEM`, so the selections persist across client restarts. Their saved values are applied on `ENTERED_WORLD`, after the camera CVars have been registered; applying them while `screen_option.alb` first loads is too early for `camera_max_dist`. Saving the page applies them after the stock action/classic camera selector, preventing that selector from immediately overwriting the custom values.

The r208022 slider layout accepts either two endpoint captions or four-to-six captions. Three captions make the stock anchor table index zero and abort construction of the entire options window. Custom sliders must therefore stay within one of the supported caption counts; these controls use four.

`Tools/ClientPatcher/Build-WindowedFullscreenScreenOption.ps1` now builds one composable `screen_option.alb` containing both Windowed Fullscreen and the camera controls. It compiles `Sources/camera_controls_screen.lua`, transplants only the required callbacks and basic-screen frame, strips execution-irrelevant debug padding, and keeps the replacement exactly the same size as the archived r208022 module. Apply the compiled module with `Invoke-AAPakFileReplacementPatch.ps1` using virtual path `game/scriptsbin/x2ui/option/screen_option.alb`.

## Guarded patch tool

`Tools/ClientPatcher/Invoke-ClientPatch.ps1` consumes a build-specific JSON manifest. It checks that the pack is at least the target build's stock length and verifies every expected byte before making a change. The minimum-length check lets exact-offset patches remain composable with later AAPacker modules appended to the same archive. Apply and restore operations refuse to run while ArcheAge is open, use exclusive file access, flush changes to disk, verify every write, and preserve the original bytes in a small local backup next to `game_pak`.

Validate the camera manifest without writing:

```powershell
pwsh -NoProfile -File Tools/ClientPatcher/Invoke-ClientPatch.ps1 `
  -PackPath "D:\path\to\client\game_pak" `
  -ManifestPath Tools/ClientPatcher/Manifests/archeage-r208022-camera-distance-35.json
```

After closing ArcheAge, apply it:

```powershell
pwsh -NoProfile -File Tools/ClientPatcher/Invoke-ClientPatch.ps1 `
  -PackPath "D:\path\to\client\game_pak" `
  -ManifestPath Tools/ClientPatcher/Manifests/archeage-r208022-camera-distance-35.json `
  -Action Apply
```

Restore the exact original bytes:

```powershell
pwsh -NoProfile -File Tools/ClientPatcher/Invoke-ClientPatch.ps1 `
  -PackPath "D:\path\to\client\game_pak" `
  -ManifestPath Tools/ClientPatcher/Manifests/archeage-r208022-camera-distance-35.json `
  -Action Restore
```

The local backup is written to `.aaemu-client-backups` beside `game_pak`. Do not commit that directory.

## Lower-right HUD auction button

The r208022 auction shortcut in `game/scriptsbin/x2ui/hud/main_menu_bar/right_button_set.alb` has two independent client defects. Its tooltip is a hardcoded Korean literal, and its normal/hover atlas rectangles start 16–17 pixels below their actual artwork. The pressed artwork is correctly aligned, which is why holding the mouse button makes the icon appear to move into place.

`Build-HudAuctionButtonFix.ps1` creates a guarded replacement that moves only the normal and hover texture Y coordinates to `335` and resolves `AUCTION_TEXT` / `auction_title` directly through the stock localization API on hover. A cached `locale.auction.auction` lookup is too early for this HUD handler and can return no text. The click behavior, anchor, pressed/disabled artwork, and neighboring buttons are unchanged. See `Docs/customized/HUD-Auction-Button-Fix.md` for the exact rectangles, hashes, commands, and verification steps.

## Startup desktop-stall fix

The r208022 `CrySystem.dll` enumerates display modes during startup and calls `ChangeDisplaySettingsExA` with `CDS_TEST | CDS_FULLSCREEN` for every eligible mode. This happens even when the client is configured for windowed mode. On current Windows and graphics drivers, those repeated driver-level mode tests can make the mouse, Explorer, and the desktop compositor nearly unresponsive for several seconds.

`Tools/ClientPatcher/Manifests/archeage-r208022-skip-display-mode-tests.json` contains the confirmed fix. It preserves display-mode enumeration but replaces the repeated driver call with a successful result and the same stack cleanup the imported Windows function would perform. The manifest targets only the pristine r208022 `CrySystem.dll` by filename, length, original SHA-256, expected instruction bytes, and final SHA-256.

Validate without writing:

```powershell
pwsh -NoProfile -File Tools/ClientPatcher/Invoke-ClientBinaryPatch.ps1 `
  -BinaryPath "D:\path\to\client\bin32\CrySystem.dll" `
  -ManifestPath Tools/ClientPatcher/Manifests/archeage-r208022-skip-display-mode-tests.json
```

Apply after closing ArcheAge:

```powershell
pwsh -NoProfile -File Tools/ClientPatcher/Invoke-ClientBinaryPatch.ps1 `
  -BinaryPath "D:\path\to\client\bin32\CrySystem.dll" `
  -ManifestPath Tools/ClientPatcher/Manifests/archeage-r208022-skip-display-mode-tests.json `
  -Action Apply
```

Restore the pristine DLL:

```powershell
pwsh -NoProfile -File Tools/ClientPatcher/Invoke-ClientBinaryPatch.ps1 `
  -BinaryPath "D:\path\to\client\bin32\CrySystem.dll" `
  -ManifestPath Tools/ClientPatcher/Manifests/archeage-r208022-skip-display-mode-tests.json `
  -Action Restore
```

The binary patcher is idempotent, refuses unsupported or partially modified files, takes an automatic full-file backup in `bin32/.aaemu-client-backups`, writes with exclusive access, and verifies the resulting hash. The client executable and DLL remain local inputs and must not be committed.

## Pattern for future tweaks

1. Locate every packed source for a setting, including `config` and `config64` variants and option CVar groups.
2. Prove whether a loose override is loadable. Do not assume release clients read loose assets.
3. Prefer data or UI script changes over native-code patches.
4. Record the target client build, pack length, virtual path, exact expected bytes, and replacement bytes in a manifest.
5. Keep replacements the same byte length when possible. Larger replacements require `AAPacker` file-table updates; use `Tools/ClientPatcher/Invoke-AAPakModulePatch.ps1`, which backs up the original header and encrypted file table before adding a module.
6. Validate, back up the exact original bytes, apply while the game is closed, and export or reread the result for verification.
7. If a data patch is ineffective, inspect the unpacked runtime module for a second clamp before changing executable code.
8. For native-code fixes, use `Invoke-ClientBinaryPatch.ps1` and require exact original and patched file hashes in addition to exact instruction bytes.

## References

- [CryEngine: Accessing Files with CryPak](https://www.cryengine.com/docs/static/engines/cryengine-5/categories/23756813/pages/23306407)
- [CryEngine 3: Directory Structure and Pak Files](https://www.cryengine.com/docs/static/engines/cryengine-3/categories/1638401/pages/1605746)
- [CryEngine: Filesystem and release-build loading](https://www.cryengine.com/docs/static/engines/cryengine-5/categories/23756813/pages/26874879)
