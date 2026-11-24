using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Models.Game.DoodadObj;

namespace AAEmu.UnitTests.Game.Models.Game.DoodadObj;

public sealed class LilyutIronVeinPlacementTests
{
    [Test]
    public async Task NaturalVeinAtLilyutCliff_RestsOnItsRockFace()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "Worlds", "main_world", "doodad_spawns.json");
        var spawns = JsonHelper.DeserializeObject<List<DoodadSpawner>>(await File.ReadAllTextAsync(path));
        var vein = spawns.Single(s => s.UnitId == 1671 && s.Position.X == 12404.4f && s.Position.Y == 15598.492f);

        // World vertices of face 1356, cliff_rock_cover_c_big.cgf, in cell 012_015/brush.dat.
        // The normal mining phase puts its static rubble at the spawn origin.
        var a = new Vector3(12405.14149f, 15594.56225f, 149.43820f);
        var b = new Vector3(12406.23509f, 15600.08387f, 149.08991f);
        var c = new Vector3(12401.85296f, 15600.32873f, 150.05256f);
        var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        var distanceToFace = Vector3.Dot(vein.Position.AsPositionVector() - a, normal);
        await Assert.That(distanceToFace).IsEqualTo(0f).Within(0.001f);
    }
}
