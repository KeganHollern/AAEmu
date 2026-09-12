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
The resolver retains missing-file failures for recognized asset paths.

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
Skeletal CHR geometry still needs its compiled bone proxies and active bone transforms.
Native `3910b080` requests the `Default` animation for a direct CGA model.
The `cga_loop` flag controls looping, not whether the animation starts.
Native `3903c2e0` reads model bounds each time it builds the placement box.
An initial render box alone does not prove the bounds of an active model.

The local asset fixture parsed 612 distinct housing CGF, CGA, and CHR containers without format errors.
This count covers container parsing, not unresolved skeletal collision or active poses.
The retained tests cover binary formats, transforms, intersection queries, and animation ancestry.
