# House construction pose rules

This record supports cluster issue #307. The source uses exact r208022 native
code from the X2Game image identified in
[the decoration and farm record](Housing-Limits-Public-Farms-497-500.md).
Native function extracts are under the cluster workspace path
`.tools-re/housing-20260912/limits-farms/`.

`HousingConstructionGeometry.Resolve` calculates a server pose from authored
model bounds and terrain samples. It has no file, world, inventory, or packet
access. The caller applies area rules, collision rules, and ownership rules
separately before it accepts the pose.

## Confirmed native rules

| Rule | Native evidence |
| --- | --- |
| The player range uses truncated 3D distance, with a maximum of `garden_radius + 30`. Error 154 reports excess range. | `39333d40`, constant `399cf898=30.0` |
| A positive garden radius selects plot geometry. The plot width is `int((2 * garden_radius + 3) / 4)` cells. Width 12 or more is unsupported. | `39325190`, `39897170` |
| Plot centers use `trunc((coordinate - halfWidth + 2) / 4) * 4 + halfWidth`. Each cell is 4 meters wide. | `39325e30`, `39332100` |
| Normal house yaw stays continuous. The pose update selects the next branch from `housings.auto_z`. | `3933a300`, `3933a380`, `3908c850` |
| Without autoZ, the client samples the 4 bottom corners of the model OBB and their center. Terrain spread above 6 meters sets error 116. Otherwise, the origin uses the minimum sampled height. | `39332180`, `39326c10`, constant `3999ecf4=6.0` |
| With autoZ, the client samples 4 corners, 4 edge centers, and terrain grid points inside the rotated model footprint. The origin uses the maximum sampled height. The minimum terrain height must reach the transformed model bottom. | `39337fb0` |
| Category 5 uses a 20-meter XY grid, 3-meter height steps, and 90-degree yaw steps. | `393323c0`, `39337320`, `393373a0` |

The model bounds are local bounds before the house yaw and translation.
The terrain callback returns decoded client terrain heights. The caller also
supplies the authored terrain grid size. Missing terrain or nonfinite geometry
rejects the pose. This prevents an absent height sample from becoming ground
at Z=0.

The final range check uses the calculated pose. Native `393363b0` runs the
range check after the pose update. The server does not use a claimed client
height as the construction height.

## Category 5 and connectors

The native object field at offset `0x0c` is `housings.category_id`.
Constructor `3908c790` copies it from housing descriptor offset `0x1c`.
Loader `39677360` confirms that descriptor field. It is not `BaseUnitType`.
The active compact has one category 5 row: housing 30, `stronghold`.

The stronghold grid phase comes from the native world origin:
`(originComponent * 4) % 20 + 10`. The pure helper needs that phase as an
explicit argument. It rejects category 5 when the phase is absent.
The normal house path does not apply this stronghold snap.

Native `39334540` checks transformed connection points only when the loaded
connector list has entries. Each point must be between terrain +7.6 and
terrain +16.4, inclusive. Otherwise, it sets error 380.
These points come from the exact prefab connector metadata.
Loader `390904b0` reads the `connector` and `connector_stair` names and connector
text such as `(height:12)`.

Connector construction, neighbor matching, and Dominion rules remain separate
from the pure pose helper. They need authored connector metadata and nearby
structures. Issue #143 keeps the full siege work separate from this release.

## Validation

The 10 focused tests passed. They cover the range boundary, vertical distance,
plot grid phase, a false client height, terrain spread, rotated bounds,
an interior terrain peak, edge samples, stronghold steps, and absent terrain.
The Release build passed with 0 errors.
The tests use independent terrain cases that make the native rules observable.
The parent housing release record covers scene integration and deployment.
No manual r208022 client test is claimed here.

## Plot area, water, and neighboring plots

The native validator `393363b0` calls the area, water, and collision predicates.
`393312a0` splits a plot into 4 m cells. It applies the alley only on outer edges.
`39331ba0` finds an area from each cell's first corner. It checks the other 3 corners against that same area.
All 4 points use Z=0 because the native housing area predicate ignores height.

`398970b0` selects categories 7 and 15 for underwater construction.
`39331d20` gets the terrain vertex under each cell corner through `I3DEngine+0x204`.
It compares that height with the water surface from `I3DEngine+0x110`.
Water must be strictly above the terrain for an underwater plot.
Equality counts as land. A house with no garden uses its pivot for this test in `39334380`.

`393349e0` selects plot or model intersection from the garden radius.
Two plots use strict XY overlap in `39331f30`, without a Z test.
The candidate reserves its alley. The neighboring plot uses its full footprint.
A plot and a house without a garden use the full separating-axis box test from `39332c10`.
Two houses without gardens use the oriented-box test from `390318f0`.
These box tests count touching faces as overlap.
