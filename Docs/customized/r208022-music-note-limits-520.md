# Artistry limits for sheet music

This change addresses aaemu-cluster issue #520 in the `deployment/r208022` fork.
The server now checks the current Artistry step before upload and before paid sheet creation.
The second check prevents a rank downgrade from bypassing the limit.

## Exact client contract

The client history identifies revision `208022`.
The original `x2game.dll` SHA-256 is `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205`.
The analyzed runtime dump SHA-256 is `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0`.
Both have PE timestamp `0x543cb835`, image base `0x38ff0000`, and entry RVA `0x8c5d6d`.
This research reused the previous runtime dump. It did not repeat the dump procedure.

The active `music/toc.g` loads `game/scriptsbin/x2ui/music/user_music_view.alb`.
The script gets Artistry ID `33`, then selects the composition limit for its current grade.
It compares `scoreEdit:GetTextLength()` with that limit.
Native `PreSaveNotes` at `0x390799e0` and `SaveNotes` at `0x39079640` repeat that check.
The getter at `0x39082130` reads the stored Artistry step and the `music_note_limits` table.

`xlcommon.dll` export `XlStringLen` at `0x3301b000` counts Unicode code points in valid UTF-8.
Spaces, line breaks, combining marks, and MML control syntax count individually.
A supplementary character counts once.
The check does not count parsed musical notes, UTF-8 bytes, or UTF-16 code units.
The server preserves the text without normalization or a new MML grammar.

The client and server compacts contain the same limits:

| Artistry step | Maximum code points |
| --- | --- |
| 0 | 200 |
| 1 | 400 |
| 2 | 600 |
| 3 | 800 |
| 4 through 10 | 1000 |

The server compact SHA-256 is `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.
The client compact SHA-256 is `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4`.
Neither compact changes in this release.

## Upload packet

The producer at `0x39081e60` sends C2G opcode `0x122`, level `1`.
Constructor `0x397acd20` and serializer `0x397bc740` establish this body:

| Field | Type and meaning |
| --- | --- |
| `noteLen` | Signed 32-bit UTF-8 byte count, without the terminal NUL. |
| `itemId` | Unsigned 64-bit source item ID. |
| `type` | 1-byte item container type. |
| `index` | 1-byte slot index. |
| `title` | 16-bit length and UTF-8 bytes, without a NUL. |
| `notes` | 16-bit length and UTF-8 bytes, including 1 terminal NUL. |

The native serializer has no trailing fields.
The established r208022 string contract and the old server reader corroborate the 16-bit prefixes.
This research did not separately decompile the CryNetwork string helper.
The native title capacity is 96 bytes. The native notes capacity is 12000 bytes plus the terminator.
The normal UI limits titles to 12 characters and scores to 1000 characters.
The native capacities are packet bounds, not substitutes for the Artistry limit.

The old `value2` comment incorrectly described a music rank.
Inventory lookup `0x3961e9a0` supplies the separate container and slot fields.
The server checks both against the owned source item in the bag.
Those fields never set the Artistry step.

The reader rejects inconsistent byte counts, malformed UTF-8, interior NULs, truncation, and trailing fields.
It removes exactly the transport terminator before the rank check.
The server uses `UserNoteCannotSave` for rejected saves.
The native client uses that error for preparation failure. This server response does not claim an identical native response to excessive length.

## State and persistence

`MusicNoteGameData` loads the authored limits as one snapshot.
Invalid rows stop the load, and a failed reload preserves the previous snapshot.
Missing character proficiency or an unknown step cannot authorize a save.
`MusicManager` checks the stored Artistry step rather than raw points or client fields.
Rejected uploads remove earlier pending text for that character.

`SkillLaborBatch` keeps sheet creation, paper consumption, labor, proficiency rewards, and the song row in the current transaction.
The final limit check occurs before paper consumption or output allocation.
Rank changes, uploads, and the transaction use the same persistence lock.
A failed transaction preserves the queued valid upload for a later retry.

The current MySQL `music` table uses `utf8mb3`.
It cannot store supplementary characters, even though the native counter can count them.
That storage restriction remains unchanged. The integration test checks that its rejection preserves the paper and labor.
No schema conversion or rewrite of saved songs forms part of this fix.

This change remains specific to the deployment fork.
Official AAEmu PR #1582 targets a newer client and uses different packet limits.
Its global maximum does not replace the r208022 character-rank check.

## Automated and human checks

The tests cover each authored rank, the exact limit, and the limit plus 1.
They also cover Unicode counts, packet fields and bounds, invalid sources, and stale uploads.
MySQL tests cover paid creation, a rank downgrade, rejection without item or labor loss, and transaction retry.

The following gameplay checks remain pending:

1. Create and play a valid sheet at the novice limit. Check the displayed count, paper, labor, and saved text.
2. Enter 1 character above the limit. Check that the client prevents the save and preserves paper and labor.
3. Repeat at a higher Artistry step. Check that the larger authored limit applies.
4. Rejoin with the valid sheet. Check the saved text and playback again.

No human client test occurred during the source and native-code checks.
