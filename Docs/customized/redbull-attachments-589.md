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

The attachment map does not supply the missing body, hood, material, or animation resources.
The Game asset archive and client still need the reviewed Red Bull content.
File and transform checks do not prove driver fit, hood clearance, or cargo interaction through the real client.
