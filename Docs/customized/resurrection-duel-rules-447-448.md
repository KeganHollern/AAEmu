# Resurrection and duel rules, issues #447 and #448

This change targets AAEmu `3409b265` and client `r208022`.
Human validation remains pending in cluster issue #573, HUMAN VALIDATION.

## Confirmed client evidence

The client revision is `208022` in `client/history.txt`.
The packed and dumped `x2game.dll` have image base `0x38ff0000`, image size `0x01cb0a00`, and PE timestamp `0x543cb835`.
The retained dump comes from the previous exact-client research. This work did not capture a new client process.

| Input | SHA-256 |
| --- | --- |
| Packed `client/bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| `.tools-re/dumps/x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |

Native exports and the X2UI trace are under the ignored cluster directory `.tools-re/combat-rules-20260926/death-duels/`.
The Ghidra project is an isolated copy of the previous portal research project.

| Request | Level, opcode | Complete body | Native evidence |
| --- | --- | --- | --- |
| `CSResurrectCharacter` | 1, `0x4e` | Boolean `inPlace`, 1 byte | Producer `0x39186e70`, constructor `0x397abb60`, serializer `0x397b0990` |
| `CSChallengeDuel` | 1, `0x50` | Target character ID, `u32` | Producer `0x3903b420`, serializer `0x397b9a30` |
| `CSStartDuel` | 1, `0x51` | Challenger character ID, `u32`, then reply, `i16` | Producer `0x3903ad50`, serializer `0x397c50b0` |

The duel reply producer sends `0` for acceptance and `507` for refusal.
The server rejects another reply value, zero IDs, incomplete bodies, and trailing bytes before it changes duel state.

The resurrection producer checks the death state and the remaining resurrection wait before either request path.
It also needs the offer Boolean for `inPlace=true`.
`OnNotifyResurrection` at `0x391d39f0` sets the Boolean at player-state offset `0xafc`.
`OnCharacterResurrected` at `0x391d3ad0` clears that Boolean and the death clock.

`GetResurrectionCountdown` at `0x39495c00` supplies the offer Boolean, a 300-second death countdown, and the authored wait countdown.
X2UI `messageboximpl/death_and_resurrection_window.alb`, source lines 230–246 and 301–318, calls `OnGraveyard` when the death countdown ends.
That countdown is automatic temple revival. It does not define an offer lifetime.
The native offer state and this UI module contain no separate offer clock.

The native loader at `0x39620ce0` reads `resurrection_waiting_times` by ID.
The 10 deployed rows use seconds. They contain these values:

| Death count | Normal wait | Siege wait | Penalty duration |
| --- | --- | --- | --- |
| 1 | 0 | 20 | 600 |
| 2 | 5 | 15 | 600 |
| 3 | 45 | 10 | 600 |
| 4 | 90 | 5 | 600 |
| 5–10 | 180 | 0 | 600 |

The server converts each duration to milliseconds for the current character fields and death packet.
The server uses `KillReason.PvpSiege` for the siege column. This change does not add the Dominion system.

The native duel request predicate is `0x3903b2f0`.
It checks target type, death, duel state, and relevant player states. It contains no distance test.
`SCDuelState` at `0x391d6e50` stores the duel flag at unit offset `0x3bac`.
`SCDuelStarted` calls `0x3903b480` for client feedback. It does not change faction.
The native opponent predicate at `0x39358ab0` needs different owners and the same nonzero duel flag.
Its flag getter supports Character and Mate. It does not support Slave.
The previous research record, `issue-284-peace-combat.md`, confirms the same predicate in native attack eligibility.

## Server changes

The previous priest work already rejected resurrection while alive and required an offer for the current death.
This change retains those checks and applies the death wait before it consumes an offer.
One death timestamp identifies the offer. `Unit.DoDie` no longer replaces that timestamp.
Expired offers cannot authorize revival or pass an expired priest experience benefit to a replacement offer.
Temple revival retains the current death-context debuffs. An offer cannot bypass an unfinished wait.
PvE temple revival applies Weakened Body and Respawn Cooldown. PvP temple revival applies Respawn Cooldown.
PvP death in War also applies Leech. In-place revival does not apply these temple debuffs.
The audit wording does not mean that every death must apply all 3 debuffs.

The server loads the authored waits instead of the hard-coded 15–240 second table.
It uses the current persisted `DeadCount`, `DeadTime`, `RezWaitDuration`, `RezTime`, and `RezPenaltyDuration` fields.
The next death resets escalation when the authored penalty duration elapsed since the previous death.
This interpretation follows the issue's death-window rule. The client does not expose the retail server's counter algorithm.
No schema or compact update is needed.

Each duel owns its participants, flag, countdown, duration timer, and distance timer.
The manager reserves both participants in one state change.
Only the challenged character can accept or refuse the pending challenge.
Acceptance and countdown completion check the participants again.
Timers retain the Duel object. A timer from a previous duel cannot alter a later duel for the same character.

The active opponent pair controls hostility and nonlethal damage.
Duel code no longer calls the permanent faction change path.
A duel does not remove guild or party membership, change housing faction, or alter relationships with other duel pairs.
Only the opponent and that opponent's mate receive nonlethal attribution.
The HP limit applies after absorption. NPC, environmental, and third-party damage can still kill a duelist.
The normal damage source reaches death and combat rules without replacement.
Death and disconnect cancel the duel.

Each skill captures its duel before delayed effects start. Buff ticks retain the duel without changing the original caster or skill source.
Damage and hostile buff effects hold an effect lease for that exact duel.
A winning hit reserves the result. Cleanup waits for active effects to return, so the winning hit retains its duel relation.
Later effects from that cast, plot, or buff reject the ended duel, including after a new duel starts.
Duel completion marks the state before it removes hostile buffs.
The state lock does not cover damage or buff-exit callbacks.
The ending duel remains reserved during cleanup, with opponent damage still nonlethal.
Cleanup removes hostile effects from the opponent and that opponent's mate.
This retains the protection added for issue #20.

## Confirmed project rules

The client does not define the server's resurrection offer lifetime or duel request distance.
The user approved a 300-second offer lifetime and a 30-metre duel request range on 2026-09-26.
Offer expiry starts when the server creates the offer, not at death. These values are project rules, not native client facts.
The previous 3-second duel countdown, 5-minute duel duration, and 75-metre surrender distance remain unchanged.
Those existing server values are not new claims about retail behavior.

## Checks

The focused tests cover authored normal and siege waits, reset boundaries, and reloaded death fields.
They also cover living, early, missing-offer, expired-offer, and previous-death resurrection requests.
Packet tests cover exact lengths, Boolean values, IDs, and accepted duel reply values.
Duel tests cover forged replies, invalid targets, concurrent reservations, stale timers, concurrent duels, and opponent versus external damage.
The Peace tests use the explicit active duel state.
The complete-cast test uses the damage then hostile-buff order from skill 10135.
It checks crime attribution, a later plot effect, a later buff tick, and a new duel between the same players.
Barrier tests check that flag creation and active effects do not hold the manager lock.
The Release build passed. The focused suites passed 43 duel, 14 resurrection, 17 priest, and 34 Peace cases.
The independent review checked packet contracts, death state, duel authorization, effect completion, and callback lock order.
The branch merged reviewed deployment tip `73c9dd5ca4e55d06036a8b0556776d89c74b62ee`.
The merged Release build passed. The full suite passed 4,341 tests with 0 failures.
The 20 skipped tests need optional exact-client asset paths. None is a resurrection or duel test.
The first full run exposed missing world metadata in the new duel fixture. The fixture now supplies that metadata.

Commands:

```text
dotnet build AAEmu.UnitTests/AAEmu.UnitTests.csproj -c Release --no-restore
dotnet AAEmu.UnitTests/bin/Release/net10.0/AAEmu.UnitTests.dll --treenode-filter '/*/*/DuelRulesTests/*'
dotnet AAEmu.UnitTests/bin/Release/net10.0/AAEmu.UnitTests.dll --treenode-filter '/*/*/ResurrectionRulesTests/*'
dotnet AAEmu.UnitTests/bin/Release/net10.0/AAEmu.UnitTests.dll --treenode-filter '/*/*/PriestPurchaseTests/*'
dotnet AAEmu.UnitTests/bin/Release/net10.0/AAEmu.UnitTests.dll --treenode-filter '/*/*/PeaceProtectionTests/*'
dotnet AAEmu.UnitTests/bin/Release/net10.0/AAEmu.UnitTests.dll
```

The local SDK is `/home/kegan/archeage/.tools/dotnet/dotnet`.
Build and test logs are in the same ignored evidence directory as the native exports.

## Pending HUMAN VALIDATION checks

Use a character on the new server release. Record its level before death tests.
Beginner revival needs a character below the configured minimum experience-loss level.
Priest revival needs the relevant priest buff before death. Another player's offer needs that player's resurrection skill.
The client automatically requests temple revival after 300 seconds from death.
The offer-expiry rejection also needs the packet-level automated check, because normal UI death timing can end the manual case first.
Normal duel checks need 2 players. Concurrent-duel checks need 4 players.
Forged replies and offer-expiry rejection need packet tooling. Automated tests cover those cases.
Use an NPC or a fall for the external-damage test. Use a battle pet and a hostile periodic buff for effect cleanup.


- [ ] **HV447-1.** Die once and revive at a temple. Check HP, MP, and the expected death-context debuffs.
- [ ] **HV447-2.** Die repeatedly within 600 seconds. Check normal waits of 0, 5, 45, 90, and 180 seconds.
- [ ] **HV447-3.** Reconnect during a death wait. Check that the same wait remains and an early request cannot revive the character.
- [ ] **HV447-4.** Wait 600 seconds after a death before the next death. Check that the next normal wait is 0 seconds.
- [ ] **HV447-5.** Receive a resurrection offer from another player. Accept it after the death wait and before offer expiry.
- [ ] **HV447-6.** Let the normal client reach its 300-second death countdown. Check automatic temple revival.
- [ ] **HV447-7.** Check a beginner's free revival and a priest-buff revival. Check restored experience for the priest-buff case.
- [ ] **HV448-1.** Challenge another player inside the 30-metre range. Accept, wait for the countdown, and complete the duel at 1 HP.
- [ ] **HV448-2.** Refuse a challenge. Check that both players can start a new duel.
- [ ] **HV448-3.** Move outside the 30-metre range before acceptance. Check that acceptance fails without a flag or faction change.
- [ ] **HV448-4.** Let an NPC or environmental damage kill a duelist. Check normal death, penalties, and duel cleanup.
- [ ] **HV448-5.** Complete a duel with a damage-over-time effect and with a battle pet. Check that damage stops after completion.
- [ ] **HV448-6.** Check guild, party, faction, and housing membership before and after a duel. Check that they stay unchanged.
- [ ] **HV448-7.** Check 2 simultaneous duels. End one and check that the other flag, hostility, and timers remain correct.
- [ ] **HV448-8.** Disconnect or change instance during a duel. Check cleanup, then start another duel without effects from old timers.

The multiplayer checks need more than 1 player. These checks do not yet have human validation for this change.
