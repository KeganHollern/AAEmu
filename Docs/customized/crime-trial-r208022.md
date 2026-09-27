# Crime and trial corrections for r208022

This release covers cluster issues #454, #501, #502, #504, #505, #506, #518, and #146.
Human checks remain pending. The release record must identify the final source commit and test results before issue closure.

## Exact client evidence

The client revision is `208022`, from `client/history.txt`.
The source DLL SHA-256 is `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205`.
The runtime dump SHA-256 is `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0`.
Both images have base `38ff0000`, timestamp `543cb835`, entry RVA `008c5d6d`, and image size `01cb0a00`.
The earlier record does not state the dump extraction method. This task reused that recorded dump without a new client run.

The research combined native code, extracted X2UI bytecode, read-only compact rows, and current server code.
Local evidence is in `.tools-re/crime-20260926/native` in the cluster workspace.
That directory contains hashes, addresses, compact extracts, and disassembly. Raw client assets do not belong in this source repository.

### Court packet bodies

All listed packets use level 1. The sizes exclude the transport frame.
Integers use the current little-endian encoding. No listed body has an optional tail.

| Packet | Opcode | Fields in wire order | Bytes | Native evidence |
| --- | --- | --- | ---: | --- |
| `CSJuryVerdict` | `0x75` | `uint32 trialId`, `int32 seat`, `uint8 verdict` | 9 | Producer `3907c720`, serializer `397ba580`. |
| `CSJuryEndTestimony` | `0x73` | `uint32 trialId`, `int32 seat` | 8 | Producer `3907c8a0`, serializer `397b0ed0`. |
| `CSCancelTrial` | `0x74` | `uint32 trialId` | 4 | Producer `3907c8f0`, serializer `397b0f10`. |
| `SCChangeJuryVerdictCount` | `0x17a` | `int32 count`, `int32 total` | 8 | Parser `397af940`, consumer `3907c260`. |
| `SCTrialCanceled` | `0x183` | `uint32 trialId` | 4 | Parser `397afa30`, consumer `3907bc30`. |
| `SCSummonJury` | `0x173` | `uint32 trialId`, `int32 courtBank`, `int32 seat` | 12 | Parser `397af830`, consumer `39080900`. |
| `SCJuryBeSeated` | `0x174` | `bool isWest`, `uint32 trialId`, `int32 courtBank`, `int32 seat` | 13 | Parser `397af880`, consumer `3907bcc0`. |

`CSJuryVerdict` accepts indices 1 through 6. Index 1 means not guilty. Index 0 is not a player choice.
Native `39080000` computes the other choices from the base sentence in milliseconds:

| Choice | Sentence in whole minutes |
| ---: | --- |
| 2 | Truncate `baseMs * 0.2 / 60000`. |
| 3 | Truncate `baseMs * 0.5 / 60000`. |
| 4 | Truncate `baseMs * 0.8 / 60000`. |
| 5 | Divide `baseMs / 60000` as integers. |
| 6 | Truncate `baseMs * 1.2 / 60000`. |

The native float constants are `3999dd34`, `3999c47c`, `399a1d94`, and `399a1d98`.
The `SHOW_VERDICTS` event and `usertrial/verdict.alb` preserve that order.
`SCChangeTrialState` uses milliseconds. Native `39080000` divides its clock by 1000 to calculate expiry.

The defendant waiting window sends cancellation while no juror accepted the trial.
`SCTrialWaitStatus` opens the queue window without a phase change.
Native phase 2 opens the waiting-for-jury window. Phases 3 and 4 show crime records.
The current server sends queue status in phase 0. Its phase 1 is a transient server step.
The phase 0-or-2 server gate is a lifecycle guard, not proof that the native client forbids phase 1.

The verdict-count packet has no trial ID. Its recipients must belong to the exact trial.
The cancellation body has a trial ID, but native `3907bc30` ignores it and clears all local trial state.
A stale cancellation must not close a newer trial. Normal verdict completion must not send a second cancellation notification.

### Jury chairs

Native `3907bcc0` accepts seats 0 through 4.
Its leading boolean selects Nuian chairs when true and Haranyan chairs when false.
Its court field is a local bank index, 0 for the main court and 1 for the alternate court.
The lookup uses `bank * 5 + seat`.

| Region | Main bank, index 0 | Alternate bank, index 1 |
| --- | --- | --- |
| Nuian, boolean true | Templates 4937–4941 | Templates 4942–4946 |
| Haranyan, boolean false | Templates 4947–4951 | Templates 4952–4956 |

Native initializers `3998db80` and `3998dc00` set these arrays.
`SCSummonJury` consumer `39080900` stores the same bank for later UI refresh and the `CSJurySummoned` reply.
Server court IDs 1 through 4 remain internal identifiers. They are not native bank indices.

### Crime reports and court chat

`CSReportCrime`, opcode `0x76`, contains an object ID, skill ID, next phase, function key, and bounded UTF-8 text.
Its producer is `3907bfd0`. Serializer `397ba600` confirms the current field widths.
Native `393bb090` writes the cached fields before it emits `REPORT_CRIME`, event `0x202`.
The descriptor loader `39673b90` binds the phase to `doodad_funcs.next_phase` and the final key to `doodad_funcs.id`.
The final key is not `actual_func_id`. The server must match it to the current evidence function.
Callers `393b3ec0` and `393b7440` supply the selected interaction skill. Both use skill lookup `396f6390` before the report dispatcher.

Court chat permits the current defendant and accepted jurors to speak.
Valid audience members can receive those messages. Terminal trials cannot send court chat.
The retained audience check uses a seat within 5 metres in the same instance.
These are server authorization rules. Native research did not establish a different speaker rule or audience range.

## Prison, Wanted, and quest scope

Buffs 631 and 2028 have a 1,800,000 ms authored duration, save rule 1, and `real_time=false`.
Their timers pause offline. They survive death unless a justice result explicitly removes them.
Wanted 3710 is a permanent marker with save rule 1.
Leech 4424, Retribution 2167, Prime Suspect 4863, and bot prison 4868 also have positive save rules.
Buff 8038 is described as reserved and unused. Persistence support does not activate it as a new gameplay penalty.

The persistence extension covers known justice penalties and the current bot-report and paid-buff exceptions.
It preserves the current filter for ordinary hostile buffs with the Normal save rule.
It also preserves the current behavior for save rules at or above `CharacterPersistent`.
An ordinary hostile effect needs its original caster and skill after restore. This batch does not add that broader restore path.
For example, Burn 1002 has a 3,000 ms damage tick and cannot safely restore with the recipient as its caster.

Loading keeps the last saved checkpoint. A second process stop before autosave must not remove the saved sentence.
The next character save replaces that checkpoint. Real-time expiry uses a wide calculation to avoid overflow after long offline periods.
Combat resets preserve justice penalties and bot-report markers. Explicit justice completion still removes the applicable state.

Item 32212 states that triggered Wanted continues even at 0 Crime Points.
The Wanted fix follows that rule. A reward that lowers crime must not clear an active Wanted marker.
The same item states that 0 Infamy Points permits departure from the pirate alliance.

The 10 negative crime-supply rows contain 7 live quest rewards and 3 orphan acts.
The live quests are 2916, 2926, 2936, 2935, 5198, 5197, and 5494.
Native `RewardCrimePoint`, through `3949d490` and `39378da0`, reads the kind-8 reward act with type `0x35`.
`questcontext/common.alb` puts that value in `disHonorPoint` and shows the `locale.money.dishonor` label.
`baselib/locale_helper.alb` maps the label to `MONEY_TEXT/dishonor_name`, localized as **Infamy Points**.
Quest 5494, Never Too Late, supplies -100 and offers to erase pirate records.
So the audit suggestion to remove the Infamy adjustment is incorrect. Negative quest rewards continue to reduce both point totals, with a floor of 0.

## Server rules

The native client does not define the base sentence formula, pirate sentence, or vote tie rule.
The server uses these rules:

- A murder contributes 20 minutes. A theft contributes 8 minutes. An assault contributes 0 minutes.
- A victim below level 30 gives that contribution a factor of 10.
- The total uses the integer factor `1 + InfamyPoint / 1000`.
- Victim level comes from the same character data whether the victim is online or offline.
- `Justice.PirateSentenceMinutes` sets the pirate base sentence. Its default and shipped value are 40 minutes.
- A tie between submitted verdicts selects the lower sentence, including acquittal.
- Accepted jury seats with no submitted verdict before timeout use legacy choice 3, which now uses the confirmed half-base factor.
- A trial with no jurors, or a trial where all jurors leave before a verdict, uses the full base sentence.

The user approved the lower tied verdict and the 40-minute pirate setting. These are server policy, not exact-client rules.
The pirate setting uses whole minutes. The server clamps it to 0 through 35,791 minutes before packet conversion.
The legacy 10-second minimum still applies to a guilty result whose new sentence is 0 minutes.

Issue #501 authorizes the old sentence carry-over.
The active old prison timer continues online during the new trial and pauses offline.
The new verdict factor applies to the new sentence only. The old time left at the verdict is then added.
An acquittal of the new case does not remove the old conviction.
The legacy 10-second minimum prison effect remains. The code uses wide arithmetic before it clamps the final duration.

Pending sentence storage keeps minutes as its unit. Value 0 means no pending case.
Value -1 marks a pending case whose new sentence is 0 minutes. This avoids confusion with old rows that use region 0 by default.

Official AAEmu PR 1574 targets client 10.0.2.13 and documents 50/80/100/130/150 percent sentence choices.
Those values differ from r208022. Its result policy is comparison material, not authority for this client.
No direct copy of its rates or vote policy belongs in this batch.

## Pending human checks

All 33 checks below remain pending. Copy them to HUMAN VALIDATION #573 when the release is complete.
Use the released server and exact r208022 client. Record the source commit, character names, court, and result.
Use a separate test server for process-stop checks and packet tooling.
GM setup means a controlled test character or test case. Do not change normal character progress for test setup.
One operator can use several test clients when a check needs more than 1 client.

### Saved penalties, #454

- [ ] **HV454-1.** Need: 1 client and a character with a prison sentence. Record the timer. Log out for 2 minutes and reconnect. Check that the timer pauses offline.
- [ ] **HV454-2.** Need: 1 client and GM setup for Leech or Retribution with less than 60 seconds left. Reconnect. Check that the penalty and its timer remain.
- [ ] **HV454-3.** Need: an operator-controlled test server and 1 client. Save a sentence. Restart the server twice before another character save. Check that both restores keep the saved sentence checkpoint.

### Escaped prisoners, #501

- [ ] **HV501-1.** Need: 2 clients, a sentenced character, a prison escape route, and a combat pet. Escape and die to the other player. Repeat with the other player's pet. Check for a new arrest in both cases.
- [ ] **HV501-2.** Need: GM setup for a second conviction with a known new base sentence and old time left. Record old time at the verdict. Check that the verdict factor changes only the new sentence. Check that the server then adds the old time left.
- [ ] **HV501-3.** Need: GM setup for an acquittal of a new case while an old sentence remains. Check that the acquittal does not remove the old conviction. Reconnect and check that its timer still pauses offline.

### Wanted and redemption, #502

- [ ] **HV502-1.** Need: 1 client, Wanted status, and eligible redemption quests or GM setup. Lower Crime Points below 50, then to 0. Reconnect and check that Wanted remains.
- [ ] **HV502-2.** Need: 1 client and a pirate eligible for quest 5494, Never Too Late. Record Crime Points and Infamy Points before the reward. Check that both totals fall by 100, with a floor of 0. Use GM setup for a separate case that reaches 0 Infamy Points. Check departure from the pirate alliance.

### Crime reports, #504

The report skill has an authored maximum range of 4 metres. The server range calculation includes height and the caster's model radius.

- [ ] **HV504-1.** Need: valid bloodstain or footprint evidence and an eligible reporter. Use normal gameplay or GM setup to prepare the evidence. Report nearby. Check for exactly 1 record, the correct point charge, and the next evidence phase.
- [ ] **HV504-2.** Need: valid evidence and 1 reporter client. Open the report dialog, move outside interaction range, and submit. Check that no record or point change occurs. Move back into range and check that a normal report still works.
- [ ] **HV504-3.** Need: controlled packet tooling, test evidence, and test characters. Send the wrong object, function key, next phase, and skill. Repeat with the evidence owner, a dead reporter, and a prisoner. Check that each request leaves history and points unchanged.
- [ ] **HV504-4.** Need: 2 reporter clients and the same fresh evidence. Submit both reports close together. Check for exactly 1 record and 1 point charge. Check that the evidence changes phase once.
- [ ] **HV504-5.** Need: 1 reporter client and GM setup for authored altered evidence with kind 0 and points 0. Report the evidence. Check its authored phase change. Check that no invalid record, point charge, or report achievement appears.

### Trial requests and seats, #505

- [ ] **HV505-1.** Need: 6 clients, 1 defendant, 5 eligible jurors, and GM control of court assignment. Check that all 5 jurors reach different correct chairs. Refresh the court UI before voting. Check that every juror receives the verdict window. Repeat in both regions and both alternate courts.
- [ ] **HV505-2.** Need: GM trials with a 7-minute base sentence and enough clients to finish each trial. Check the acquittal and all 5 guilty choices in separate trials. The guilty choices must show 1, 3, 5, 7, and 8 minutes. Check that the applied new sentence matches the selected result.
- [ ] **HV505-3.** Need: an active test trial and controlled packet tooling. Send another juror's seat, another trial ID, duplicate confirmations, duplicate votes, and verdicts 0 and 7. Check that the requests do not change the valid vote, phase, or sentence.
- [ ] **HV505-4.** Need: a defendant client and 1 eligible juror client. Cancel in the trial queue before any juror accepts. Check that the waiting window closes and the default sentence applies once. Repeat the cancel action. Start another test case and accept a juror. Check that a late cancel cannot skip that trial.

- [ ] **HV505-5.** Need: GM trials and 2 or 4 juror clients. Submit equal counts for 2 different verdicts. Check that the lower sentence wins. Reverse the vote order and repeat. Include a tie between acquittal and a guilty verdict.
- [ ] **HV505-6.** Need: a pirate test character and GM trial setup. Check that the trial shows a 40-minute base sentence. Select the full-base guilty choice. Check that the new sentence uses 40 minutes before any old sentence time is added.

### Prisoner access and delayed transfers, #506

- [ ] **HV506-1.** Need: 1 sentenced character and an available battlefield queue. Request entry and check the prisoner refusal. Repeat after the sentence ends. Check that the normal queue request works.
- [ ] **HV506-2.** Need: 1 sentenced character and an accessible dungeon or system-instance portal. Check that both entrance types refuse the prisoner. Check that justice transport still reaches the correct jail or court.
- [ ] **HV506-3.** Need: 2 clients, an available battlefield, and GM timing control. Receive a sentence after an invitation, then try to accept. Repeat after battlefield travel starts but before entry completes. Check that jail position, original faction, and sentence remain correct. Wait for the old battlefield to end. Check that no old return teleport moves the character.
- [ ] **HV506-4.** Need: 1 client, an accessible dungeon, and GM control of delayed instance loading. Request entry, then receive a sentence before loading completes. Let loading finish. Check that the character stays in jail with the correct sentence. Repeat with a system instance. Check that an old dungeon leave event cannot restore the earlier return position.
- [ ] **HV506-5.** Need: a prepared arena match and GM timing control. Receive a sentence during the 3-second delay before the arena start reset. Let that reset run. Check that jail position, original faction, and the prison effect remain correct.
- [ ] **HV506-6.** Need: 2 arena clients and GM timing control. Prepare a kill, then sentence the victim during the 6-second reset delay. Check that the reset does not move or revive that character outside jail. Repeat with the killer. Check that the sentence and original faction remain correct in both cases.

### Courtroom chat and role changes, #518

- [ ] **HV518-1.** Need: 3 clients with a defendant, an accepted juror, and a valid audience member. Send court chat from the defendant and juror. Check that the current trial participants and audience receive each message.
- [ ] **HV518-2.** Need: an active trial, an outsider, an audience member, and a second active courtroom. Send court chat from the outsider and audience member. Check that both receive `ChatNotInTrial`. Send valid chat in each court. Check that messages do not reach the other trial.
- [ ] **HV518-3.** Need: an audience client and active court speakers. Leave the audience, then change courtrooms. Check that old court chat stops after each change. End the trial and check that its former speakers cannot send more court chat.
- [ ] **HV518-4.** Need: 2 prepared cases, test clients, and GM control of trial timing. Try to join another audience as a defendant or accepted juror. Repeat while the old case awaits final cleanup. Check that each join fails. Make a former audience member a defendant or juror. Check that old audience membership ends before the new role starts.

### Court notifications, #146

- [ ] **HV146-1.** Need: 5 clients, with 1 defendant, 3 jurors, and 1 audience member, plus GM trial setup. Submit votes separately. Check that all 5 clients see the live count increase once per accepted vote. Finish the case. Check that the normal result does not also show a cancellation.
- [ ] **HV146-2.** Need: an active trial, other participant clients, an audience client, and control of the defendant connection. Disconnect the defendant during testimony. Check that the correct participant and audience windows close. Reconnect the defendant and check pending sentence recovery.
- [ ] **HV146-3.** Need: controlled packet tooling or an instrumented test server and 2 successive trials. Finish one case, then start another in the same courtroom. Run stale server callbacks for the old count, timer, and cancellation. Check that the server sends no stale notice and keeps the new trial unchanged.
- [ ] **HV146-4.** Need: a defendant, at least 2 accepted jurors, an audience client, and control of a juror connection. Disconnect a juror during voting. Repeat after that juror submits a vote. Check that all valid recipients see the correct submitted count and current total. Check that no stale vote finishes the case twice.
