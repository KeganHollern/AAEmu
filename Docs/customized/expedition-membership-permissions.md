# Guild membership and permissions

This change completes aaemu-cluster issues #511, #512, and #521.
It uses the existing expedition tables and does not need a schema update.
The [native contract record](social-native-contracts-r208022.md) contains the exact-client packet evidence.

## Membership

An invitation lasts 1 minute and belongs to the current sender, receiver, and guild objects.
The receiver must reply with the matching guild and sender IDs.
The server checks current sessions, blocks, invitation permission, membership, and expiry again before acceptance.
A guild can contain at most 100 members, including its owner.
Kegan selected this custom server limit on 2026-09-12.
The exact client does not supply a numeric guild limit.
Creation, invitation, and acceptance apply the same limit.
Concurrent replies cannot take the same final place.

The guild, sender, and receiver must share their mother faction.
The server resolves the guild's saved base faction through the current faction data.
A faction without a mother uses its own ID.
This keeps the Nuian and Elf factions in the same alliance and rejects a hostile alliance or an unrelated root faction.

The guild save writes both `expedition_members` and `characters.expedition_id` in one transaction.
A reconnect does not depend on the next periodic character save.

## Authority

A kick or role change needs the actor's matching policy permission.
The target must have a lower current role than the actor.
The target cannot be the owner or role 255.
A new role must exist and stay below the actor's role.
Only the recorded owner with role 255 can change policies, transfer ownership, rename, or disband the guild.
The owner cannot leave before an ownership transfer.
The ownership transfer saves both the owner ID and owner name.

A policy update saves its name and every flag, including chat and siege flags.
A clan chat message needs current membership and the role's `Chat` permission.
This change does not add Dominion or siege systems from issue #143.

## Rename and disband

Guild creation and rename use the same name check.
The current settings allow 3 through 32 characters from the configured letters and spaces.
A name must contain a non-space character and cannot match another guild name without case sensitivity.
A rename packet must identify the sender's guild and set `isExpedition=true`.
The normal rename notification refreshes the client faction cache and nearby guild tags.

A disband first deletes the guild, members, role policies, and character links in one transaction.
It then clears current member links, removes invitations and the guild registry entry, and releases the guild ID.
Nearby players get the member tag changes.
All clients get the successful disband event, which removes only the specified faction.
New logins do not receive a deleted guild record.

A save failure before commit restores changed membership, roles, policies, ownership, or name before any success notification.
A failed removal save keeps its deletion record until the transaction commits or the manager restores the removed member.
If a commit or its acknowledgement fails, the server stops through the current persistence consistency check.
Later guild and family saves also check that stop state.
Guild login and chat use the same gate as membership changes, so a late login cannot restore chat access after a kick.

## Checks

`ExpeditionAuthorizationTests` covers invitation ownership, faction changes, expiry, rank checks, all policy flags, and chat permission.
It also covers concurrent final-place acceptance, disband cleanup, rename body order, malformed packets, and save failures.
The existing `CharacterBlockTests` still cover blocks and replaced invitation participants.
`ExpeditionPersistenceTests` covers a new manager load after rename, ownership transfer, role changes, member removal, and disband.
SQL failure tests check transaction rollback and a later successful removal retry.

Human checks still need the published server and the r208022 client:

1. Invite a character from the same alliance and accept the invitation.
2. Try an invitation to a hostile alliance and check that the server rejects it.
3. Change a lower role, deny clan chat, and test the resulting permissions.
4. Try to kick or demote the owner and check that the server rejects it.
5. Transfer ownership, leave, and reconnect both characters.
6. Rename the guild and check its name from another nearby character.
7. Disband the guild and check member tags from a different guild.
8. Reconnect and make sure that the disbanded guild does not return.
