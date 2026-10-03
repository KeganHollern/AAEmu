# Name rules for r208022

Issue #438 covers names only. Chat-message filtering remains separate in issue #595.
The server checks new input before a name write or a charged portal action.
The change does not alter saved names or replace the current character and summon case normalization.

## Exact client evidence

The research used these local inputs:

| Input | SHA-256 |
| --- | --- |
| Original `x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| Reused local `x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| Original `xlcommon.dll` | `0e0881aa837553d7e307a5c6f9d886f3e82a0d72d94b6d666448aedaa94e2203` |
| Stock r208022 compact | `784d362434a2a0fd0a29fbc1bbb7f771d9ffbe2c8568339ac54d6fdcf44d2ed7` |
| Server compact | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |

Both x2game files identify the same image base `0x38ff0000`, PE timestamp `0x543cb835`, and entry RVA `0x8c5d6d`.
`client/history.txt` identifies revision 208022. The original capture method of the reused dump is not known.
Raw decompiles, assembly, table bytes, Lua listings, and data queries remain in the ignored task research directory.

`xlcommon!330095c0` exports `XlIsValidName`. `33009650` exports `XlGetNamePolicyInfo`.
The latter selects a 24-byte record at `3305db78 + (locale * 6 + type) * 24`.
The Lua getter `394c8ca0` proves the field offsets:

- Offsets 0 and 4 hold the local minimum and maximum.
- Offsets 8 and 12 hold the English minimum and maximum.
- Offset 16 holds the allowed space count. Zero means no numeric limit when spaces are allowed.
- Offset 20 holds space, mixed-language, mixed-case, and special-character flags at bits 0, 1, 2, and 4.

Bindings at `394caada` through `394cacb0` prove the types:
Character 0, Summons 1, Faction 2, FamilyTitle 3, ChatTab 4, and Portal 5.
`33019950`, `3301af90`, and the pointer table at `33075308` prove the locale IDs:
0 `ko`, 1 `zh_cn`, 2 `en_us`, 3 `ja`, 4 `zh_tw`, 5 `ru`, 6 `de`, and 7 `fr`.
The server uses `AppConfiguration.DefaultLanguage` as its policy selector.
A client packet cannot select a different policy.

## Length and glyph rules

The native length function `3301b000` counts UTF-8 code points.
An initial ASCII letter selects the English minimum. Other initial characters select the local minimum.
The English, German, and French records are identical:

| Type | Local minimum | ASCII-first minimum | Maximum | Internal spaces | Mixed case |
| --- | --- | --- | --- | --- | --- |
| Character | 2 | 2 | 26 | No | No |
| Summons | 2 | 2 | 26 | No | No |
| Faction | 2 | 3 | 32 | Yes | Yes |
| FamilyTitle | 2 | 2 | 26 | No | Yes |
| ChatTab | 2 | 2 | 10 | Yes | Yes |
| Portal | 3 | 3 | 32 | Yes | Yes |

The locale branches are `330091a0` for English, German, and French, `33009320` for Russian, and `33008f80` for the others.
The glyph helpers are `33008eb0` and `33008df0`.

| Locale | ASCII-first branch | Other first character |
| --- | --- | --- |
| `en_us`, `de`, `fr` | U+0020–007F or U+00A0–017F | Same ranges |
| `ru` | ASCII letters | U+0410–044F, U+0401, U+0451 |
| `ko`, `zh_tw` | ASCII letters | U+AC00–D7AF |
| `zh_cn` | ASCII letters | U+4E00–9FFF |
| `ja` | ASCII letters | Native UTF-16 glyph branch |

The Traditional Chinese Hangul branch exists in this exact binary. This change does not invent a correction for that locale.
The special-character flag bypasses the glyph check. Length, space, and applicable case checks still apply.
All authored mixed-language flags are false. The English branch does not read that flag.
The Asian non-ASCII-first branches do not apply the uppercase check.
Other applicable branches reject uppercase characters after the first when mixed case is disabled.
`3301a910` uses an ASCII table below U+0080 and CRT `iswupper` below U+0800. Higher values return false.
The server uses the corresponding .NET uppercase classification. Exact CRT behavior outside ASCII remains a validation limit.

Native buffers hold 128 UTF-8 bytes plus NUL for character, mate, house, slave, faction, and portal names.
Family title constructor `397ab600` uses a 104-byte payload plus NUL.
The server checks both character count and the relevant byte capacity.

## Route-specific data checks

`39572620` loads `allowed_name_chars`. The checker `395728b0`, through `395729d0`, has no locale condition.
It accepts ASCII letters and literal space directly. Other glyphs must match an authored UTF-8 entry.
Both compacts contain the same 2352 entries: 26 lowercase ASCII letters and 2326 Hangul syllables.
The sole recovered direct caller is faction creation `392f9020` at `392f9070`.
That caller first checks Faction policy 2, then the authored allowlist.
Thus its English path accepts ASCII letters and spaces, despite the wider common glyph rule.
The server applies this extra authored check to faction names. It does not apply the Korean table to every name type.

`397290a0` loads `blocked_texts`. `397a7540` selects only `check_name=true` rows.
For exact rows it calls `33019cd0`, which uses byte `_stricmp`.
For partial rows it calls `33019d60`, which scans bytes with `_strnicmp`.
Both compacts contain 15 name rows: 6 exact and 9 partial. All 15 strings are ASCII.
The server uses ASCII case folding with the authored match type. It does not normalize Unicode or change the stored name.
For example, `GM` is an exact rule and `Admin` is a partial rule.
Rows with `check_name=false` do not affect this change, regardless of `check_chat`.
The native blocked matcher has one recovered direct caller, faction creation at `392f9094`.
The common server check across public name routes is server enforcement of the authored name rows.
It is not proof that every client producer called this matcher.

## Input corrections and route evidence

The edit dialog calls `CheckAvailableName` at `394c8940`, then `XlIsValidName`, before it enables OK.
Outside text composition, the helper uppercases an initial letter and lowercases later letters.
Character, mate, house, and slave producers then lowercase the initial letter before send.
These transformations are separate from the blocked-name comparison.

The recovered routes are:

- Character producers `391a4390` and `391a4640` check type 0.
- Mate `39069860`, house `3932be40`, and slave `394aa0c0` check type 1.
- Family title uses the type 3 dialog, then `394748d0` and `392ffb60`.
- Portal save checks type 5 in `394cace0`, then uses `3938f7d0`.
- Portal rename uses the type 5 dialog, then `394cad10` and `3938f950`.

All policy minima exceed zero. The recovered edit dialogs have no empty-name reset action.
The family invitation form uses a separate input path.
`family_tab.alb` lines 40–43 enable Add only when both input strings are nonempty. Line 56 sends the name and title.
The popup `ActiveInviteFamily` opens that form and focuses the title field.
Native callback `39474840` calls `392ff770`, which sends opcode `0x1a` without a full name-policy check.
The chat-command caller `393c86c0` needs at least 2 parsed arguments and sends them as name and title.
The common server title rules also apply to these invitation writes. This is a server policy, not a copied producer check.
The server rejects empty player title writes. It keeps initial member defaults and saved titles unchanged.
It also rejects empty house and portal input before it can change state.

Native validation always rejects a leading period.
Native whitespace means ASCII U+0009–000D or U+0020. NBSP is not native whitespace.
The English glyph range permits punctuation, including `<`, `>`, backslash, and `|`.
No extra punctuation filter appears in the recovered edit dialog or summon producers.
This does not prove that every sequence displays as literal text in every client label.

The server rejects invalid UTF-16, supplementary characters, and controls before name validation.
This avoids native control acceptance and unproven supplementary behavior.
The server also checks the actual final character for whitespace.
The English and Russian native checks instead index a UTF-8 byte with the character count.
Assembly at `33009254` through `3300925d` proves that indexing defect.
These are explicit server corrections, not claims of exact native behavior.

`VNT_CHAT_TAB` describes a local chat tab. It does not describe a player-created channel.
The channel producer `393c3810` and constructor `397ab6c0` use separate 48-byte name and 6-byte password capacities.
That producer does not call `XlIsValidName`.

## Source and validation limits

The shared rules are in [NameRules.cs](../../AAEmu.Game/Models/Game/Names/NameRules.cs).
The authored rows load in [NameGameData.cs](../../AAEmu.Game/GameData/NameGameData.cs).
The focused rule tests are in [NameRulesTests.cs](../../AAEmu.UnitTests/Game/Models/Game/Names/NameRulesTests.cs).

The wider valid character range makes accent identity important.
The current SQL collation treats `Eva` and `Éva` as equal in a name query.
Friend lookup, pending-deletion lookup, and moderation targets now resolve the unchanged NameManager key before an ID query.
The moderation path keeps explicit character IDs and `account:` targets.
This prevents an offline accented name from selecting a different character or account.
The change does not alter SQL collations or the name keys of saved characters.

This static research does not claim a human test pass.
Human checks must cover accented names, case normalization, faction restrictions, title and portal input, and unchanged saved names.
Name-label markup and non-ASCII CRT case behavior remain explicit limits of this research.
