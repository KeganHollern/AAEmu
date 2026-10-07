# World books and doodad bubbles: issues 645 and 647

## Result

The r208022 client opens these interfaces locally. The missing server action is
`CSChangeDoodadPhasePacket`, which only wrote a log before this change.
Both client functions send this packet for a positive authored `next_phase`.
They do not start the interaction skill through the normal skill path.

The server now checks the current doodad function before it applies the phase.
The check includes the function row, skill, next phase, permission, world,
instance, current object identity, removal state, and distance.
Only `DoodadFuncBubble` and `DoodadFuncOpenPaper` use this new path.

The normal `DoChangePhase` path runs phase functions, updates persistence when
applicable, sends `SCDoodadPhaseChangedPacket`, and raises world phase events.
The persistence lock keeps validation and the transition together.
A repeated request cannot reuse a function from the previous phase.
The client can use the next authored function after the phase changes.

The two server function templates now explicitly leave `ToNextPhase` false.
A separate server skill must not repeat the local interface or phase change.
In particular, `next_phase=-1` does not remove a readable book.
No client, compact, SQL, or shared doodad lifecycle change is needed.

## Exact client evidence

Research used AAEmu source `ccb787a1d0db21d5b971ec08820fd10799a7d182`.
`client/history.txt` identifies revision `208022`.
The source and runtime dump share these PE values:

| Field | Value |
| --- | --- |
| Timestamp | `543cb835` |
| Image base | `38ff0000` |
| Entry RVA | `008c5d6d` |
| Image size | `01cb0a00` |

| Input | SHA-256 |
| --- | --- |
| `client/bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| Runtime dump `x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| `game/scriptsbin/x2ui/webbrowser/book.alb` | `e13b7c129bd6d1b581ee2cf20a52fd49a891223f1627dbaf91f10345369af338` |
| Client compact | `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4` |
| Server compact | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |

The dump came from the prior local research archive. Its original capture
procedure was not repeated for this change. The hashes and matching PE values
identify the analyzed binary. Native addresses below use its image base.

Confirmed native behavior:

- `39673b90` loads `doodad_funcs`. It maps Bubble to type `0x3f` and
  OpenPaper to type `0x46`. The descriptor stores row ID at `+0`, skill ID
  at `+0x18`, and next phase at `+0x1c`.
- `393bc260` dispatches these types to `393b8240` and `393b8450`.
  Both report that they handled the interaction.
- `393b7440` skips the normal skill start when that dispatch reports success.
- Bubble `393b8240` calls `393703b0` with the object ID and authored bubble ID.
  That path resolves the bubble and starts its local presentation through
  `39375140` and `39374750`.
- OpenPaper `393b8450` emits event `0x201`, `OPEN_PAPER`, with a page or book ID.
  X2UI `book.alb` registers `OPEN_PAPER` at line 275. Lines 248–274 select the
  page or book window. The page window calls `RequestBookPage`.
- Both functions construct opcode `0xe9` only when `next_phase > 0`.
  The constructor is `397ac8d0`. Its vtable is `399d313c`.
  The serializer is `397bbbb0`.

The packet has direction C2G, level `1`, and opcode `0x0e9`.
The constructor, both producers, and serializer confirm this complete body:

| Body offset | Wire type | Meaning | Native object offset |
| --- | --- | --- | --- |
| `0` | `u24`, little endian | Doodad object ID | `+0x0c` |
| `3` | `u32`, little endian | Interaction skill ID | `+0x18` |
| `7` | `i32`, little endian | Authored next phase | `+0x10` |
| `11` | `u32`, little endian | `doodad_funcs.id`, not actual function ID | `+0x14` |

The serializer writes 15 bytes. It has no strings, lists, optional fields, or
trailing group. The server accepts exactly 15 bytes and rejects malformed bodies
before any state change. No new G2C interface packet is needed.

## Data and limits

The client compact has 60 Bubble rows and 112 OpenPaper rows.
The issue counts include rows without a reachable doodad template.
The server compact joins 43 Bubble rows and 110 OpenPaper rows to doodad templates.
This change does not restore deleted or unreachable content.

Representative supported transitions:

| Doodad | Current phase | Next phase | Skill | Function row | Actual function |
| --- | --- | --- | --- | --- | --- |
| Red Buto toy `6806` | `18326` | `18329` | `22584` | `14756` | `71`, bubble `25` |
| Red Buto toy `6806` | `18329` | `18326` | `22584` | `14780` | `79`, bubble `26` |
| Executioner statue `6167` | `16297` | `16295` | `20743` | `13413` | `98`, page `453` |
| Story shop sign `6162` | `16269` | `16271` | `16262` | `13514` | `100`, page `452` |
| Relief `4374` | `10968` | `10743` | `17015` | `9911` | `80`, page `100` |

The sign has a 1000 ms timer in phase `16271`, then returns to phase `16269`.
The other positive book rows belong to test doodad `4418`.

The server uses the current 3-metre service distance in 3 dimensions.
This is an approximation, not a recovered retail centre-distance rule.
The client uses `max_interaction_doodad_distance`, default `2.1`, with engine
geometry in `39093800`. This change does not copy that geometry algorithm.

## Automated validation

`CSChangeDoodadPhasePacketTests` covers 35 focused cases. The cases include:

- Native golden body, opcode, level, and complete response body.
- Both function types and the 3 real positive book rows.
- Sequential and concurrent duplicate requests.
- Authored return interaction after the first phase change.
- Truncation at every byte and unexpected trailing data.
- Invalid object, skill, phase, row, and actual-function identifiers.
- Other function types and functions outside the current phase.
- Permission, horizontal distance, vertical distance, world, and instance checks.
- Pending removal, deleted objects, removed objects, and replacement objects.
- Phase functions, phase events, and one response after a valid transition.
- No duplicate interface action or removal from a separate server function call.

Command:

```sh
dotnet test --project AAEmu.UnitTests --configuration Release --no-progress -- --treenode-filter '/*/*/CSChangeDoodadPhasePacketTests/*'
```

The combined release must run the full relevant test suites.

## Human validation

These checks still need the r208022 client. Automated tests do not confirm
visual presentation or book page rendering. Record results in cluster issue
[HUMAN VALIDATION 573](https://github.com/KeganHollern/aaemu-cluster/issues/573).
Use the release that selects this source change. A GM can prepare temporary
doodads with `/doodad spawn <templateId>`. Do not save these temporary spawns.
Approach within 2 metres before each interaction.

1. Spawn toy `6806`. Use its interaction twice, with a short pause between uses.
   Expect the authored bubble on each use. Expect phases `18326`, `18329`, then
   `18326`. A server trace can confirm the phase values. Expect no duplicate
   bubble, unexpected skill cast, or object removal.
2. Spawn sign `6162`. Read it and close the page. Expect page `452` and phase
   `16271`, then `16269` after 1 second. Read it again. Expect one page window
   and an object that remains usable.
3. Spawn statue `6167`. Use `/doodad phase change <objectId> 16297` to prepare it.
   Read it. Expect page `453` and phase `16295`. Repeat with relief `4374`,
   prepared at phase `10968`. Expect page `100` and phase `10743`.
4. Spawn noticeboard `4349`. Read it twice. Expect page `104` each time.
   Its authored `next_phase=-1` leaves the board in phase `10679`.
5. Reconnect near the test objects. Expect the current model and interaction
   for each server phase. A placed persistent toy also needs a restart check
   to confirm its saved phase in a normal housing session.

A page rendering failure can involve the client's web browser or page source.
Record that failure separately from the packet and server phase result.
