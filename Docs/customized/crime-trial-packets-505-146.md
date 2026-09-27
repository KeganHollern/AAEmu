# Trial packet ownership and notifications

This change addresses aaemu-cluster issues #505 and #146 for client r208022.
It changes server trial state and packet handling. It needs no client content or
compact update.

## Confirmed client contract

The source client DLL has SHA-256
`3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205`.
The matching runtime dump has SHA-256
`a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0`.
Both identify revision 208022 and PE timestamp `543cb835`.

All packet bodies below use little-endian fields and level 1. The sizes exclude
the transport frame. The native serializers and parsers agree with the current
server field widths. The G2C factory allocation sizes give another width check.

| Packet | Opcode | Body | Bytes |
| --- | --- | --- | ---: |
| CSJuryVerdict | 0x75 | uint32 trial ID, int32 seat, uint8 choice | 9 |
| CSJuryEndTestimony | 0x73 | uint32 trial ID, int32 seat | 8 |
| CSCancelTrial | 0x74 | uint32 trial ID | 4 |
| SCSummonJury | 0x173 | uint32 trial ID, int32 court bank, int32 seat | 12 |
| SCJuryBeSeated | 0x174 | bool west, uint32 trial ID, int32 court bank, int32 seat | 13 |
| SCChangeJuryVerdictCount | 0x17a | int32 submitted count, int32 total | 8 |
| SCTrialCanceled | 0x183 | uint32 trial ID | 4 |

The C2G bodies have no optional or trailing fields. The server rejects another
body size before it changes state.

Native `ChooseVerdict` at `3907c720` accepts only choices 1 through 6. It sends
the selected index unchanged. Choice 1 means not guilty. Choices 2 through 6
use the five sentence values that native `39080000` gives to `SHOW_VERDICTS`.
Those values truncate the base sentence to whole minutes after factors 0.2,
0.5, 0.8, 1, and 1.2. The server now uses this rounding instead of nearest-minute
rounding. The native result path accepts the final result from the server. It
does not prove a server tie rule or a base sentence formula.

The native cancel producer at `3907c8f0` checks for zero jurors. The defendant
waiting window offers cancel before the case reaches the crime-record display.
`SCTrialWaitStatus` directly opens the queue window. Phase 2 opens the jury-wait
window. Phases 3 and 4 show the crime records and close the waiting window.
The server accepts cancel in its waiting phases 0 and 2, with no accepted juror
and no result. Exclusion of its transient phase 1 is a server state guard.
It is not a claim that the native client forbids phase 1.

The count packet updates the submitted-vote count. It does not reveal individual
votes and carries no trial ID. Native cancellation clears all local trial state
and ignores its packet ID. The server must send both packets only to participants
of the exact current trial. An ordinary verdict does not send cancellation.
When a juror leaves during voting, the server sends the new vote count and total.

Native jury seats use indices 0 through 4. The seating consumer at `3907bcc0`
rejects index 5. Its continent flag is true for Nuia and false for Haranya. The
court field is a local bank, 0 or 1. The client stores the same bank from the
summon packet and uses it again when the UI refreshes. The server derives these
wire values from the native chair arrays, templates 4937 through 4956. Its global
court IDs and authored placement labels remain unchanged.

The phase countdown uses milliseconds. Native `39080000` stores that value and
divides it by 1000 for its local expiry. The server clamps expired deadlines to
zero instead of casting a negative duration to uint32.

## Server state rules

The sender must own the exact accepted juror seat. Confirmation and voting each
run only in their matching phase. A seat confirms once and votes once. An unknown,
ended, replaced, or foreign trial cannot change a vote, timer, or sentence.

A trial claims its result once before counters, evidence, and sentence effects.
The persistence lock precedes the trial lock. Jury admission also uses its own
reservation lock before the trial lock. Queue eligibility checks run outside the
queue lock. This keeps packet handlers, timer transitions, disconnect, and saves
in the same lock order.

A participant stays reserved until the old case sends its closing packets and
finishes cleanup. Cleanup clears a courtroom only when its current trial still
matches. A stale timer cannot remove a replacement trial.

Disconnect settles the trial before the final character save. It preserves the
pending default sentence for login recovery. The timer does not later change an
old offline Character. Juror return and trial removal use the same cleanup path.
Cleanup removes jury buffs even when the escaped juror does not return or earn
a jury point. Seat admission removes old audience membership before the new
juror receives packets.

A guilty result displays the new sentence plus the old unserved milliseconds.
The old sentence is added after the new verdict factor. Acquittal displays zero
for the new charge and preserves an earlier prison sentence.

The zero-submitted-vote timeout preserves the legacy server choice 3, which
applies half the base sentence. This is not a native-authored rule. It differs
from no-jury cancellation, which keeps the full default sentence. The approved
server tie rule selects the lower verdict among equally frequent choices. A tie
with choice 1 acquits the new charge. Seat order does not change the result.
The approved pirate base sentence uses the explicit PirateSentenceMinutes setting,
which defaults to 40. Neither policy is a native-confirmed server rule.

## Comparison with official AAEmu

Official PR 1574, merge `0b7662f1fd0a14740bf6ebba56a799e8028e0628`, supplied
useful comparison code for seat ownership and phase gates. Its aggregate guilty
vote policy and its 50/80/100/130/150 sentence ratios do not prove r208022 behavior.
This change does not copy those rules. Exact r208022 evidence takes precedence.

## Validation

The focused tests cover valid and invalid verdicts, seat ownership, repeated
confirmation, exact packet bodies, truncated and trailing input, result claims,
concurrent duplicate votes, disconnect cleanup, and stale trial identity.
The Release build passed with no errors. All 75 focused tests passed without
skips: 65 trial-rule cases and 10 packet cases.

The combined sentence branch adds 30 passing lifecycle cases. These use actual
trial results and prison buffs. They cover pending 37-minute recovery without
jury eligibility, repeat arrests, old sentence carry, acquittal, default cancel,
escaped-juror cleanup, audience removal, and the production death arrest query.
The owner tests reject a reused pet object ID with a different persistent owner.
Policy tests cover tied-vote permutations, acquittal ties, vote plurality, the
40-minute default, changed pirate values, zero, bounds, and the non-pirate formula.
Timeout tests distinguish accepted jurors with no submitted votes from no jurors.
Both final verdicts keep their result when all jurors leave before court cleanup.

## Pending human checks for #573

- Check all five seats in each of the four configured courtrooms. Each juror
  must appear at the correct chair and receive the verdict window. Refresh the
  court UI before the vote to check the stored court bank.
- Use one defendant, three jurors, and one observer in a GM test trial. Let each
  juror vote. Check that all five clients see the submitted count rise once per vote.
- Submit an acquittal and each guilty tier in separate trials. Use a 7-minute
  base sentence. The displayed guilty choices must be 1, 3, 5, 7, and 8 minutes.
- Use 2 jurors with different guilty choices. The lower choice must win the tie.
  Repeat with an acquittal vote and confirm acquittal of the new charge.
- Repeat a guilty result with old unserved prison time. The result must show the
  new sentence plus that old time. Acquittal must keep the old sentence active.
- Cancel while the defendant waits with no accepted juror. Check that the waiting
  UI closes and the default sentence applies once. Repeat the button action.
- Accept one juror, then attempt a late cancel. The court must keep the case.
- Disconnect the defendant during testimony. Check that the other clients close
  the old case. Reconnect and check the pending sentence recovery.
- Finish a case, then start another in the same courtroom. No old count, timer,
  or cancellation packet must close or change the new case.

These checks remain pending. Automated packet tests do not establish client
validation or retail server equivalence.
