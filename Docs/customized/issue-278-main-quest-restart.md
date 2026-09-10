# Main quest restart for r208022

This change resolves [aaemu-cluster issue 278](https://github.com/KeganHollern/aaemu-cluster/issues/278).
The research base is AAEmu commit `61f535c0c0953f1c8da249c77ac74c330a74c2a4`.

## Exact client

`client/history.txt` states `version 208022`.

| Module | Size | SHA-256 |
| --- | ---: | --- |
| `client/bin32/x2game.dll` | 18,483,712 | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| `.tools-re/dumps/x2game.dumped.dll` | 30,085,120 | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |

Both files have PE timestamp `2014-10-14 00:44:21`, image base `0x38ff0000`, entry RVA `0x008c5d6d`, and image size `0x01cb0a00`.
Direct native references to `QuestContextRestart` and `OnQuestContextStarted` match that image base.
The original dump command is unknown.

The analysis uses GNU `objdump` and Ghidra `12.1.3 PUBLIC` with JDK 21.
The local script is `.tools-re/issue-278/DecompileQuestRestart.java`.
Its SHA-256 is `18960be55f9b0b605290be6628456798b1c82467593f5f2cd23e1b83186823c3`.
The unchanged AAEmu `CSOffsets.cs` has SHA-256 `6940809f2d506a0d381a74e142067c83061100a6067d548e54c3c500dfc9c3f2`.

## Confirmed packet and client path

The packet is C2G `CSRestartMainQuestPacket`, level `1`, opcode `0x0fd`.
The body contains exactly `4` bytes.
It has no count, optional field, or trailing group.

| Offset | Type | Meaning | Evidence |
| --- | --- | --- | --- |
| `0` | `uint32-le` | Quest context/template ID | Producer `0x3937a8d0`, setter `0x397aca80`, serializer `0x397bc150` |

The native object has a `12`-byte packet prefix and `1` field at offset `0x0c`.
The producer allocates `16` bytes and sets opcode `0xfd`.
The serializer uses the native `uint32` field operation at stream vtable offset `0x3c`.
The packet contains neither the database runtime ID nor a component checkpoint ID.

The Lua API registration at `0x394a8efd` binds `QuestContextRestart` to `0x3949b780`.
That wrapper calls `0x3937b200`, which finds the active quest through `0x397d6140`.
The wrapper rejects completed status `5` before it calls the producer.
The producer calls `0x397d3500`, which checks quest detail `2`, the main quest type.
It also checks the active quest entry and the free inventory slots for authored Supply items.
It passes the same context ID to the packet setter and sends the packet through `0x39186810`.

The wrapper sets native flag `0x3a173744` before the request.
`OnQuestContextStarted` at `0x391dbd50` calls consumer `0x3937b5b0`.
That consumer tests and clears the restart flag, and emits the restart UI event.
Its state helper `0x397d73f0` replaces the active entry for the context ID.
This path confirms `SCQuestContextStartedPacket` as the restart response.
An ordinary update then uses `OnQuestContextUpdated` and consumer `0x3937bdf0`.
The update consumer handles failed status `2` and refreshes quest state.

The extracted X2UI module is `game/scriptsbin/x2ui/questcontext/quest_context.alb`.
Its SHA-256 is `d11e5d34258870a042f89f39c0a39cdc85b968a7b07a3edc71c27867ee872efc`.
It registers `QUEST_CONTEXT_UPDATED` and uses `RefreshAllQuestUI` after world entry.
The quest modules do not contain a separate restart callback or binary serializer.

## Server state and transaction

The compact contains `222` contexts with `restart_on_fail='t'`.
Of these, `211` have main detail `2`, and `11` have normal detail `1`.
The native producer confirms that this packet applies to the `211` main quests.
None of the `222` contexts has a Fail component.
Their Start acts are accept conditions, and their Supply acts are `QuestActSupplyItem`.

The server checks owner identity, active membership, the Fail step, Failed status, main detail, and `RestartOnFail`.
It also rejects completed quests and templates without a Start component.
These guards apply the issue's restart-on-fail policy.
The native wrapper alone does not enforce all these server guards.

The restart keeps the runtime ID, template, original acceptor, supplies, and completion bits.
It creates a fresh attempt with empty objectives, report selections, reward pools, and applied-act sets.
The original acceptor lets the Start condition run without a new NPC request.
The server writes the fresh Start row in `1` SQL transaction before it activates events, timers, packets, or evaluation.
A failed write keeps the old attempt unchanged and permits a later retry.
The server uses `SaveManager.PersistenceSyncRoot` to prevent overlap with saves, failure, evaluation, and another restart.

A Start timer gets its new deadline before the transaction.
After the commit, the server restores that deadline and sends the Started response.
If that packet fails, the server still enables evaluation for the committed attempt.
Later steps use the normal quest evaluation path.
Supply grants only the count absent from the player's current items.
The restart itself does not grant or remove supplies, rewards, currency, or achievement progress.
The normal save transaction commits later item changes and applied-act IDs together.

Repeated requests fail because the fresh attempt is no longer in Fail.
An old queued evaluation cannot run because the active quest reference changed.
A timeout now checks the exact quest attempt, so an old timeout cannot fail the new attempt.
Quest failure sets Failed status and sends an update even when the template has no Fail component.
Load also corrects older Fail rows with a different status.

Restart from the same authored Start step is an inference from the native context replacement and Supply preflight.
The client sends no other checkpoint or initial-state choice.
No client patch or SQL schema change is needed.

## Tests and manual check

Automated tests cover exact packet length, uint32 identity, invalid state, owner mismatch, concurrent replay, failed writes, and persistence reload.
They also cover completion-bit preservation, reward reset, retained supplies, missing supplies, the timer deadline, and an old timeout.

The full solution build passed with `0` errors.
All `2383` unit tests passed on `2026-09-10`.
The commands used `/home/kegan/archeage/.tools/dotnet/dotnet` with these arguments:

```text
build AAEmu.slnx --nologo -v quiet
test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --no-build -- --treenode-filter '/*/*/*Quest*/*'
test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --no-build
```

`QuestRestartPersistenceTests` adds `2` GameMySql tests for the real restart transaction.
They check row reload, unchanged completion bits, a failed SQL write, and retry.
Fork CI runs these tests because this host has no local container runtime.

Do this manual check with the deployed r208022 client.

1. Fail a restartable main quest with visible objectives and a supplied item.
2. Use the restart action.
3. Make sure that the same quest shows fresh objectives and the expected supplied-item count.
4. Repeat the request and make sure that no more items or rewards appear.
5. Fail and restart a timed main quest, then reconnect before its deadline.
6. Make sure that the timer continues and later fails the quest once.
7. Complete the quest and make sure that its reward and completion flag appear once.

Human gameplay validation and a controlled packet capture remain open checks.
