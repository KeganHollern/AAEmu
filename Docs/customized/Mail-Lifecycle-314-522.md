# Mail expiry, return, and packet identification

Issues: [314](https://github.com/KeganHollern/aaemu-cluster/issues/314) and
[522](https://github.com/KeganHollern/aaemu-cluster/issues/522).
Source baseline: `c721206c` on `deployment/r208022`.

## Decision

The r208022 client uses C2G `0x0a3`, level `1`, for Return Mail.
The prior `CSReportSpamPacket` name was incorrect.
A spam report at that opcode would report a player when the receiver selects Return Mail.
The exact client `X2Mail.ReportSpam` binding sends no packet.
This change corrects the return registration and removes the false report stub.
It does not add a report endpoint, moderation report records, or client content.
Issue 522 needs correction as an incorrect packet identification, not a claim of completed spam reporting.

## Approved expiry policy

The user approved database archive retention on 2026-09-11 UTC.
Archived mail leaves the active mailbox. Its contents remain in SQL.
This batch does not add a staff recovery interface or automatic archive deletion.

The deadline is `received_date + 14 days`, with an inclusive expiry boundary.
Read status and `open_date` do not extend this deadline.
The delivery notification flag does not determine expiry or return eligibility.
The task uses one UTC time snapshot for each scan.

| Mail at expiry | Result |
| --- | --- |
| Normal or Express, not returned, with contents and a valid distinct sender | Return the exact contents once, with no postage charge |
| System mail with contents | Archive |
| Already-returned mail with contents | Archive |
| Player mail with contents and no valid sender, or self-mail | Archive |
| Any mail without contents | Remove the active row and keep its terminal source snapshot |
| Return destination temporarily full or unavailable | Keep the source and try again on the next scan |
| Unresolved, foreign, duplicate, or shared item reference | Keep the source for repair and do not move its contents |

All 3 amount fields count as contents when nonzero.
Billing amounts remain billing metadata. The archive does not convert them to currency.
Returned mail gets a new ID, an unread state, `returned=true`, and a fresh 14-day deadline.
Normal and Express returns arrive immediately. A returned mail cannot return again.
The sender ID determines the destination, even if the sender changed the character name.
The SQL `deleted` field determines whether the sender remains active.
A stale entry in NameManager cannot make a deleted sender eligible.
The transaction checks this state again before it stores a return or missing-sender archive.
An unavailable sender query defers the transition. It does not prove that the sender is missing.

Manual Return Mail accepts only a delivered, unexpired, owned Normal or Express mail with a valid distinct sender.
Read mail can return. Manual return rejects already-returned mail and self-mail.
At the exact expiry boundary, the scheduled expiry policy owns the transition.
Expired mail cannot supply attachments or appear in a fresh mailbox list.
Auction receipt replay still uses the current durable claim path when the original mail no longer exists.

Character removal now processes only that character's received player mail through the same transaction.
This forced transition preserves undelivered and expired contents without the manual packet's timing gate.
It no longer calls the old in-place header swap or processes every player's mail.

## Persistence and asset retention

The additive update is `SQL/updates/2026-09-11_aaemu_game_mail_lifecycle.sql`.
The base schema contains the same definitions.
The update creates 2 tables and does not change current mail or item rows.

`mail_lifecycle` stores one immutable terminal record per source mail ID.
It contains the outcome, UTC time, new return ID, actor character ID, and versioned source JSON.
The JSON preserves the mail fields, all amount fields, and the ordered attachment IDs.
Automatic expiry and character removal use actor ID `0`.
Mail ID allocation reserves terminal source IDs after restart.

`mail_archive_items` stores each archived item ID, source mail ID, and a snapshot of every `items` column.
BLOB values use base64 in JSON. SQL NULL values remain JSON null.
The original `items` row stays unchanged. Related pet, vehicle, UCC, and other rows stay unchanged.
The live item registry and container no longer hold archived items after commit.
The startup item loader excludes archived IDs, so a restart cannot make those items claimable.
No archive transition deletes an item row or releases an item ID.

The shared persistence lock covers source validation, item moves, mail changes, commit, and notifications.
The economy checkpoint saves the source changes, returned contents, removal, and terminal record in one transaction.
An archive snapshot uses the item row from that same transaction, after pending item changes reach SQL.
A known failure restores prepared live state and keeps the source for a retry.
An uncertain commit result stops the Game process before a later save can lose the once-only record.
This follows the current auction claim rule for uncertain economic commits.
No notification precedes commit. A notification failure cannot restore a removed source.

Current damaged mail with unresolved item references remains in SQL for repair.
The normal save skips that mail, so it cannot overwrite the unresolved references.
The normal startup warning identifies the unresolved mail and item IDs.

## Exact-client evidence

`client/history.txt` identifies version `208022`.
Both PE files are x86 DLLs with timestamp `2014-10-14 05:44:21 UTC`, preferred base `0x38ff0000`,
entry RVA `0x008c5d6d`, and image size `0x01cb0a00`.

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| `client/bin32/x2game.dll` | 18483712 | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| `.tools-re/dumps/x2game.dumped.dll` | 30085120 | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |

The source DLL is packed. Analysis used the current local dump and Ghidra `12.1.3` with JDK `21.0.12.1`.
The original dump command is not present in this record. This is a provenance gap, not a new capture claim.
An isolated copy of the prior native project prevents shared project locks.
The absolute Return and Delete serializer pointers agree with the PE base.
No manual rebase was necessary.

The local script is `.tools-re/mail-314-522/AnalyzeMailLifecycle.java`.
Its SHA-256 is `56e1f4dbab032fda3ff483ff447103caf2a012f472f7328ee91605130d9daf09`.
The prior `AnalyzeMailPackets.java` and `DecompileMailChain.java` scripts supplied useful address references.
No external opcode map determines the new registration. The producer supplies the opcode directly.

| Native location | Confirmed observation |
| --- | --- |
| `0x3948fb10` | Registers `ReturnMailById` with `0x3948b630` and `ReportSpam` with `0x3948b310` |
| `0x3948b310` | Calls only the Lua return helper at vtable offset `0x2c`. It sends no packet |
| `0x3948b630` | Parses the string ID with `XlSafeAtoi64` and calls `0x393cd290` |
| `0x393cd290` | Checks the received mail entry and returned flag, sets opcode `0x0a3`, then sends the object |
| `0x397ac080` | Stores the ID as 2 adjacent 32-bit halves at object offsets `0x10` and `0x14` |
| Vtable `0x399d39f8`, serializer `0x397ba450` | Serializes the single 64-bit value at object offset `0x10`, then returns |
| `0x3948b5d0`, `0x393cd1d0` | Independent comparison: Delete Mail uses a boolean and a 64-bit ID, opcode `0x0a1` |
| `0x391c45a0` | Registers the returned-mail response with opcode `0x121` |
| `0x391db000` | Handles `OnMailReturned`: old mail ID at object `+0x10`, returned header at `+0x18` |
| `0x393ce500` | Removes the old received entry and inserts the returned header into the sent list |

The C2G body has exactly 8 bytes: one little-endian signed 64-bit mail ID at body offset `0`.
The in-memory vtable and packet type are not body fields.
There is no empty form, report reason, sender ID, or trailing group.
The server rejects nonpositive IDs, truncated bodies, trailing bytes, and a foreign receiver.
The response uses the current `SCMailReturnedPacket` body: old ID followed by the new mail header.

Fresh archive extraction used the repository `aapak.py` helper against `client/game_pak`.
The 32-bit Lua 5.1 helper read the original float-number bytecode.

| Archive path, after `game/scriptsbin/x2ui/mailbox/mail/` | SHA-256 |
| --- | --- |
| `read_mail.alb` | `9c618829778b07158ce39476220f16d5d8b8d2129c0361e840d1fa7984af7aa0` |
| `mailbox.alb` | `f60c90ca058382a2006235d047508506342def8d49b3d183fc1a19d7ac6132f0` |
| `tab_received.alb` | `019c9145590e78cbe34cab79b54ad74ecaf5130ea1ef2bbc12f12f1db476f147` |

`read_mail.alb` lines 1304-1307 call `X2Mail:ReturnMailById(window.mailId)` after the return dialog.
Its widget logic shows Return for user mail and disables Return for self-mail.
`mailbox.alb` registers `MAIL_RETURNED` and updates the mail display through that event.
The 10 mailbox scripts contain no `ReportSpam` reference.
The Lua evidence identifies the action. The native producer and serializer establish its wire contract.

## History comparison

The official AAEmu `CSReportSpamPacket` still contains the same read-and-log stub.
The current upstream MailManager history provides no reusable expiry consumer.
Those versions do not override the exact r208022 producer.
Retained source history includes `61f535c0` for economy checkpoints and `0bcc2387` for auction claim serialization.
This change preserves those settlement paths and their tests.

## Validation and release evidence

The full solution build passed.
The final complete runs passed 2,620 unit tests and 148 GameMySql tests on 2026-09-11 UTC.
The GameMySql run used the current loopback MySQL service and a randomly named disposable schema.
The unit run took 16.975 seconds. The GameMySql run took 19.646 seconds.
Tests cover deadlines, read and unread mail, player and system mail, all amount fields, and full item-row preservation.
They also cover full destinations, missing senders, returned-mail expiry, failed SQL writes, restart, duplicate requests, and lost commit acknowledgements.
Packet tests cover the exact body, receiver ownership, nonpositive IDs, truncation, trailing bytes, and post-commit response timing.
The disposable MySQL fixture applies the additive update from absent tables and applies it again.

The read-only live preflight found 3 expired mails with 3 item references and no currency.
Two mails use type `16`, status `0`, and `returned=0`.
One mail uses type `31`, status `0`, and `returned=0`.
All 3 use the approved archive path. No live write occurred during this development task.
The aggregate query is `Mail-Lifecycle-314-522-Preflight.sql` beside this record.

No manual client validation occurred during development.
After release, check Return Mail with an item and copper, the received and sent lists, unread counts, and a second return attempt.
Check the 3 archive records and their original item rows without exposing mail content or item details in public logs.
