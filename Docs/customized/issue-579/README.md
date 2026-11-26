# Stock doodad bounds for floor contact

The Game uses these bounds to find stationary doodads for an exact floor check. Each key names a CGF in the client archive. Each row gives its SHA-256 and 6 local coordinates in millimetres. The coordinates use the order min X, min Y, min Z, max X, max Y, max Z. The generator rounds each minimum down and each maximum up.

## Reproduce the table

Use .NET 10, the signed sequence 28 client archive, the server compact, and the stock Game placement files. Set `GAME_PAK` and `SERVER_COMPACT` to the paths of the 2 local files. Run these commands from the Game repository root. The generator checks the hashes of all inputs before it reads geometry.

```sh
output_dir=$(mktemp -d)
dotnet run --project Docs/customized/issue-579/DoodadFloorBounds.csproj -c Release -- \
    "$GAME_PAK" "$SERVER_COMPACT" AAEmu.Game/Data/Worlds "$output_dir/bounds.json"
cmp "$output_dir/bounds.json" AAEmu.Game/Models/Game/World/Data/r208022-doodad-floor-bounds.json
```

The expected output has 141 rows and 22,175 bytes. Its SHA-256 is `e1af11f02bcd762a412a1278fcadb4fb8d207402bdaf972fea0ba2eb06719a62`.

| Input | Identity |
| --- | --- |
| Client archive | Signed release `nui.r20261126`, sequence 28, 24,960,731,648 bytes. SHA-256 `149962986700dca59573028097499b9b43d87495afe44c6c2fb4498cc6bfd25b`. |
| Server compact | SHA-256 `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`. |
| Game source | `0742fe1d952fd1c2a7fc9bc1534f14e3235d7902`. The Git tree for `AAEmu.Game/Data/Worlds` is `d26cf7031836bd16bc474755c854305eb79bc45b`. |
| Placement inputs | 18 files match `*/doodad_spawns*.json`. Their manifest SHA-256 is `6c861a8e4e0f0dc66ee4944d38261e98a2e20977995fa7f935376f5bf5b31516`. |

The placement manifest contains the SHA-256 of each file, 2 spaces, its relative path with forward slashes, and a newline. It uses UTF-8 and ordinal path order. The signed manifest identifies the final archive separately from its download representation. [Signed sequence 28 manifest](https://github.com/KeganHollern/aaemu-cluster/blob/f499fcd3697a35ba3f127fed089fb7ff0deda393/client-patches/releases/28/manifest.json)

## Selection and geometry

The 18 worlds contain 46,202 placement rows. The generator applies the Game's same-template position check within each world. It skips followers, growth templates, non-default model kinds, world-supplied models, and models without collision. It uses each start or normal phase with no phase functions or only timer functions. A phase model takes priority over the template model.

This gives 492 distinct CGF paths across 16,500 eligible placements. The archive contains 349 static models with default physical boxes or triangle meshes. Another 93 models have animated collision or need a pose. Another 48 models have no default physical box or mesh. The archive lacks 2 paths.

The generator uses `HousingGeometryAssets` and `CryGeometryQueries.GetBounds` to combine the transformed physical parts of each model. It then finds the corner farthest from the model origin. A model qualifies if that corner exceeds 2 m at any eligible placement's uniform scale. Rotation preserves that distance. The independent survey also checked all 8 corners at each authored rotation. Both methods selected the same 141 paths.

The table stores the local bounds at unit scale. A query must use the current model, scale, and transform before it tests the actual floor. In the original survey, 141 paths crossed the corner threshold across 3,040 placements. Only 75 paths had a level face that extended more than 2 m from the origin.
