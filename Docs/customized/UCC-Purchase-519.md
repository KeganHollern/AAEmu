# UCC crest purchase rules for r208022

Issue [519](https://github.com/KeganHollern/aaemu-cluster/issues/519) described an unchecked crest purchase. The exact client shows 2 corrections to its proposed rules. The normal crest printer is doodad 3038 with `DoodadFuncStampMaker`. DDS uploads have several valid sizes.

## Confirmed client evidence

The client identifies itself as `version 208022`. Native addresses below use each file's preferred image base.

| Input | SHA-256 |
| --- | --- |
| `client/bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| `.tools-re/dumps/x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| `client/bin32/cryrenderd3d9.dll` | `d09ae4561edc447b7a3b3e9ad0c6cd32e2abb5f4a7b5c654c24b9181c3cabdee` |
| `game/scriptsbin/x2ui/ucc.alb` | `eec9ebf67f397f74a14337be5ee3df815e2792a896726ba3edea7b8344b36bf3` |

The x2game source and dump share timestamp `543cb835`, image base `38ff0000`, entry point `008c5d6d`, and image size `01cb0a00`. The dump came from the current workspace. The original capture method is not part of this change's evidence.

`GetMakeUccConsumeInfo` at `0x394b5e80` and `UploadEmblem` at `0x393dcec0` read the printer's StampMaker rule. Simple crests use its copper cost. Custom images use its material and count, with 0 copper. The current compact gives printer 3038 an output of item 17663, a simple cost of 50,000 copper, and a custom cost of item 11127 x1. Both current StampMaker rows for 3038 have these values. Doodad 2633 uses the separate legacy `DoodadFuncUccImprint` path. It does not supply a StampMaker rule, so it cannot authorize this purchase. Its X2UI `OPEN_EMBLEM_IMPRINT_UI` handler at source lines 458-460 only writes a chat log entry.

The X2UI script at source lines 358-392 shows separate copper and custom material dialogs. Lines 415-424 pass background and foreground pattern IDs, or 0 for an absent layer. The foreground must exist for a simple crest. `emblem_patterns.kind_id` identifies background kinds 1-3 and foreground kind 4.

Renderer conversion at `0x3815c0e0` accepts positive power-of-two dimensions through 256. It keeps supported compressed input or converts an image to DXT5. DDS writer `0x381b94c0` and size function `0x381a5370` define the 128-byte header and compressed mip sizes. Supported output FourCC values are DXT1, DXT3, DXT5, and ATI2. The maximum valid 256x256 mip chain is 87,536 bytes. These rules permit smaller valid images. The header and payload must agree on format, dimensions, and mip count.

## Confirmed stream contract

| Packet | Body |
| --- | --- |
| CT `0x0e` start | 3-byte printer object ID, u64 initial UCC ID, i32 DDS byte count, 2 u32 pattern IDs, 9 u32 color channels, u64 FILETIME |
| CT `0x0c` part | i32 total bytes, i32 part bytes, u32 zero-based index, u16 byte-array length, exact part bytes |
| CT `0x0d` completion | u8 status. 0 means success. 1 means client upload failure. |
| TC receive status | u8 status and i32 count. 0 or 1 advances upload, 2 ends it, and 3 fails it. |

Producer `0x393da640`, constructor `0x393d97f0`, and serializers `0x397bcfc0`, `0x3983aec0`, and `0x3983ac30` support the start body. The producer uses `XlGetCurrentFileTime`. Part producer `0x393da5b0` uses sequential 3,096-byte parts. Completion producer `0x393d9d80` sends status 0 only after all parts. Receive handler `0x393da8b0` resets upload state on status 3. AAEmu's readers provide the independent field-order check.

## Server behavior and tests

The server checks the active character, current printer interaction, world instance, 5 m range, current StampMaker rule, patterns, and colors. It rejects a second active upload. It checks the byte declaration before storage, every part before append, and the complete DDS before purchase. Disconnect removes the exact connection's pending upload.

The completion packet starts one inventory mutation. A simple crest debits copper. A custom crest consumes the authored material. The mutation creates the output with its UCC ID. The current economy checkpoint saves the wallet, material removal, output item, and crest row in one transaction. A known failure restores the assets. A repeated completion cannot charge again. The FILETIME conversion now matches the client.

The 62 focused unit cases cover valid bodies, exact status bodies, malformed lengths, pattern and color rejection, DDS bounds, part order, cancellation, duplicate start, movement, character replacement, funds, capacity, and commit failure. The 4 disposable MySQL cases cover simple and custom success, rollback after the crest insert, exact persisted costs, output reload, and repeated completion. No schema or compact change is needed.

The packet layout, cost rule, and DDS limits have native and script evidence. A live r208022 purchase still needs human validation. Create 1 simple crest and 1 custom crest at printer 3038. Check the shown cost, output image, reconnect result, and unchanged assets after each rejected purchase.
