# User chat channels for r208022

Issue #516 adds player-created chat channels. The server keeps channel state for the current session only.
The complete feature also needs the X2UI changes in client content sequence 12.
This document records static evidence and server tests. It does not claim a human test pass.

## Exact client evidence

The research used the original r208022 `x2game.dll` and a local dump of that image.

| Input | SHA-256 |
| --- | --- |
| Original `x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| Local `x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| Original compact | `784d362434a2a0fd0a29fbc1bbb7f771d9ffbe2c8568339ac54d6fdcf44d2ed7` |

Both native files identify image base `0x38ff0000`, PE timestamp `0x543cb835`, and entry RVA `0x8c5d6d`.
`client/history.txt` identifies revision 208022. The original capture method of the reused dump is not known.
The ignored research directory contains the decompiles, assembly, traces, and Lua listings.

The packets use the normal level-1 Game route. Strings use a u16 byte count followed by UTF-8 bytes.
Each key uses 64 bits. Its low signed 16 bits contain the chat type. Type 15 means a user channel.
The client compares the complete key for user-channel membership.

| Packet | Opcode | Complete body |
| --- | --- | --- |
| `CSJoinUserChatChannel` | `0x61` | name string, password string, create bool |
| `CSLeaveChatChannel` | `0x62` | u64 key |
| `CSSendChatMessage` | `0x63` | u64 key, target string, message string, language u8, ability i32, 4 pairs of u16 start and u16 length |
| `SCJoinedChatChannel` | `0xcc` | u64 key, name string |
| `SCLeavedChatChannel` | `0xcd` | u64 key |
| `SCChatMessage` | `0xce` | u64 key, speaker object ID in 3 bytes, character ID u32, language u8, race u8, faction u32, speaker name string, message string, ability i32, option i32 |

Lua registration `3945aa00` maps Create, Join, Leave, and Send to `394580d0`, `39458100`, `39458130`, and `39458150`.
Create and Join call `393c3810`. Constructor `397ab6c0` and serializer `397b0790` prove the 48-byte name and 6-byte password limits.
These limits exclude the final native NUL byte.
The producer does not call `XlIsValidName`. The local ChatTab policy is not a channel-name policy.

Leave producer `393c38b0`, constructor `397ab700`, and serializer `397b9370` copy the complete key.
Chat constructor `397ae4e0` limits the target to 128 bytes and the message to 1023 bytes.
Serializer `397c4f50` writes all 4 trailing ranges. Their meaning is not confirmed, so the server consumes them without interpretation.

Joined parser `397b4540` reads the key and name. Leave parser `397b45b0` reads the key.
Chat parser `397c2420` reads the complete message body in the table.
The server keeps the current built-in G2C fields. The new user-channel overloads write the complete user key.

## Approved server rules

The user approved these rules because the recovered client does not define them:

- A character can join up to 5 user channels.
- Members must have the same current faction allegiance.
- Channel names use case-insensitive matching.
- The server removes a channel when its last member leaves.

Allegiance means `MotherId` when it is nonzero. Otherwise, it means the current faction `Id`.
This lets Nuian and Elf characters share channels. Pirates use a separate allegiance.
The server does not use race, duel state, or a friendly relation as a substitute for current allegiance.
Names are separate for each allegiance. `OrdinalIgnoreCase` compares names, and the server preserves the creation spelling.

Names need 1 to 48 UTF-8 bytes. The server rejects controls, invalid UTF-16, supplementary characters, and outer Unicode whitespace.
Internal spaces and other printable BMP characters are permitted. The shared authored reserved-name rules also apply.
The server rejects `|` because the client uses it as a rich-text and link delimiter.
These name restrictions are explicit server UI rules. They are not claims of a recovered native channel policy.

Passwords use 0 to 6 UTF-8 bytes. An empty password makes a channel public.
Passwords preserve case and spaces. They reject controls, invalid UTF-16, and supplementary characters.
The server does not log or persist passwords.

The native error map at `3956b860` proves these values:

| Error | Value | Server use |
| --- | --- | --- |
| `ChatChannelAlreadyExists` | 269 | Create uses an occupied name. |
| `ChatNoChannel` | 270 | The requested channel does not exist for this allegiance. |
| `ChatAlreadyJoinedChannel` | 271 | The character already joined the channel. |
| `ChatPrivateChannel` | 272 | A private channel needs a password. |
| `ChatWrongPassword` | 273 | The supplied password does not match. |
| `ChatNotJoinedChannel` | 467 | Send or Leave uses an unknown key or lacks membership. |

The private-channel and wrong-password distinction is a server choice. The native map proves the error values, not that distinction.
The server sends a private notice for invalid text or the 5-channel limit. No matching native error was proved.
User messages pass through the current command, moderation, level, and spam checks before channel dispatch.
The current User chat minimum stays at level 0. The change adds no new level restriction.

## State and concurrency

`ChatManager` owns `UserChatChannels`. The registry holds names, keys, passwords, and exact character/connection memberships.
Channel keys use an increasing counter in the high 48 bits. The low 16 bits contain 15.
The registry never reuses a key during its lifetime. A stale key cannot select a later channel.

A private registry lock serializes creation, membership changes, sends, and the narrow character faction assignment.
`Unit.SetFaction` calls `UserChatChannels.ChangeFaction` for a character. That boundary sets the faction and removes incompatible memberships.
Broadcast, buff, housing, and team callbacks run after the registry lock is released.
The registry does not take the persistence lock or a buff lock. This avoids the inverse lock order of a broad `SetFaction` lock.

Every send also checks the current faction and connection of each member.
Disconnect calls `LeaveAllChannels`, which removes the exact character instance.
A delayed cleanup from an old session cannot remove a replacement character instance.
Channels and passwords do not survive a Game restart. Membership does not survive logout.
The native reset at `391b9640`, through `393c6580` and `393c60f0`, restores the 15 built-in entries.
No native automatic rejoin or persisted membership source was confirmed.

## Client dependency

Joined handler `391d8a10` reaches `393c63e0` and assigns a free local index at or above 15.
The native vector grows. It does not prove a numeric membership limit.
The Send Lua API takes this local index, not a server key or the constant 15.
`GetChatChannelName`, through `394583d0` and `393c2160`, reads the same vector.

Incoming chat handler `391d8b00` reaches `3913ac13` and `393c7bb2` through the traced native thunk.
For a user key, it finds the joined entry and emits `CHAT_MESSAGE` with that local index.
An unknown key produces no chat event. There is no separate native user-channel display path.

Stock `chat_msg_event.alb` has an empty `CHAT_USER` branch at authored lines 452–453.
Higher local indexes also produce no displayed message. The stock chat scripts do not call the Create, Join, or Leave APIs.
Client content sequence 12 adds an independent channel dialog and a display path for all local indexes at or above 15.
The dialog must wait for confirmed join/leave events and use the exact current native index and name.

## Automated and human validation

The focused server tests cover:

- Complete packet bodies, strict UTF-8, byte limits, flags, and the 16-byte chat tail.
- Distinct channel audiences, exact keys, passwords, and confirmed errors.
- Concurrent case-equivalent creates and the 5-channel membership limit.
- Forged keys, stale sessions, leave, disconnect, and empty-channel removal.
- Faction boundaries, a faction change during delivery, and callbacks outside the registry lock.
- Account mute, account spam limits, the current level minimum, and unchanged built-in packet fields.

Human validation must use the coordinated server and sequence-12 client release.
Check at least 2 simultaneous channels, because the second local index exceeds 15.
Check public and private joins, wrong passwords, names, leave/rejoin, logout cleanup, and built-in chat.
Two characters are needed to check audience boundaries and faction isolation.
The HUMAN VALIDATION issue tracks these pending gameplay checks.
