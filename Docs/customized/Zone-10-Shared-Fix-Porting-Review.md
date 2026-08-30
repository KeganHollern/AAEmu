# Zone 10 Shared-Fix Porting Review

**Review date:** 2026-08-30  
**Target build:** ArcheAge 1.2 (`r208022`)  
**Target branch reviewed:** `deployment/r208022` at `f31df6de`  
**Source branch:** `upstream/client_version/zone-10.0.2_r575` at `3cc280b1`  
**Shared ancestor:** `dea8b2bb54a8b67e09207219a4ca38df15405dc2`

## Purpose

This document identifies logical fixes from AAEmu's Zone 10 branch that are candidates for selective adoption in the ArcheAge 1.2 build.

Zone 10 targets a substantially different client and contains large protocol, content, and architecture changes that do not belong in the 1.2 server. The recommendations below therefore focus on behavior that should remain valid across client versions:

- authority checks for client-supplied identifiers;
- money and item integrity;
- transactional or rollback-safe state changes;
- concurrency and shutdown correctness;
- malformed-input handling;
- crash prevention; and
- small logic corrections with version-independent intent.

This is a review and porting plan, not an instruction to merge or cherry-pick the Zone 10 branch.

## Executive summary

Several Zone 10 fixes expose serious issues that remain present in the 1.2 implementation. The highest-priority candidates are:

1. Mail ownership and character-deletion cleanup.
2. Auction authorization and payment ordering.
3. Trade validation, stable offer snapshots, and rollback.
4. Fail-closed length-prefixed network framing.

These should be manually adapted to the 1.2 code and packet model. The large Zone 10 integration commit must not be cherry-picked wholesale.

The next tier covers orderly shutdown, login-link reconnection, duel authorization and cleanup, doodad interaction serialization, and culture-independent `game_pak` path lookup.

A final tier contains small, isolated corrections for level-up events, auto-attack hit rolls, conflict-zone initialization, crime evidence zone keys, plot target enumeration, and dead mount/vehicle boarding.

## Review method

The source and target branches were refreshed from the base [AAEmu repository](https://github.com/AAEmu/AAEmu) and compared from their shared ancestor. The Zone 10 branch contains 88 commits after that point, while the target branch contains 165 commits on its side of the divergence.

Candidate commits were filtered for shared logical behavior and then checked against the current 1.2 implementation. A commit was not considered portable merely because its title described a fix; the affected code path also had to exist in the 1.2 build and exhibit the underlying behavior.

### Porting rules

- Port the smallest coherent behavior, not entire commits with v10 protocol or mechanics changes.
- Treat all packet fields and identifiers as untrusted input.
- Revalidate ownership, container membership, amounts, and mutable state at commit time.
- Protect multi-step operations from concurrent completion by two sessions.
- Add regression tests before considering a port complete.
- Preserve 1.2 packet layouts, error semantics, content rules, and database schema unless a separate migration is explicitly designed.

## Priority summary

| Priority | Candidate | Risk addressed | Source |
| --- | --- | --- | --- |
| P0 | Mail authority and integrity | Cross-character read/delete/return, global mail mutation, crashes, corrupted attachment state | [`e533e88b`](https://github.com/AAEmu/AAEmu/commit/e533e88b6057c3c1a5f6517e3177d71b58671100) |
| P0 | Auction authority and payment | Unauthorized cancellation/listing, free purchases, currency creation, item metadata loss | [`e533e88b`](https://github.com/AAEmu/AAEmu/commit/e533e88b6057c3c1a5f6517e3177d71b58671100) |
| P0 | Trade validation and rollback | Forged acceptance, stale offers, negative values, partial transfers | [`e533e88b`](https://github.com/AAEmu/AAEmu/commit/e533e88b6057c3c1a5f6517e3177d71b58671100) |
| P0 | Fail-closed framing | Non-consuming receive loop and CPU denial of service on partial input | [`2731c850`](https://github.com/AAEmu/AAEmu/commit/2731c85073a2dcd6230484b2fa14d9d8a5f313fd) |
| P1 | Shutdown and reconnect lifecycle | Work continuing during teardown, physics races, reconnect after intentional stop | [`e533e88b`](https://github.com/AAEmu/AAEmu/commit/e533e88b6057c3c1a5f6517e3177d71b58671100), [`2731c850`](https://github.com/AAEmu/AAEmu/commit/2731c85073a2dcd6230484b2fa14d9d8a5f313fd), [`b3ed9c56`](https://github.com/AAEmu/AAEmu/commit/b3ed9c560329885b3618bcab6953b004f0b5af39), [`46664db4`](https://github.com/AAEmu/AAEmu/commit/46664db43c4da7d8de65c2c6a7dbdb7e32559ad7) |
| P1 | Duel authorization and cleanup | Third-party responses, stuck factions/debuffs, orphaned timers | [`274bce31`](https://github.com/AAEmu/AAEmu/commit/274bce3122943b803c73e7cc84db1ec4f352f5e3), [`c6848ec3`](https://github.com/AAEmu/AAEmu/commit/c6848ec3a80507c583278d616d718f136f591989) |
| P1 | Doodad interaction serialization | Duplicate phase transitions, loot, or scheduled effects | [`45fbd2f1`](https://github.com/AAEmu/AAEmu/commit/45fbd2f1a506354e5200418376a261f5a8e3fb87) |
| P1 | Stable, ordinal pak lookup | Culture/ICU crash and concurrent enumeration instability | [`3b19402c`](https://github.com/AAEmu/AAEmu/commit/3b19402c020c36ff935a4c596854b4bbafa6f666) |
| P2 | Small shared logic fixes | Incorrect events, data keys, state initialization, and validation | Individual commits listed below |

## P0 findings

### 1. Mail ownership and integrity

#### Current 1.2 behavior

`AAEmu.Game/Models/Game/Char/CharacterMails.cs` retrieves several client-supplied mail IDs from the global mail collection without consistently proving that the active character owns the requested side of the mail:

- `ReadMail` can return another character's mail body and mutate its read status.
- `DeleteMail` can delete another character's no-attachment inbox mail.
- `ReturnMail` can attempt to return another character's mail.
- `ReturnMail` walks all ten possible attachment indexes even though the attachment list only contains used entries, so ordinary mail with fewer than ten items can throw.

`GetAttached` already has a receiver check in the current build, but it can throw after moving some items and before completing attachment bookkeeping. A partial move followed by an exception can leave an item in the bag while it is still represented as a mail attachment.

There is a separate high-impact problem in `AAEmu.Game/Core/Managers/UnitManagers/CharacterManager.cs`: character asset deletion enumerates every tracked mail and returns each eligible mail, without restricting the operation to mail addressed to the character being deleted. Deleting one character can therefore mutate mail globally.

Player-to-player mail also accepts signed currency fields before validating their domain. Negative values can bypass affordability logic and create malformed mail that cannot be collected or deleted normally.

#### Recommended port

Adapt the mail-domain portions of [`e533e88b`](https://github.com/AAEmu/AAEmu/commit/e533e88b6057c3c1a5f6517e3177d71b58671100):

- add one authoritative resolver that verifies sender or receiver ownership according to the requested mailbox;
- use that resolver for read, delete, attachment, and return operations;
- return an existing mail in place rather than rebuilding it through the send-mail path;
- bind return operations to the authorized receiver;
- serialize return and deletion against the global mail collection;
- restrict deletion cleanup to mail whose `ReceiverId` is the character being deleted;
- reject negative money values and widen fee arithmetic before checking affordability;
- complete bookkeeping for successfully moved attachments even if a later attachment cannot move; and
- prevent attachment-count underflow.

Keep the 1.2 packet classes and field layouts. The v10 mail packets are not part of this recommendation.

#### Required regression tests

- A character cannot read, delete, return, or take attachments from another character's mail ID.
- Sent-box access verifies the sender; inbox access verifies the receiver.
- Deleting one character only returns mail addressed to that character.
- Returning mail with zero, one, and ten attachments succeeds without indexing unused slots.
- Negative money fields are rejected without changing inventory or currency.
- A failure moving attachment N preserves correct ownership and bookkeeping for attachments 1 through N-1.
- Two concurrent return/delete attempts produce one valid final state.

### 2. Auction authority and payment ordering

#### Current 1.2 behavior

`AAEmu.Game/Core/Managers/AuctionManager.cs` has several independent integrity problems:

- `CancelAuctionLot` looks up a client-supplied auction ID but does not verify that `ClientId` matches the caller.
- Cancellation creates a new item from template, count, and grade instead of returning the listed item. This loses enchantment, gems, dye, lifespan, crafter, UCC, and other instance data while stranding the original auction attachment.
- Buy-now and bid paths ignore the result of `SubtractMoney`.
- A previous bidder can be refunded before the replacement bidder has paid.
- `PostLotOnAuction` resolves `itemId` from the global item store without proving that the item belongs to the caller and is in the caller's inventory.
- Negative client-supplied prices can produce a negative listing fee; subtracting that fee adds currency.

#### Recommended port

Adapt the auction hardening in [`e533e88b`](https://github.com/AAEmu/AAEmu/commit/e533e88b6057c3c1a5f6517e3177d71b58671100):

- require listing ownership for cancellation;
- return the original listed item and move it into the mail-attachment container;
- validate item owner and inventory container before listing;
- reject invalid or negative prices before fee calculation;
- take payment before refunding an earlier bidder or transferring the lot; and
- stop immediately when payment fails.

The Zone 10 patch does not fully serialize a complete auction operation. The 1.2 port should add per-lot synchronization or an equivalent single-winner state transition so simultaneous bid, buy-now, cancel, and expiry operations cannot all act on the same lot.

#### Required regression tests

- A non-owner cannot cancel or list another character's item.
- Cancellation returns the same item ID and preserves all instance metadata.
- An unaffordable bid or purchase changes no lot, mail, item, or currency state.
- The previous bidder is refunded exactly once and only after replacement payment succeeds.
- Negative and otherwise invalid prices cannot create currency.
- Concurrent buy-now attempts produce one buyer, one seller payment, and no duplicated item or refund.

### 3. Trade validation and rollback

#### Current 1.2 behavior

`AAEmu.Game/Core/Managers/TradeManager.cs` does not record and validate a pending invitation before starting a trade. It also stores mutable `Item` references rather than stable item ID, slot, container, and amount snapshots.

Offer validation is incomplete:

- item existence is not safely checked before dereference;
- slot type, ownership, inventory container, soulbound state, and positive amount are not consistently enforced;
- negative money can pass the initial balance comparison;
- changing or moving an offered item before final acceptance is not reliably detected; and
- the requested stack amount is not represented by the stored offer object.

When both players accept, insufficient bag-space checks call `CancelTrade` but do not return before `FinishTrade`. Currency is then modified before item movement, and item movement failures only increment an error counter. The trade is removed and reported complete even when part of it failed.

#### Recommended port

Adapt the trade-domain work in [`e533e88b`](https://github.com/AAEmu/AAEmu/commit/e533e88b6057c3c1a5f6517e3177d71b58671100):

- track pending invitations and reject unsolicited acceptance or refusal;
- validate online/dead/combat/instance/relation state both at invitation and completion;
- store stable offer entries containing item ID, slot type, slot, and amount;
- reject negative money, invalid containers, invalid amounts, and soulbound items;
- re-resolve every item at final commit;
- account for slots freed by outgoing full stacks;
- stage item moves and roll them back if any move fails;
- apply the net currency delta only after item staging succeeds; and
- enforce item-task limits.

Omit v10 heir-level restrictions and other version-specific eligibility rules. Add a per-trade synchronization boundary because the Zone 10 implementation's ordinary dictionary and staged transfer logic do not by themselves prevent both clients from entering finalization simultaneously.

#### Required regression tests

- Acceptance without a matching invitation is rejected.
- A traded item moved, replaced, split, consumed, rebound, or made soulbound before commit cancels safely.
- Negative money and non-positive item amounts are rejected.
- Full recipient bags cancel without transferring money or items.
- Valid partial-stack transfers preserve item metadata.
- An injected failure in either transfer direction rolls the entire trade back.
- Concurrent final acceptance completes the trade once.

### 4. Fail-closed length-prefixed framing

#### Current 1.2 behavior

`AAEmu.Commons/Network/PacketStream.ReadUInt16` logs and returns zero when fewer than two bytes remain. Game, stream, and internal protocol handlers attempt to catch `MarshalException`, so their incomplete-header branch is not reached.

With a single byte buffered, the handler can accept a zero-length frame, consume no input, and re-enter the loop with the same byte. Normal TCP fragmentation or malicious input can therefore cause a non-consuming CPU loop.

#### Recommended port

Port the shared framing helper and tests from [`2731c850`](https://github.com/AAEmu/AAEmu/commit/2731c85073a2dcd6230484b2fa14d9d8a5f313fd). Apply it to:

- Game client framing;
- Stream client framing;
- Game-to-Login framing;
- Login-to-Game framing; and
- any other length-prefixed internal link using the same pattern.

Select a protocol-appropriate minimum frame size for each connection. A single minimum of two bytes may be too permissive where a valid frame must also contain a multi-byte opcode or header.

#### Required regression tests

- A one-byte header fragment is retained without dispatch and without spinning.
- A partial payload is retained until complete.
- A zero-length or undersized frame is rejected and makes forward progress.
- One valid frame followed by a remainder dispatches once and preserves the remainder.
- Repeated one-byte receives never dispatch opcode zero or enter a non-consuming loop.

## P1 findings

### 5. Shutdown and Login reconnect lifecycle

Three related issues should be treated as one reliability workstream:

1. `TaskManager.Stop()` is a no-op, while due work is launched with untracked `Task.Run`. Scheduled work can continue into manager, world, database-provider, and client-source teardown.
2. `PhysicsManager.Stop()` only clears a running flag. `WorldManager` can clear or dispose state before the physics thread exits its current iteration.
3. `LoginProtocolHandler.OnDisconnect` always stops and restarts the Login link, including disconnect callbacks caused by intentional application shutdown.

Recommended sources:

- tracked scheduled executions and asynchronous stop: [`e533e88b`](https://github.com/AAEmu/AAEmu/commit/e533e88b6057c3c1a5f6517e3177d71b58671100);
- shutdown-aware Login reconnect and framing: [`2731c850`](https://github.com/AAEmu/AAEmu/commit/2731c85073a2dcd6230484b2fa14d9d8a5f313fd);
- reconnect refinements and cancellation epochs: [`b3ed9c56`](https://github.com/AAEmu/AAEmu/commit/b3ed9c560329885b3618bcab6953b004f0b5af39); and
- physics thread joining: [`46664db4`](https://github.com/AAEmu/AAEmu/commit/46664db43c4da7d8de65c2c6a7dbdb7e32559ad7).

The preferred 1.2 implementation should:

- stop accepting new scheduled work;
- cancel queued work;
- observe and log task failures;
- await running scheduled work with the host cancellation token;
- signal all physics loops before joining them;
- prevent reconnection after intentional shutdown; and
- coordinate retry attempts so only the current reconnect generation may publish a connection.

### 6. Duel authorization and cleanup

`AAEmu.Game/Core/Managers/DuelManager.cs` uses a client-supplied challenger ID to find a duel but does not consistently prove that the responding character is one of its participants. Cancellation has no caller argument, making participant authorization impossible at that API boundary.

The current flow also lacks a complete invitation timeout and logout/disconnect cleanup. Exceptions or abnormal termination can leave duel tasks, factions, or effects active.

Adapt the manager-level logic from:

- [`274bce31`](https://github.com/AAEmu/AAEmu/commit/274bce3122943b803c73e7cc84db1ec4f352f5e3) for invite timeout, logout cleanup, timer cancellation, and `finally`-style restoration; and
- [`c6848ec3`](https://github.com/AAEmu/AAEmu/commit/c6848ec3a80507c583278d616d718f136f591989) for participant-bound accept and decline.

Do not copy the surrounding v10 duel packet changes. Preserve the existing 1.2 duel packet sequence and current custom faction/debuff behavior.

### 7. Doodad interaction serialization

`Doodad.Use` and `Doodad.OnSkillHit` can concurrently mutate phase, task, data, and persistence state. Two interactions against the same doodad can both observe the same phase and apply the same transition or reward.

Port the synchronization concept from [`45fbd2f1`](https://github.com/AAEmu/AAEmu/commit/45fbd2f1a506354e5200418376a261f5a8e3fb87). Use a private synchronization object rather than `lock(this)` and keep callbacks or broadcasts outside the critical section where practical. The v10 ReactDevote behavior and duplicate packet cleanup are separate concerns.

Tests should execute competing use/use and use/skill-hit operations and prove that only one phase transition or final reward occurs.

### 8. Stable, ordinal `game_pak` lookup

`AAEmu.Game/IO/ClientSource.cs` enumerates live `GamePak.pakFiles.Keys` and applies `CurrentCultureIgnoreCase` to archive paths. Pak paths are identifiers, not localized user text, so culture-sensitive comparison is inappropriate and can enter native ICU code during concurrent skill/plot loading.

[`3b19402c`](https://github.com/AAEmu/AAEmu/commit/3b19402c020c36ff935a4c596854b4bbafa6f666) changes the comparison to ordinal and snapshots keys before filtering.

The 1.2 port should use `OrdinalIgnoreCase` and provide an immutable post-load key snapshot or lock dictionary mutations. Calling `ToArray()` on a dictionary while another thread mutates it is not a complete concurrency guarantee.

## P2 small shared fixes

These are suitable as isolated ports with focused tests.

| Fix | Current issue | Recommended change | Source |
| --- | --- | --- | --- |
| Level-up event gating | `DoOnLevelUpEvents` runs after every XP gain when connected | Move it inside the existing `leveledUp` block | [`c9335b97`](https://github.com/AAEmu/AAEmu/commit/c9335b97b3637c560973329fb8f0aebc377f939a) |
| Auto-attack hit reroll | A reused auto-attack `Skill` retains the first `HitTypes` entry because `TryAdd` never replaces it | Assign the new result for every swing | [`9f0c1681`](https://github.com/AAEmu/AAEmu/commit/9f0c16817e0c0fc64c9b9a7bca4b3d64620b55fe) |
| Conflict-zone initial state | A testing override forces conflict zones to start at `Conflict` | Remove the override and start at `Tension`, unless the deployment explicitly requires test behavior | [`6450576b`](https://github.com/AAEmu/AAEmu/commit/6450576b8d50fb9b2c4e8efbbb24a254c06c602d) |
| Crime evidence zone key | Crime evidence stores `Zone.Id` in the `zone_key` field | Store `Zone.ZoneKey` | [`897cb6a8`](https://github.com/AAEmu/AAEmu/commit/897cb6a89882fc84402e143913acd0eb1e560bda) |
| Random plot target snapshot | A lazy target sequence is enumerated separately by `Any`, `Count`, and `ElementAt` | Materialize once and index the stable list | [`2d425ac8`](https://github.com/AAEmu/AAEmu/commit/2d425ac871e3e07d6f170ccfe1b3e519289087ee) |
| Dead mate boarding | `MountMate` does not reject a dead target | Reject before changing attachment state | [`374ed9f9`](https://github.com/AAEmu/AAEmu/commit/374ed9f958958584ec8be4a4018781b3c7350a19) |
| Dead slave boarding | One slave-binding entry point rejects dead vehicles, but the direct object-ID overload does not | Enforce the invariant in the shared lower-level method | [`9639e298`](https://github.com/AAEmu/AAEmu/commit/9639e298b43f714608d72f5cff51f88566204a45) |

### Optional lower-priority utility fix

The `SubStream` changes inside [`e533e88b`](https://github.com/AAEmu/AAEmu/commit/e533e88b6057c3c1a5f6517e3177d71b58671100) add range validation, correct position checks, EOF handling for non-seekable streams, and a clearer bounded read-only contract. This is sensible shared utility hardening but is lower priority without an observed 1.2 failure.

If adopted, also review the shared underlying `AAPak` stream. Multiple substreams that independently change one shared stream position need ownership or synchronization beyond the Zone 10 `SubStream` refactor itself.

## Fixes already covered in the target build

The following Zone 10 commits should not be ported because equivalent behavior is already present in the 1.2 build:

| Zone 10 fix | Target status |
| --- | --- |
| Combine auction keyword and item/category filters ([`30300e41`](https://github.com/AAEmu/AAEmu/commit/30300e4167a68aadf2e7df5131ef177ced3e3966)) | Current auction search applies cumulative filters. |
| Preserve effect-bearing reagent items ([`c212e1e1`](https://github.com/AAEmu/AAEmu/commit/c212e1e17681df6a761777682bf4023a244d8ebf)) | Equivalent behavior is already present. |
| Transfer all furniture on house sale ([`3cc280b1`](https://github.com/AAEmu/AAEmu/commit/3cc280b14d7da0d874121d14ebbf409f5e032d1c)) | Current housing ownership transfer updates all relevant furniture and persistence state. |
| Handle `Climate.None` and missing zones ([`f15db25f`](https://github.com/AAEmu/AAEmu/commit/f15db25f7a8810e862ed5994be80d342bbfe065d)) | Equivalent climate handling is already present. |
| Correct Indun capacity off-by-one ([`ab4173c4`](https://github.com/AAEmu/AAEmu/commit/ab4173c4e81e4565d94811691ac4f5d8b19fdd63)) | Current code uses `Dungeon.IsFull`. |
| Shield/offhand equipment correction | The target contains an equivalent patch. |

## Explicitly excluded Zone 10 work

The following categories should not be brought into the 1.2 server as part of this effort:

- v10 opcodes, packet sizes, packet fields, and offset tables;
- AAEmu.World and zone-authority architecture;
- ancestral/heir progression and v10 level restrictions;
- v10 premium, schedule, tower-defense, attendance, and ArchePass systems;
- v10 housing, mail, team, friend-request, and character-list protocol rewrites as whole systems;
- changes to mechanics, balance, loot, combat formulas, or content availability that are specific to the v10 client; and
- database migrations whose only purpose is a v10 feature.

Some excluded commits still contain portable hunks. Only the identified domain logic should be extracted after being rewritten for the 1.2 types and invariants.

## Recommended delivery sequence

### Phase 1: Authority boundary

Implement mail, auction, and trade hardening as separate changesets. Each should include exploit-focused tests and should be independently reviewable.

Suggested order:

1. Mail ownership resolver and character-deletion cleanup.
2. Auction ownership, original-item return, and payment ordering.
3. Trade invitation authority, stable offers, and rollback.

### Phase 2: Transport and lifecycle

1. Shared length-prefixed framing helper and protocol-specific tests.
2. Shutdown-aware Login reconnect.
3. Tracked scheduled work and asynchronous shutdown.
4. Physics thread signal-and-join ordering.

### Phase 3: Concurrent gameplay state

1. Duel participant authorization, invitation expiry, and logout cleanup.
2. Doodad interaction serialization.
3. Stable `game_pak` path snapshots and ordinal comparison.

### Phase 4: Small isolated fixes

Apply the P2 fixes individually or in one tightly scoped shared-logic changeset, with a regression test for each behavior.

## Completion criteria

A candidate should be considered successfully ported only when:

- no v10 packet or mechanic dependency was introduced;
- all client-supplied IDs are authorized against the active character;
- failure paths leave money, items, mail, and world state unchanged or fully rolled back;
- concurrent completion has one defined winner;
- existing 1.2 packet serialization tests still pass;
- new exploit and regression tests pass;
- `dotnet build` passes; and
- the relevant unit and integration test suites pass.

## Source commit index

| Commit | Subject | Recommended use |
| --- | --- | --- |
| [`e533e88b`](https://github.com/AAEmu/AAEmu/commit/e533e88b6057c3c1a5f6517e3177d71b58671100) | `feat: integrate 10.0.2.13 authority updates` | Extract selected mail, auction, trade, task, and utility logic only. Never cherry-pick wholesale. |
| [`2731c850`](https://github.com/AAEmu/AAEmu/commit/2731c85073a2dcd6230484b2fa14d9d8a5f313fd) | `fix(world): fail-closed framing, schedules, and login reconnect` | Framing helper/tests and shutdown-aware reconnect concepts. |
| [`b3ed9c56`](https://github.com/AAEmu/AAEmu/commit/b3ed9c560329885b3618bcab6953b004f0b5af39) | `fix(world): close login reconnect and realtime ToD gaps` | Reconnect cancellation and publication refinements. |
| [`46664db4`](https://github.com/AAEmu/AAEmu/commit/46664db43c4da7d8de65c2c6a7dbdb7e32559ad7) | `fix: physics dispose` | Join physics execution before teardown. |
| [`274bce31`](https://github.com/AAEmu/AAEmu/commit/274bce3122943b803c73e7cc84db1ec4f352f5e3) | `fix(duel): release both players when a duel never ends properly` | Duel timeout, abnormal termination, and cleanup. |
| [`c6848ec3`](https://github.com/AAEmu/AAEmu/commit/c6848ec3a80507c583278d616d718f136f591989) | `fix(duel): only the two participants may answer their own duel` | Participant authorization. |
| [`3b19402c`](https://github.com/AAEmu/AAEmu/commit/3b19402c020c36ff935a4c596854b4bbafa6f666) | `fix(skill): stop the ICU crash when a plot skill resolves its text` | Ordinal path comparison and stable pak-key enumeration. |
| [`45fbd2f1`](https://github.com/AAEmu/AAEmu/commit/45fbd2f1a506354e5200418376a261f5a8e3fb87) | `fix: serialize doodad interactions and remove duplicate bot-trial packet` | Doodad synchronization only. |
| [`c9335b97`](https://github.com/AAEmu/AAEmu/commit/c9335b97b3637c560973329fb8f0aebc377f939a) | `fix(world): restore unit gates and session rejoin` | Level-up event gating only; other changes need separate version review. |
| [`9f0c1681`](https://github.com/AAEmu/AAEmu/commit/9f0c16817e0c0fc64c9b9a7bca4b3d64620b55fe) | `fix(combat): reroll auto-attack hit results` | Direct shared combat logic fix. |
| [`6450576b`](https://github.com/AAEmu/AAEmu/commit/6450576b8d50fb9b2c4e8efbbb24a254c06c602d) | `fix(zones): start conflict zones at Tension, not the testing-only Conflict override` | Remove testing-only production behavior. |
| [`897cb6a8`](https://github.com/AAEmu/AAEmu/commit/897cb6a89882fc84402e143913acd0eb1e560bda) | `Fix CrimeManager.ReportCrime zone key resolution` | Direct data-integrity fix. |
| [`2d425ac8`](https://github.com/AAEmu/AAEmu/commit/2d425ac871e3e07d6f170ccfe1b3e519289087ee) | `fix(plot): snapshot random-target candidates` | Stable random-target selection. |
| [`374ed9f9`](https://github.com/AAEmu/AAEmu/commit/374ed9f958958584ec8be4a4018781b3c7350a19) | `Reject mounting dead mates` | Direct validation fix. |
| [`9639e298`](https://github.com/AAEmu/AAEmu/commit/9639e298b43f714608d72f5cff51f88566204a45) | `Reject boarding dead slaves` | Enforce the invariant at the shared binding boundary. |

