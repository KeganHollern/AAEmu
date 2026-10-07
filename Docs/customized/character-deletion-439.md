# Character deletion, issue 439

Issue: <https://github.com/KeganHollern/aaemu-cluster/issues/439>.

Deletion uses the configured level delay. The request rejects an active character,
a guild owner, a character with an active prison or trial penalty, and a bot suspect.
The server reads saved penalty buffs because the lobby does not load their effects.
A real-time buff can expire offline. A game-time buff keeps its saved duration.
Pending trial state also blocks deletion.

The server checks the durable deletion state before character selection. Selection
and deletion use the persistence lock. Selection takes the session lock first.
Deletion checks all active connections, world characters, and vehicles that still
have a live owner reference. A timer cannot delete a selected character before
world registration.

Kegan approved this auction rule for this release:

- Deletion waits for owned listings and current winning bids to finish.
- The ordinary auction scheduler keeps its current deadlines and settlement rules.
- Deletion checks again after 1 minute when an obligation or cleanup failure remains.
- The player can cancel deletion during this wait.
- A former bidder whose bid lost has no current auction obligation.

Player mail returns use the current durable mail lifecycle before final deletion.
A failed return keeps deletion pending. The retry does not duplicate earlier returns.
Final deletion uses one economy checkpoint. That checkpoint saves pending economic
state, marks the character deleted, clears character currency, and removes personal
items, item containers, received mail, mates, vehicles, and character child records.
The checkpoint also applies the current house expiry rule to the saved house rows.
A failed database operation restores the prepared mail state and retains the source
assets for a retry. An unknown commit result uses the current persistence stop rule.

The cleanup keeps account labor, credits, and loyalty. It also keeps other recipients'
mail, coffer contents under the house lifecycle, and immutable mail archive evidence.
Guild and family removal continue through their current managers. The cleanup does
not delete shared houses or their contents through an inventory query.

The character row remains as a tombstone. `Character.Save` checks and locks that row
before any character or child write. It skips a deleted row and includes `deleted`
in normal saves. Autosave also skips a known deleted character. Item and container
caches remove committed personal assets. Their IDs stay reserved for the process
lifetime. Historical name and account lookup remains available, but a released
name and its tombstone do not become playable mail recipients.

No schema update or retroactive deletion accompanies this change. Cleanup runs only
through the normal character deletion workflow.

## Automated checks

The MySQL tests use an isolated random local schema. They cover active and selected
characters, offline penalties, expiration rules, stale selection and saves, deletion
cancellation, cleanup failure and retry, personal assets, shared coffers, account
currency, mail archive evidence, and existing mail-return retry behavior.
Auction tests cover seller and bidder deletion, unchanged auction deadlines,
settlement failure and retry, ordinary sale proceeds, and deletion cancellation.

## Human checks

Use disposable characters. Keep Kegan's main character out of these checks.

1. Create a disposable character and give it an item and some gold.
2. Return to character selection and request deletion.
3. Wait for the configured delay, then refresh the character list.
4. Make sure the deleted character disappears.
5. Restart the client and make sure the deleted character remains absent.
6. If name release is enabled, create a new character with the released name.
7. Request deletion of another disposable character, then cancel it before completion.
8. Make sure that character can enter the world with its items and gold.

A separate auction check needs a second disposable character and a short test listing.
Request deletion while that character owns a listing or holds the winning bid.
Make sure deletion waits for normal settlement. Check the other character's item or
sale proceeds after settlement. Repeat with cancellation of the pending deletion.
Do not change the live auction deadline solely for this check.
