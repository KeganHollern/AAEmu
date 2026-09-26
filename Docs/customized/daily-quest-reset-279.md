# Daily quest reset persistence (#279)

The daily reset now writes each changed 64-bit completion block before it changes memory or sends reset packets.
The server copies the block and clears all eligible daily quest bits in that copy.
A failed write preserves the previous block and sends no reset packet for that block.
The next reset can retry the failed block.
Successful blocks remain reset when another block fails.

The reset and completion flag writes use `SaveManager.PersistenceSyncRoot`.
The normal character save uses the same lock.
This prevents a periodic save from restoring stale bits after the reset write.
The reset still skips active quests, missing templates, and non-daily quests.
The reset covers Daily, DailyGroup, DailyHunt, and DailyLivelihood.
No schema, compact, schedule, or packet-layout change is part of this fix.

## Checks

`CharacterQuestDailyResetTests` checks packet order, one write per changed block, unchanged bits, all daily kinds, failure, retry, and repeat calls.
`QuestDailyResetPersistenceTests` uses the disposable local MySQL fixture.
It resets completion bits, creates a new character state, and loads the stored result without a character save.
A trigger rejects one block write in the failure test.
That test checks both the stored block and the in-memory block before and after retry.

## Pending human checks for HUMAN VALIDATION #573

- [ ] HV279-1. Complete a daily quest before the daily reset. Keep the character online across the reset. Check that the quest becomes available again.
- [ ] HV279-2. Reconnect after that reset. Check that the quest stays available and that a completed normal quest stays complete.
- [ ] HV279-3. Keep an accepted daily quest in progress across the reset. Check that the reset preserves its progress.

Human validation remains pending. Automated database tests cover restart durability and injected write failure.
