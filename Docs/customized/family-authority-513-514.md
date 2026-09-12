# Family invitations and permanent membership changes

This change resolves cluster issues [#513](https://github.com/KeganHollern/aaemu-cluster/issues/513) and [#514](https://github.com/KeganHollern/aaemu-cluster/issues/514).
The source basis is AAEmu `a87384fdf7db13ebf8820fed8d284e5800ae8cf7`.

## Rules

The server records one pending invitation for each invited character.
The record keeps both character objects, both connections, the invited family, the title, and the expiry.
The invitation expires after 1 minute. This duration is server policy, not a recovered retail timer.
A duplicate invitation does not replace the first title or extend its expiry.

A reply needs the recorded inviter and invited character.
A declined or accepted reply consumes that invitation once.
The server rejects replies after expiry, logout, connection replacement, a new family membership, a steward transfer, or a receiver block.
The server takes the title from the invitation and ignores the reply title.
Titles must fit the current SQL limit of 45 characters and the native limit of 104 UTF-8 bytes.
Only the current steward can invite another member into a family.
Two characters without a family can create one through the same invitation checks.

The server permits at most 8 members. It checks the limit at invitation and acceptance.
The shared character persistence lock covers these checks and the membership change, including concurrent replies for the last place.
This also excludes a character autosave until live family membership matches the committed rows.
The family packet writer also limits its member list to the 8 entries that the native client supports.
This does not delete members from a pre-existing oversized family.

Only the steward can change titles, transfer stewardship, or remove another family member.
Each target must belong to that steward's family.
Unknown IDs, IDs in another family, and a transfer to the current steward cause no change.
The ordinary leave action rejects the steward until a transfer succeeds.

Character deletion uses a separate cleanup method.
Deletion of a steward disbands that family. This is server cleanup policy.
Deletion of an ordinary member follows the normal member removal path.
A family disbands when fewer than 2 members remain.

## Persistence

A title, steward, invitation acceptance, or member removal writes a staged family before it changes live membership or sends success packets.
The family write updates `family_members` and `characters.family` in the same transaction.
This makes a new family available to the normal family loader after a restart.
The write preserves unrelated character fields.

A failed write keeps the live family unchanged.
The pending removal list clears only after commit, so a rolled-back removal can retry.
No schema migration or compact change is necessary.

Character deletion invokes family cleanup after its broader deletion transaction.
This change keeps that current lifecycle. A database failure at that later step can leave cleanup incomplete.
The server logs the failed family write. This change does not add a new deletion transaction system.

## Exact client evidence

The input is client `r208022`, as recorded in `client/history.txt`.
The analysis used these SHA-256 values:

| Input | SHA-256 |
| --- | --- |
| Original `client/bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| Runtime `x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| `game/scriptsbin/x2ui/relationship/family_tab.alb` | `5978f8617c7ecd229d963a7015b93d45cd858b824fef28caaf82ccdfab9aa13d` |
| `game/scriptsbin/x2ui/components/popup_menu_proc.alb` | `8695e4ff818ab1320707755e4713eb4d6fb6a82b50e5a1fa638b29de913e3aa0` |

Addresses below use the runtime dump image base `0x38ff0000`.

| Conclusion | Status | Evidence |
| --- | --- | --- |
| Only the steward can invite into a current family. | Confirmed | `X2Family` registration `0x39474ec0` binds `Invite` to `0x39474840`, which calls `0x392ff770`. That producer calls `0x398a8770` and reports error 370 on failure. `0x398a81f0` matches the character ID and checks the member role byte. The popup script also checks `X2Family:IsOwner()` before an invitation into a current family. |
| A title contains at most 104 encoded bytes. | Confirmed | The family parser passes `0x68` to its title string reader at `0x398a86f7`. Each fixed member record reserves 105 bytes for the title and its terminator. |
| The native family contains at most 8 members. | Confirmed | Serializer `0x398a85b0` reads the count and limits it to 8 at `0x398a85e4`. Store insertion `0x398a8df0` allocates `0x788` bytes: an 8-byte header and 8 member records of `0xf0` bytes. |
| The ordinary steward cannot use the Leave menu. | Confirmed | `components/popup_menu_proc.alb`, function lines 940–1002, checks `IsOwner` at lines 989–995. That branch adds only the title action. The other branch adds `Leave` at line 997. The native `Leave` producer at `0x392ff8b0` only checks for family membership, so the server must also enforce the menu rule. |
| Member removal disbands a family below 2 members. | Confirmed | `0x398a8d40` removes the member through `0x398a8160`, compares the resulting count with 2, and removes the family below that count. |
| The inviter selects the title. | Confirmed | `family_tab.alb`, function lines 49–60, reads both edit controls and calls `X2Family:Invite(name, title)`. The native producer passes those values to `0x397ab560`. |
| Pending invitation expiry is 1 minute. | Server policy | The exact client does not establish the server's pending invitation lifetime. |

The C2G invitation and reply layouts stay unchanged.
The bounded member writer keeps the current field order: family ID, member count, then each member's ID, name, role, online flag, and title.
No extracted client file is part of this change.

## Tests and manual checks

`FamilyAuthorizationTests` covers unmatched replies, stored titles, replay, decline, expiry, changed sessions, receiver blocks, changed family or steward, duplicate invitations, and simultaneous replies.
It also covers the 7-to-8 member boundary, cross-family and unknown targets, non-stewards, failed writes, steward leave, deletion cleanup, and complete bounded packet bodies.
`FamilyPersistenceTests` uses the disposable MySQL fixture to check title and steward state after restart, atomic writes, cross-family scope, offline removal, and cleanup.

The release record supplies the final build and test results.
Human gameplay validation is still necessary:

1. Invite a character into a new family and into a current family.
2. Change a title, transfer stewardship, and reconnect both characters.
3. Fill a family to 8 members and try one more invitation.
4. Remove an offline member and check family access to a house or coffer.
5. Transfer stewardship and leave as the former steward.
