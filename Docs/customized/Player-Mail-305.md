# Player mail payments and attachments

Issue: <https://github.com/KeganHollern/aaemu-cluster/issues/305>

Player sends accept Normal and Express mail only. Copper must be nonnegative;
billing and the unused alternate currency must be zero. Fees are calculated in
wide arithmetic and must fit the packet amount. Server validation derives the
attachment count and recipient identity and rejects repeated slots/IDs, foreign
or unregistered items, and bound attachments.

`CharacterMails.SendMailToPlayer` uses `PlayerMailSendExecutor` to prepare the
complete debit, original attachment transfers to the receiver's mail container,
and the new mail record under the shared persistence lock. It commits the shared
economy checkpoint, including the sender, before publishing any success or item
notifications. A known save failure restores all prepared state; an uncertain
commit outcome preserves it without reporting success. Reservations held by a
trade prevent using offered attachments or gold in a mail send.

The obsolete `MailPlayerToPlayer` implementation has been removed. The existing
wire input layout and Normal-mail delay are retained. The attachment claim issue
and its held pull request remain separate work.

Validation: executor cases cover reservations, self-mail payment and missing
recipients; their fixture supplies the real inventory and mail managers. Legacy
send cases moved from the mailbox-only fixture. These unit cases are compiled
against the previously built helper assembly while the entry-point preflight
reports the pending shared-save API. Nine real-MySQL failure/reload cases exercise
`CharacterMails.SendMailToPlayer` and its actual save dependency, including forged
header metadata. Compilation and integrated validation await the shared
persistence checkpoint dependency; no release has been completed.

The final focused runs passed 76 executor cases and 24 remaining mailbox cases.
The integrated MySQL cases remain unexecuted.
