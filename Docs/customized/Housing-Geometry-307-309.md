# Housing geometry for r208022

## Source identity

The exact client reports `version 208022` in `client/history.txt`.
The source `client/bin32/x2game.dll` has SHA-256
`3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205`.
The analyzed runtime dump has SHA-256
`a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0`.
Native addresses below refer to that dump, with image base `0x38ff0000`.

## Confirmed garden footprint

Native `39325190` selects a garden footprint when `garden_radius > 0`.
Native `39897170` computes the number of 4 meter garden cells on each axis:

```text
N = trunc((2 * garden_radius + 3) * 0.25)
size = 4 * N
```

The native function accepts fewer than 12 cells.
The current compact has positive radii from 4 through 22 meters.
Their cell counts range from 2 through 11.
The radius names alone do not define an exact circle or a radius-sized square.
For example, radius 7.5 gives a 16 meter footprint, and radius 11 gives a 24 meter footprint.

Native `39326980` computes these XY bounds from the world position:

```text
minX = 4 * trunc((worldX - size / 2 + 2) * 0.25)
minY = 4 * trunc((worldY - size / 2 + 2) * 0.25)
maxX = minX + size
maxY = minY + size
```

The native integer conversion truncates toward zero.
This differs from floor for negative positions.
The footprint remains aligned with world axes, regardless of the house rotation.

Native `39326ab0` then adds `alley` to both minimum XY bounds.
It subtracts `alley` from both maximum XY bounds.
Native `39326b40` uses inclusive minimum edges and exclusive maximum edges.
These operations define garden placement permission.
They do not replace the full cell reservation footprint used by house placement.

`HousingFootprint.TryCreateGarden(position, radius, alley, out footprint)` provides these XY operations.
Pass zero for `alley` when the caller needs the full cell reservation.
Pass the template alley when the caller checks garden placement permission.
The caller must first match the world and instance.
The helper rejects zero-radius templates because those templates need their model bounds.
The helper does not supply an invented footprint for those templates.

Native `39326980` also gets minimum and maximum Z from the current house model bounds.
Native `39326ab0` extends those bounds with `extra_height_below` and `extra_height_above`.
The XY helper makes no claim about Z or support surfaces.

## Confirmed model mapping

The client compact stores model paths in `prefab_elements`.
Join `housings.main_model_id` to `models.id`, then `models.sub_id` to `prefab_elements.prefab_model_id`.
`prefab_elements.state_id` selects the model state.
One state can contain several model elements.
For example, housing 242 uses 3 elements for its normal state.
The path can identify a prefab XML entry or a direct model.
`prefab_models` containing only IDs does not make this mapping unavailable.

## Confirmed decoration predicates

Native `3903da00` gets the placement point and surface normal through a world physics ray.
Its entity mask is `0x107`, and its ray flags are `0x8f`.
The ray has a 50 meter length.
The normal Z determines the surface class:

| Normal Z | Surface class |
| --- | --- |
| Greater than 0.5 | Floor |
| From -0.5 through 0.5 | Wall |
| Less than -0.5 | Ceiling |

The floor path accepts `allow_on_floor`, `allow_pivot_on_garden`, or `allow_mesh_on_garden`.
The wall and ceiling paths use their respective flags.
The producer aligns the placement rotation to the support normal.

Native `3903c2e0` gets the decoration model bounds and applies the placement rotation.
Native `3903c5a0` checks all 8 transformed corners.
It uses the housing model box plus vertical world physics rays to classify the item as inside or outside.
An outside item needs a garden or wall permission.
An inside item needs a floor, wall, or ceiling permission.
`allow_mesh_on_garden` makes all corners stay within the garden bounds.
This flag is false for every current decoration row, but its native rule remains distinct.

The final overlap check uses a box primitive and `PrimitiveWorldIntersection` with entity mask `0x1f`.
The box changes its center and extent by 0.025 meters at its support surface.
This small gap permits normal contact with the support surface.
It does not permit unrelated overlap.

## Limits of the XY helper

The footprint helper does not complete issues #307 or #309 by itself.
Exact support, interior, and overlap checks need the client model bounds and relevant world physics geometry.
House-only meshes cannot replace the world ray and primitive intersection inputs.
The server must not call a radius approximation an exact model collision check.

## Authored collision data

`CryGeometryResolver` reads the client assets through a supplied file reader.
It does not need extracted assets in the source repository.
It reads CGF versions `0x744` and `0x745`, node version `0x823`, and compiled mesh version `0x800`.
The node hierarchy supplies transforms. CGF node translations use centimeters.
Prefab XML supplies local position, scale, and quaternions in `w,x,y,z` order.
Native `390ff370` reads the Comment named origin. Native `39110b40` subtracts
the final origin position from every child's translation after the complete
object list loads. This rebase applies to model bounds, physical parts,
animation poses, helpers, and WaterVolume children. The origin comment does
not become a helper object.
The resolver preserves prefab comments such as the house connector metadata.

The exact `cry3dengine.dll` has SHA-256
`34d6b73690d1a9d8d0ea0f5eb743fd0624107cfda28c1302826b19a3bc9546de`.
Its image base is `0x30000000`.
Native `30026370` defines the merged and compound model bounds.
An ordinary mesh node contributes bounds even when its vertex count is zero.
For a merged model, the native code uses the first mesh without its node transform.
For a compound model, it unions the normal mesh bounds with their node transforms.
Export flag bit 0 selects the merged path.
Native `301b9660` identifies helper nodes by their names.

The compiled mesh references up to 4 native physics chunks.
Chunk `0xCCCC0018`, version `0x800`, contains serialized collision geometry.
The resolver reads those proxies instead of the render triangles.
The source `cryphysics.dll` has SHA-256
`f9c52c405c60c3bb8da9ebf9a45c34bae8149c9e0b31a7e9260f2eb11822f6e2`.
Its image base is `0x34ff0000`.
An offline memory emulator restored the packed code for analysis.
No server connection or game session supplied that data.

Native `351540a0` reads the physical header and calls the shape loader.
Native `35153dd0` selects these shape formats:

| Type | Shape | Native reader |
| --- | --- | --- |
| 0 | Box | `351468f0` |
| 1 | Triangle mesh | `351ed480` |
| 4 | Sphere | `351c6fb0` |
| 5 | Cylinder | `3514f930` |
| 6 | Capsule | `3514f930` |

Mesh proxies can reference the compiled render vertex and triangle arrays.
The reader applies the authored vertex map and foreign triangle map in that case.
It preserves per-triangle material IDs and the primitive surface ID.
The collision queries use analytic primitives, triangle intersections, SAT, and support-map convex intersection.
An uncertain convex result returns `Indeterminate`.

Each part preserves its native physics type and original statobject group.
Foliage spine counts come from chunk `0xAAFC0005`.
Native `30030f20` uses these values to select the physical part flags.
Native `300343d0` also reads numeric `$picking` helper suffixes.
Native `39428ba0` returns this index, and `3903da00` refuses positive indices.
The nearest ray hit must retain this index. It must not disappear from the query.

## Doodad model and collision flags

Native `3932c120` loads the base doodad model for a decoration preview.
Native `393b0940` selects the current phase model for a placed doodad.
Native `393b03b0` uses the base model when the supplied phase model is empty.
An empty base model cannot create the preview.
For an unknown nonempty model scheme, the normal client uses `objects/box_nodraw.cgf`.
This rule includes the authored `a://invalid` value.
Missing brush models use the authored native fallback.
Missing animation models and prefab elements keep the native empty result.
Malformed files still report an error.

Native `393aa540` applies the template collision flags to all doodad physical parts.
The `no_collision` field sets `flagsAND=0` and `flagsOR=0x8000`.
This keeps ray collision and removes solid collision.
Otherwise, `collide_ship` controls bit `0x80`, and `collide_vehicle` controls bit `8`.

The material surface table contains `no_collide` values for some plants and cloth.
Native `301ac000` reads these values into material metadata.
Native `30030f20` uses compiled geometry layers when it creates physical parts.
It does not remove individual triangles with that material flag.
The query must not invent such a triangle filter.

## Animation limits

`HasAnimatedCollision` records whether a collision node or one of its ancestors has a controller.
An animated visual child does not make a static root proxy move.
The resolver retains separate pose requirements for the model bounds.
The CHR reader loads the authored live-LOD bone proxies and their bind transforms.
Native `301af880` reads compiled bones, and `301b1c00` reads compiled physical proxies.
The reader preserves analytic proxy boxes, spheres, cylinders, and capsules.
Native `3153b5b0` places these proxies with the current global bone transforms.
Native `31543950` calls `315391e0` to update non-articulated character physics after animation.
This includes static character physics, so an active CHR needs its current bone pose.

`LoadCharacterPose` resolves a named clip through the model CAL file and its includes.
The CAF reader supports authored controller version `0x829` and timing version `0x918`.
Native `315fbd40` selects packed quaternion formats `5` and `8`.
Their decoders are `315fb0b0` and `315fb4f0`.
Native `31653e80` uses shortest-path normalized linear interpolation between quaternion keys.
The sampler reads local bone tracks, then applies the parent hierarchy to each physical proxy.
Its returned model bounds use the native character bone selection.
Scene broad-phase checks must also include the physical proxy bounds.

Native `3910b080` requests the `Default` animation for a direct CGA model.
The `cga_loop` flag controls looping, not whether the animation starts.
Native `39108450` cancels that request when `GetAnimIDByName` cannot find the name.
The authored cat and worn machine CAL files contain no `Default` entry.
Their previews therefore use the bind pose.
Their placed doodads use named phase animations, which the CAF reader samples.
Native `3903c2e0` reads model bounds each time it builds the placement box.
An initial render box alone does not prove the bounds of an active model.

The local asset fixture parsed 612 distinct housing CGF, CGA, and CHR containers without format errors.
It also sampled all 4 authored cat and worn machine clips with their exact physical proxies.
Native `3161c640` clears a bone geometry pointer before it finds the authored proxy ID.
An absent ID keeps that pointer null, so the bone has no collision geometry.
A successful lookup transfers the proxy to the bone and erases its map entry through `31665910`.
The CHR reader uses the same transfer and missing-ID rules.
The wider fixture reads all 12 authored Mermaid proxies despite its 17 positive bone references.
The two world skeleton prefabs also retain their 6 and 7 valid physical parts.
The tests cover missing and repeated IDs without discarding other authored geometry.
The retained tests cover binary formats, transforms, intersection queries, and animation ancestry.

## Server animation clock

The server samples placed CGA, CHR, and prefab geometry from the persisted doodad phase time.
House geometry uses the placement time and current build model.
Placement previews use the native initial bounds.
All support and overlap queries for one placement use the same time.
`DoodadFuncAnimate.play_once` selects the last key or a repeated clip.
Native `393a4210` selects a random animation when a phase supplies several entries.
The server selects the entry with the lowest authored ID for repeatable collision checks.

The placement packet contains no animation time or random animation selection.
Native `393a4530` starts the client animation when the client loads the doodad model.
Native `393a4360` supplies a 0.5 second transition to that animation.
A client can thus show another animation time or selected clip.
The server uses the authored physical proxies at its own phase time.
It does not claim to reproduce each client's visual frame or transition.

These server clock rules remain downstream-only for the r208022 deployment.
No client content, compact input, or SQL schema changes accompany this release.

## CGA poses and bounds

`LoadPose(modelUri, elapsedSeconds)` samples direct CGA models and active prefab children.
Prefab samples use the authored `Speed` and `bLoop` values.
The caller supplies the animation clock. The packet does not contain a client animation time.
The server uses its placement and phase times as an explicit clock policy.
The client can start an animation when the object becomes visible, so its visual time can differ.

Native `315e2220` loads controller version `0x826`.
It reads TCB vector keys and relative angle-axis keys.
Native `315e5480` accumulates the rotation deltas into absolute quaternion keys.
Native `3158ad50`, `3158b070`, `3158b150`, and `3158b240` calculate vector tangents.
Native `315ead40` calculates the quaternion tangents.
Native `3162bae0` conjugates the sampled rotation and converts position centimeters to meters.
The current authored CGA fixture has no cyclic track flags or multiple-revolution key segments.
The reader rejects those unresolved formats instead of changing their interpolation.

Native `31549f80` calculates CGA bounds from the posed rigid joint objects.
It adds `0.2` meters to each side of any dimension smaller than `0.4` meters.
The CGA sampler applies this rule after the node hierarchy transforms.
It returns both sampled physical proxies and sampled rigid mesh bounds.
All 30 extracted CGA fixtures passed samples at `0`, `0.5`, and `2` seconds.

`LoadCgaPose` also reads named external ANM clips.
Native `315e39c0` removes the CGA base name and underscore to get the animation name.
Native `315e2220` maps ANM nodes to the first base joint with the same name CRC.
Later ANM entries with that name update the same joint controllers.
The crafting seal contains duplicate node names, so this rule matters for its pose.
The authored fixture includes the Halloween coffin, crafting seals, and active guard tower clips.

## Character definitions

Native `315d28b0` reads the `Model` element from a `CharacterDefinition` XML file.
It keeps the `File` and optional `Material` references separate.
The loader also reads attachment and shape deformation data.

The 12 affected shop pet prefabs reference CDF files with one CHR model and one material.
They contain no attachments, and all 8 shape deformation values equal zero.
The resolver loads the referenced skeleton and replaces the part material paths.
It resolves animation names through that skeleton's CAL file.
An attachment or nonzero shape deformation still needs its own authored geometry rules.
The reader does not discard those fields to accept an unsupported definition.

## Prefabs without solid models

Native `3910f570` loads each prefab object by its type.
Particle and decal objects use the default bounds getter `39104df0`.
That getter returns a reset box, so these objects do not extend the model bounds.
Comments and sounds do not supply solid geometry.

The world fixture includes 46 prefab paths without a solid model.
The resolver returns their empty part list. Effects without native bounds set
`HasModelBounds` to false. WaterVolume children supply native model bounds,
even though they do not supply a solid collision part.
It also excludes a missing animation child from the bounds of other valid children.
Placement candidates still need model bounds.
An empty effect in another part of the world does not cause a model-read failure for all housing queries.

## Character bounds

Native `3151d5d0` initializes character preview bounds through `31549f80` with mode zero.
This mode includes every bind bone position, including the root.
Runtime mode one uses the bone list from `3150df50` and `31527310`.
That list contains the bone palettes of all loaded skin LOD subsets.
It does not add every parent or every physical bone.

Mesh subset chunk `0xCCCC0017`, version `0x800`, stores those palettes when flag `2` is set.
Each subset has a count followed by a fixed array of 128 unsigned 16-bit indices.
Native `315ec4c0` builds the `_lod1.chr` and later LOD paths.
Native `315ec790` stops when a LOD file is absent.
The resolver reads the palettes of those authored LODs once per character model.

The bounds routine adds `0.2` meters per side when an axis is smaller than `0.4` meters.
An invalid box or coordinate outside `-13000` through `13000` uses the native `[-2, 2]` fallback.
The CAF sampler returns these current model bounds and clears its resolved pose requirements.
The focused character tests passed: 21 passed, with 2 optional game_pak checks skipped.

## Mixed CAF controller versions

Native `3162f080` selects compressed controllers when a CAF file contains any version `0x829` through `0x831`.
Native `3162f6e0` then skips old versions `0x827` and `0x828` for that whole clip.
This rule also applies when an old controller has no compressed controller with the same ID.
The two pirate protection prefabs and the Ferre flag contain mixed CAF files.
The reader uses their compressed tracks and keeps bind transforms for absent selected tracks.

## Animation model format and preview bounds

Native `315d6820` accepts only `.cga`, `.chr`, and `.cdf` character files.
It returns no character for another extension, even when that static file exists.
The Ezna iron gate uses a `cga://` URI with a `.cgf` file and thus has no client character geometry.
The resolver uses the same format gate for animation URIs and prefab `AnimObject` entities.

CGA preview bounds use their bind rigid mesh bounds with the native narrow-axis padding.
A static brush with a CGA file does not start a default animation.
`LoadPose` samples a direct model only when its URI starts animation.
An active prefab child uses its explicit animation properties.

## Complete authored model check

The direct game_pak fixture checked all nonempty model URIs for owned housing and decorations.
It also checked every distinct main-world doodad model URI.
Each check loaded the preview and sampled times `0`, `0.5`, and `2` seconds.

| Source | Distinct nonempty URIs | Pose samples | Parse errors | Unresolved collision poses |
| --- | ---: | ---: | ---: | ---: |
| Owned house, build, decoration, and phase models | 953 | 2859 | 0 | 0 |
| Main-world doodad models | 1666 | 4998 | 0 | 0 |

All 460 house build cases have model bounds.
The 385 house main cases include 11 obsolete `siegefield.xml` elements that are absent from the client.
These belong to designs `4` through `9`, `25`, `28`, `30`, `93`, and `94`.
The castle gate and tower designs `268` and `269` load their authored geometry.

The 577 decoration base cases include 4 empty model strings: designs `383`, `386`, `389`, and `392`.
Wraith designs `257` and `258` reference absent prefab elements.
These 6 designs have no native preview bounds and cannot pass the placement geometry check.
Native empty model results are distinct from a parser failure or an unresolved collision pose.

The read set includes 83 CGA files and 74 authored scale tracks.
Those scale tracks contain constant values.
The focused final checks passed 7 CAF tests, 9 CGA tests, and 18 resolver tests.

## Doodad phase animation coverage

`LoadAnimationPose(modelUri, animationName, elapsedSeconds, loop)` applies the selected phase clip.
It handles direct CGA or CHR models and character children inside a prefab.
Native `393a9b00` calls `39108de0` for a doodad phase animation.
That function visits each animation child, including a child whose authored `bPlaying` is zero.
The resolver keeps the child transform and material after the phase clip changes its pose.

Native `3155b350` returns false before a queue change when the requested animation ID is absent.
A missing phase name thus keeps the model's current default pose in the server clock policy.
The resolver does not replace it with an unknown pose or empty collision.

The phase check covers 317 distinct model and animation pairs from 735 applicable compact rows.
All 951 phase samples passed, with no parser errors or unresolved collision poses.
The check found 3 legacy-only CAF clips for quest wings, sewing, and printing.
The reader supports their controller versions `0x827` and `0x828`.
Version `0x827` omits the normal chunk header. Version `0x828` includes that header.
Native `3162f6e0` converts position centimeters to meters and divides integer key ticks by `160`.
Native `3162ac20` decodes conjugate log quaternions and uses normalized shortest-path interpolation through `3162a840`.

The focused checks passed 9 CAF tests and 2 game_pak phase tests.
Those phase tests cover a named CGA door clip, a missing name, and a stopped prefab character with a world transform.
The resolver retains cached geometry for static prefab models without active animation.
