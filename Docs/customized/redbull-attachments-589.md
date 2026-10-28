# Red Bull attachment positions

Cluster issue [#589](https://github.com/KeganHollern/aaemu-cluster/issues/589) uses the original Speedcar rigs for the custom Red Bull body.
`Data/slave_attach_points_redbull.json` adds positions for model `1427` only.
It does not add passenger entry, change another vehicle, or change the compact database.

## Source

The positions and rotations come from the preserved native `speedcar_body.chr` bind transforms.
The reference is 963,770 bytes with SHA-256 `88269f91bebd8e72781a65045726ec22729d69ac091cd9afe600063e388d8f00`.
The approved custom body retains those transforms.

| Attachment | Native helper | Purpose |
| --- | --- | --- |
| `1` | `driver` | Driver position and entry distance |
| `21` | `sail0` | Hood doodad `6967` |
| `60` | `box0` | First trade-pack storage doodad `3446` |
| `61` | `box1` | Second trade-pack storage doodad `3446` |

The JSON uses metres and degrees.
`SlaveGameData.LoadSlaveAttachmentPointLocations()` converts the rotations to radians after reading the file.
Cargo helpers have nonzero roll, pitch, and yaw. Their rotations must not default to zero.
The native `passenger0` helper does not establish a supported passenger skill, so it is not part of this map.

## Checks

`RedBullAttachmentTests` checks the shipped map through the production loader.
It checks the supported point set, preservation of another vehicle, degree conversion, and a second load.
With `AAEMU_HOUSING_GAME_PAK` set, it compares all 4 loaded transforms with the native character bones.
That comparison includes child transforms under a translated, pitched, rolled, and turned parent.

`RedBullGeometryAssetsTests` uses the separate `AAEMU_REDBULL_GAME_PAK` setting for a candidate archive.
It checks the normal body and hood prefabs, their preserved skeleton bounds, collision parts, and independent material path.
It also checks steering, trunk, and hood clips through their renamed CAL files and the prefab phase path.
The separate setting keeps pristine-archive checks independent from the candidate.
The Game reader derives character bounds from the retained compiled-bone chunk (`0xacdc0000`).
Those bounds describe the skeleton envelope, not the new 3.05 m render width.
The test reports triangle collision extents separately. It does not claim that skeleton bounds contain every surface.

For the normal-only rough candidate, the reader returns these local extents in metres:

| Part | Skeleton minimum | Skeleton maximum | Collision minimum | Collision maximum |
| --- | --- | --- | --- | --- |
| Body | `(-1.305720, -2.627823, 0)` | `(1.305723, 2.913010, 1.181040)` | `(-1.01, -2.68, 0.34)` | `(1.01, 2.755, 1.285)` |
| Hood | `(-0.213737, -0.2, -0.2)` | `(0.213740, 0.2, 0.2)` | `(-0.88, -0.69, -0.12)` | `(0.88, 0.69, 0.105)` |

Both collision meshes extend outside their native skeleton bounds.
The body extends beyond the bounds at the rear and top. The hood extends beyond them across its width and length.
The pristine Speedcar body and hood also have collision vertices outside these same skeleton bounds.
The bounds equal those from the retained Speedcar rigs. They supply the native summon-placement box.
Placed-object queries use `HousingGeometryAssets.CollisionBounds`, which unions the skeleton bounds with every physical part.
The test confirms that this union contains every triangle vertex and that a query outside the skeleton still finds the vehicle.
This check does not change the Game reader's bounds policy or prove complete vehicle coverage in every state.

The attachment map does not supply the missing body, hood, material, or animation resources.
The Game asset archive and client still need the reviewed Red Bull content.
File and transform checks do not prove driver fit, hood clearance, or cargo interaction through the real client.
