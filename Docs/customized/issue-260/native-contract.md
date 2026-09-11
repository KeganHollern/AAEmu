# r208022 quest source checks

This record supports aaemu-cluster issue 260. The source base is
`61f535c0c0953f1c8da249c77ac74c330a74c2a4` on `deployment/r208022`.

## Client identity and method

The local `client/history.txt` identifies version `208022`.

| Input | SHA-256 |
| --- | --- |
| `client/bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| `.tools-re/dumps/x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| `client/bin32/cryanimation.dll` | `c630e58a62e36b33fd4dedad8917aad000474bf35d7054d9a2bbe00fb057ca6f` |

Both PE images have timestamp `2014-10-14 00:44:21 UTC`, entry RVA
`0x008c5d6d`, preferred base `0x38ff0000`, and image size `0x01cb0a00`.
The supplied dump has no separate capture record. Its native strings,
registrations, packet constructors, and decoded bodies agree with the exact
client. This record does not claim a new live capture.

Ghidra `12.1.3` and `objdump` supplied the native code evidence. Ghidra used
the PE loader. The primary analysis reached its time limit, so each relevant
function also received direct disassembly and decompilation. The addresses
below are virtual addresses in the supplied dump. Subtract `0x38ff0000` to
get an RVA. No live server or client state changed during this research.

The CGA check also used the exact, unprotected `cryanimation.dll` PE image.
Its timestamp is `2014-10-14 00:44:15 UTC`, and its preferred base is `0x31500000`.
Ghidra completed its analysis in 46 seconds.
Addresses that start with `315` or `316` below refer to this image.

## Confirmed source contract

| Request | Native path | Source rule |
| --- | --- | --- |
| Accept NPC quest | `3949b2b0 -> 3937a2d0 -> 393761f0` | NPC sphere edge distance is less than 3 m. |
| Report NPC quest | `3949b5c0 -> 3937b860 -> 393761f0` | The same NPC source check applies. |
| Talk quest | `3949b4e0 -> 3937aad0 -> 39093860` | NPC sphere edge distance is less than 3 m. |
| Accept/report doodad quest | `393761f0 -> 39093800` | Nearest doodad shape edge distance is less than 9 m. |
| Express emotion | `393c39d0 -> 393995d0`, then `391872f0` | The packet uses the actor and its selected target. |

`393761f0` resolves the NPC or doodad from the client object managers before
the distance check. It also needs the doodad model. The float at `39ac759c`
is `9.0`, with bytes `00 00 10 41`.

The 2 distance overloads are different. `39093860` takes the square root
of its range argument. `39093800` does not. Both compare with strict `<`.
This difference is visible in the instructions, not only in the decompiler.

`3989bf90` compares the actor's aggregate sphere with each target shape.
It returns the nearest shape edge distance. Sphere dispatch reaches
`398b1af0`, which computes the 3D center distance minus both radii.
The native code does not omit vertical distance.

### Actor geometry

`390b3ae0` gets the model shape through virtual method `+0xbc`.
`390bf920` applies the model transform to the shape group.
`ActorUnitModel::Init`, at `390d5880`, creates a sphere with local center
`(0, 0, height)` and the actor model radius.

The native actor-model loader is `39736340`. Its SELECT lists `height` at
column `0x26` and `radius` at column `0x3a`. It copies these values through
`39703d70` to offsets `+0x90` and `+0xe0`. The actor initializer reads those
same offsets. These are the server's `ActorModel.Height` and `Radius` fields.

Sphere creation is `3989d2a0`. Its virtual table is `39a1fe58`.
The type getter `3989b0d0` returns sphere type `0`. Transform method
`3989b100` applies scale, quaternion rotation, and world translation to the
local center. It also multiplies the radius by scale.

The server rejects unknown actor geometry. It does not replace unknown
models with point distance. The compact contains 46 `PrefabModel` NPCs and
1 `VehicleModel` NPC. Only test NPC `13632` occurs in the direct accept,
report, or talk acts among these non-actor models.

### Doodad geometry

The `ClientDoodad` virtual table is `399d23b4`. Its shape getter at `+0x18`
is `393a1d10`. This method applies the doodad position, quaternion, and scale
to the model shape group at `model + 0x10`.

`IDoodadModel::LoadProxy`, at `393ad570`, gets authored helper shapes through
`39152fc0`. This function reads CGF helper subobjects with names that start
with `$aimpoint` or `$aimbox`.

- A CGF `$aimpoint` supplies a sphere. Its radius is helper size times
  `0.5`, with the model transform scale.
- A CGF `$aimbox` supplies an oriented box. The helper transform supplies
  its orientation and axis sizes.
- CGF unit conversion occurs in the model loader before these runtime
  structures. The extraction record documents the file-unit conversion.

The native fallback is not the mesh bounds. `LoadDoodadModel`, at
`393b03b0`, checks for an empty shape list through `3989ae50`. It adds the
sphere from `3989ac20` through `3989d1c0`. The sphere at `39acb758` has center
`(0, 0, 0)` and radius `0.5`. Brush callbacks `393ad800` and `393ad750`
use the same fallback after the model stream completes.

The mesh bounds only support a warning:
`no aim point in big doodad model(%s)`. They do not change this sphere.
The server's `SimRadius` is not this interaction radius.

Prefab loader `393ad920` asks `391049b0` for shapes from every prefab element.
The element callbacks include:

- `390fe8b0` and `390ff010`: CGF helpers with the prefab element transform.
- `39101500 -> 3989d420`: Comment objects with `aimPoint` or `aimbox` names.

A prefab `aimPoint` Comment is the sphere radius in meters. `3989d420`
passes the parsed value directly to `3989d2a0`. It does not divide it by 2.
The element matrix supplies the center. A prefab `aimbox` Comment supplies
3 half-extents. The current quest source catalog contains spheres only.

The server uses the current phase model when it is not empty. Otherwise,
it uses the doodad template model. The embedded catalog contains only
models whose shapes the extraction record confirms. Unknown models reject
the request instead of using an invented radius.

### Entity CGA helpers

The Entity callback `390ff010` gets the character for the prefab slot.
It calls virtual method `+0x24`, then virtual method `+0x34` on the returned model.
It passes the returned static object to `39152fc0` with the prefab element matrix.

The exact animation DLL resolves these methods:

- `CCharInstance` has virtual table `31675754`. Method `+0x24`, at `31520480`, returns the model pointer at `+0x180`.
- `CCharacterModel` has virtual table `3167dea4`. Method `+0x34`, at `3160c530`, returns its static object through a smart pointer.
- The model stores that static object at `+0x5c`.

`LoadNewCGA`, at `315e3b30`, creates the model with type `0x55aa55aa` and calls `315e2220`.
That method loads a static object from the complete model filename through engine method `+0xc8`.
The geometry-name argument is null. The assignment at `315e2c33` stores the result at model offset `+0x5c`.
The quest callback thus uses the root static object, not the current animation pose.
The extraction record describes the parent transforms within that static object.

### Emote selection and visibility

`391872f0` constructs opcode `0xad` through `397ac320`. It reads the actor
object ID at `+8` and selected target at `+0x1aa0`. It has no distance test.
The slash-command path `393c39d0` calls the animation skill first, through
`393995d0 -> 393989e0`, and then sends this packet.

The compact links emotes to self-target animation skills. Most have
`max_range = 4`. This value alone does not establish a quest NPC emote
distance. The prior achievement code's 20 m check is also not such evidence.
Neither value supplies a new quest distance constant.

The native target selection path `39429e40` resolves the target, checks
`3935a610`, and updates `+0x1aa0`. That check rejects a hidden-state flag
and conditionally calls detection-range function `3935a2b0`. It does not
apply a common distance limit to normal visible NPCs. The object removal
path `39362560` clears a selected target when the client removes that object.

The server now needs a selected, visible NPC in the same world instance
for an emote quest event. This closes stale, hidden, wrong-instance, and
unstreamed target paths. These are the supported target limits for this
packet. The server does not add a numeric proximity rule without evidence.

## Server authority and limits

`QuestInteraction` checks the actual `ParentWorld` reference, transform world
and instance IDs, visible state, stealth, and streamed region references.
Region coordinates alone are not an instance identity. The server also
checks the exact NPC or doodad geometry before accept, report, or talk events.

These checks precede quest state changes and NPC target changes. Ambiguous
NPC/doodad source pairs reject. Client packets with a sphere ID also reject,
even when the packet contains another source.

Client starts also check authored Start conditions before quest allocation.
A missing or different world source cannot start a world-source-only quest.
Sphere-only conditions reject all client starts, including zero-source packets.
Non-world alternatives keep their normal checks. Server starts keep their current path.

Only `SphereQuestManager` entry events can call the internal sphere-start
method. The current sphere geometry and unit requirement checks remain in
that server path. This change does not trust a client sphere ID.

The native quest distance functions do not cast a terrain ray or request a
navigation path. The server's visibility check is not a line-of-sight check.
Its physics world does not contain all client static collision geometry.
This change does not claim obstacle occlusion or terrain reachability support.

## Tests and manual checks

The focused tests cover strict NPC and doodad boundaries, vertical distance,
model height, model scale, rotated centers, unknown geometry, hidden sources,
wrong instances, remote objects, selected emote targets, and sphere spoofing.
The existing early-completion tests use visible nearby NPC fixtures.
The source-less packet tests also cover alternate Start conditions and sphere-only quests.
The phase tests cover known overrides, empty overrides, unknown models, and URI normalization.

After the deployment branch merge, the focused command passed 211 tests with 0 failures:

```sh
dotnet run --project AAEmu.UnitTests/AAEmu.UnitTests.csproj -- --treenode-filter '/*/*/Quest*/*'
```

The full unit suite also passed 2,439 tests with 0 failures:

```sh
dotnet run --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --no-build
```

All 12 catalog extraction tests passed. The final catalog SHA-256 is
`4453b040bbaeb0d384f99bfbd9fc6073603f31811bc4bb7f4523de616dabc645`.

After deployment, check these behaviors with the exact client:

1. Accept and report a normal NPC quest at its interaction boundary.
2. Complete a talk quest near the NPC, then try it from another floor.
3. Accept and report a doodad quest near a small source and an authored large source.
4. Complete an emote objective with the intended selected NPC.
5. Enter a quest starter sphere and make sure that its quest starts once.

These checks need human gameplay validation. Automated tests do not claim
that validation.
