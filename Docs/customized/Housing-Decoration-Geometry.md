# Decoration geometry for r208022

The pure predicates in `HousingDecorationGeometry` follow the exact client
`3903c2e0`, `3903c5a0`, and `3903da00`. The source and dump hashes match the
inputs in [doodad-function-permissions.md](doodad-function-permissions.md).
The local C exports are in `.tools-re/housing-20260912/geometry` and
`.tools-re/housing-20260912/placement` in the cluster workspace.

## House selection range

Item activation `394eaac0`, case 6, calls `39328630` with the player position
and `3999cffc = 2.0`. That function first accepts a player inside the adjusted
garden volume, including its boundaries. Otherwise, `39327830` computes the
squared 3D distance from the player to the closest point on that volume.
A result at most `4` permits selection. The selector chooses the nearest
eligible volume when the player is outside all volumes.

`IsWithinSelectionRange` exposes this exact per-house predicate. It is a 2 metre
margin around the garden volume. It is not a range from the house origin or
from the decoration pivot. The runtime adapter checks the requested house
against the player's current position before it changes inventory.

Preview creation `3932c120` and preview update `3932a3b0` do not use the building
preview's `39333d40` distance rule. Decoration support ray `3903da00` has a
50 metre length from the camera. That value does not define player reach.
No packet provides a trusted camera pose or a persistent server preview state.

## Support and coordinates

The packet position and rotation describe the decoration in house coordinates.
The support object identifier has a separate purpose. The world adapter must
check that identifier against the support hit. It must not use a support doodad
as the parent coordinate system for the decoration.

`3903da00` reads the hit normal and uses these exact support rules:

| Surface | Normal Z | Required design flag |
| --- | --- | --- |
| Floor | Greater than `0.5` | Floor, pivot on garden, or mesh on garden |
| Ceiling | Less than `-0.5` | Ceiling |
| Wall | From `-0.5` through `0.5`, inclusive | Wall |

The adapter resolves the support object and its native eligibility. The pure
validator accepts its result through `supportAllowed` and `supportNormal`.

## Interior and garden

`3903c2e0` builds an oriented decoration box from its model bounds and placement
rotation. `3903c5a0` transforms all 8 corners into world coordinates.

A corner is inside the house when it lies inside the house model's oriented
box. The box includes its boundary. Each inside corner also gets a vertical
ray from the top of the garden volume. The ray ends 10 metres below its bottom.
The first terrain hit makes the candidate exterior. A building hit or no hit
does not make it exterior.

`ray_hit + 0x34` is `bTerrain`. Native `39428ba0` confirms this field. It is not
the hit normal Z component. The ray uses entity mask `0x107` and flags `0x8f`.

All corners must pass these checks for the decoration to count as interior.
Interior placement needs a floor, wall, or ceiling flag. Exterior placement
needs a pivot-on-garden, mesh-on-garden, or wall flag.

A house with a garden always checks the decoration pivot against its garden
volume. Mesh-on-garden designs also check every original model corner. These
garden bounds include each minimum and exclude each maximum. The adapter builds
the garden volume from the native grid footprint, house model height bounds,
and the house height adjustments. These are separate from the house model box.

## Collision box

The collision query uses the original model bounds with these changes:

1. If local minimum Z is negative, clip that minimum to `0`.
2. Move the box center by `0.025` along the decoration's local Z axis.
3. Reduce its half-height by `0.025`.
4. Query overlap only when the resulting half-height is positive.

The top remains fixed. The bottom rises by `0.05` after the optional clip.
The adjustment uses the decoration axis, including for wall and ceiling objects.
The interior and garden checks use the original corners before this adjustment.
The native overlap query uses entity mask `0x1f` and geometry collision mask `3`.

| Address | Float value | Purpose |
| --- | --- | --- |
| `3999c47c` | `0.5` | Surface threshold and bound midpoint |
| `3999dda8` | `-0.5` | Ceiling threshold and clipped center change |
| `3999dda4` | `0.025` | Collision center and half-height adjustment |
| `3999cce8` | `10.0` | Extra depth for the interior ray |

The domain returns the same native errors for surface, exterior, interior,
garden, and overlap failures. The world adapter can return `Indeterminate`
when data cannot support a query. The server denies that placement before any
item or doodad changes.

## API and checks

`HousingDecorationGeometry.Check` accepts model bounds, the decoration pose,
the house box, the garden volume, support information, and 2 query delegates.
`HousingDecorationRayResult` separates the hit state from the terrain flag.
The overlap delegate accepts a `CryBox` and returns `CryIntersection`.

The Release build and all 46 focused unit tests passed.
Tests cover both exact surface boundaries, rotated boxes, all 8 interior rays,
terrain classification, garden boundaries, mesh and pivot flags, uncertain
queries, overlap rejection, below-pivot clipping, wall orientation, and thin
objects that have no positive collision height.
