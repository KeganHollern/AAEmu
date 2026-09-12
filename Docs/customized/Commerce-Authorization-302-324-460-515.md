# Commerce authorization and mail cleanup

This change covers cluster issues [302](https://github.com/KeganHollern/aaemu-cluster/issues/302), [324](https://github.com/KeganHollern/aaemu-cluster/issues/324), [460](https://github.com/KeganHollern/aaemu-cluster/issues/460), and [515](https://github.com/KeganHollern/aaemu-cluster/issues/515).

## Auction and trade

Auction registration resolves the item from the seller's bag. The registered item must be the same object in ItemManager. Foreign, bank, and unregistered items fail before settlement. Bound items return the authored auction or trade error. The current reservation and atomic settlement checks remain in effect.

Trade checks both participants' block lists and configured minimum levels. It checks eligibility before invitation, offer changes, and completion. Mail and chat use their configured level limits. These limits share `LevelRestrictionConfig` with the other service paths.

## Block lists

A character can add or remove a known offline character by name. Duplicate entries and self-blocks fail. The recipient's block list controls whispers, mail, team invitations, family invitations, and expedition invitations. Pending team, family, and expedition invitations also check the current block list.

Expedition replies need a current invitation for the same recipient object, inviter, and guild. Each recipient has at most 1 pending invitation. The server removes it after a reply, disconnect, or 1 minute. Acceptance checks the inviter's current guild and permission again. A forged reply cannot create membership.

The exact r208022 native handler `0x391d2820` passes the received inviter and guild fields to dialog constructor `0x392f88b0`. Reply callback `0x392f8970` supplies the guild ID, inviter ID, and reply flag to packet setter `0x397ab470`. Serializer `0x397b9190` writes those fields as 2 u32 values and 1 Boolean. This agrees with the current AAEmu packet reader. The x2game source and dump hashes are in [the UCC evidence record](UCC-Purchase-519.md).

Online mail recipients use their current block list before the next save. Offline recipients use the persisted list. A deleted recipient fails even when the name cache still contains that character. Block deletion acknowledgements preserve later changes that occur during a save.

## Character deletion and mail

Character deletion returns only that character's returnable mail. Each return uses the current durable mail transition. A failed return keeps deletion pending. A later check or restart retries the sources that still exist. Repeated completion does not create a second return. Mail returns precede the terminal character update, and a current database check rejects a cancelled deletion request.

The mail return keeps the item identity, ownership transfer, copper, billing amount, and second money field. Unrelated recipients' mail remains unchanged. This work needs no schema or compact update.

The legacy attachment path also checks wallet credit success. An overflow keeps the money attachment and labor unchanged and returns `MailTooMuchMoney`.

## Tests

The focused unit suite covers auction ownership, bound item errors, trade state, blocks, mail eligibility, level limits, and wallet overflow. The block-list suite has 17 cases. These include forged replies, later blocks, revoked permission, character replacement, declines, and invitation expiry. The mail packet fixture uses a registered mailbox in the character's current world.

The disposable MySQL suite uses the production mail transition and save code. It covers a partial return failure, process-state reload, retry, duplicate completion, a cancelled deletion request, deleted recipient rejection, and offline block persistence. It checks exact mail counts, item IDs, owners, and money amounts.

Issue [393](https://github.com/KeganHollern/aaemu-cluster/issues/393) has a separate process-crash test. Its current claim store and migration remain unchanged by this change.

## Client checks after release

1. Try to auction, trade, and mail a bound item. Check the error and unchanged item.
2. Block a second character. Try whispers, mail, trade, and social invitations in each direction.
3. Remove the block while the second character is offline. Send mail again.
4. Delete a character with mail from another character. Check the returned attachments and amounts.
5. Check the configured level limits with a new character and a mature character.

These focused client checks still need human validation.
