using System.Text;

using AAEmu.Game.Models.CryEngine.Objects;
using AAEmu.Game.Models.CryEngine.Physics;

namespace AAEmu.UnitTests.Game.Models.CryEngine;

public sealed class CryVoxelGeometryTests
{
    [Test]
    public async Task SurfaceNames_KeepTheAuthoredIndexAndStopAtNull()
    {
        var data = new byte[2048];
        Encoding.UTF8.GetBytes("rock\0unused").CopyTo(data, 0);
        Encoding.UTF8.GetBytes("sand").CopyTo(data, 31 * 64);
        var names = CryVoxelGeometry.ReadSurfaceNames(data);
        await Assert.That(names.Count).IsEqualTo(32);
        await Assert.That(names[0]).IsEqualTo("rock");
        await Assert.That(names[1]).IsEqualTo("");
        await Assert.That(names[31]).IsEqualTo("sand");
    }

    [Test]
    public async Task MissingCompiledGeometry_FailsInsteadOfUsingTheBoundingBox()
    {
        await Assert.That(() => CryVoxelGeometry.Read(new ObjectDataType6Voxel(), "missing"))
            .Throws<InvalidDataException>();
    }
}
