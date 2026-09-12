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
