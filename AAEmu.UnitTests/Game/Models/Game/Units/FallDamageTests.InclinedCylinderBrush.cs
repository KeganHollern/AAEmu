using System.Numerics;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

public sealed partial class FallDamageTests
{
    [Test]
    [Arguments(13524.879f, 15085.209f, 221.91028f, 13526.734f, 222.44415f, 222.44472f)] // Loose siege wheel, 16.45 degrees.
    [Arguments(10928.752f, 12422.535f, 140.9847f, 10929.424f, 141.16472f, 141.16525f)] // Stone pedestal, 15 degrees.
    public async Task StockInclinedDisk_PlayerLandsOnceInsideTheMeasuredRim(
        float x, float y, float z, float rimX, float rimZ, float outsideZ)
    {
        var world = CreateStockWorld();
        world.Template.GetCell((int)x / 1024, (int)y / 1024).VerifyCellLoaded();
        var ground = world.Template.GeoData;
        var foot = new Vector3(x, y, z);
        var rim = new Vector3(rimX, y, rimZ);
        await Assert.That(ground.HasBrushFallSupport(rim, true)).IsTrue();
        await Assert.That(ground.HasBrushFallSupport(new(rimX + 1f / 512f, y, outsideZ), true)).IsFalse();
        await Assert.That(ground.HasBrushFallSupport(rim - Vector3.UnitZ * 0.05f, true)).IsFalse();
        await Assert.That(ground.HasBrushFallSupport(foot - Vector3.UnitZ * 0.01f, true)).IsFalse();
        await Assert.That(ground.HasBrushFallSupport(foot, false)).IsFalse();

        var player = new InclinePlayer { ParentWorld = world, Hp = 9516 };
        player.Transform.Local.SetPosition(foot + Vector3.UnitZ * 1.3f);
        player.ObserveFallMovement(false, 0);
        player.Transform.Local.SetPosition(foot + Vector3.UnitZ * 0.975f);
        player.ObserveFallMovement(false, 0.16);
        await Assert.That(player.Impacts).IsEmpty();
        player.Transform.Local.SetPosition(foot);
        player.ObserveFallMovement(false, 0.32);
        await Assert.That(player.Impacts).IsEquivalentTo(new ushort[] { 3120 });
        player.ObserveFallMovement(true, 0.42);
        await Assert.That(player.Impacts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task StockInclinedDisk_RoundingUsesOnlyPositionsInsideTheWheel()
    {
        var world = CreateStockWorld();
        world.Template.GetCell(13, 14).VerifyCellLoaded();
        var ground = world.Template.GeoData;
        // The first decoded foot is 2.32 mm above the plane at the reported XY. The
        // hidden XY can explain the difference and still remain on the wheel.
        await Assert.That(ground.HasBrushFallSupport(new(13526.141f, 15086.4375f, 222.3573f), true)).IsTrue();
        // At this rim, the correction for a 2.30 mm gap would lie outside the disk.
        await Assert.That(ground.HasBrushFallSupport(new(13526.588f, 15084.426f, 222.3523f), true)).IsFalse();
    }

    [Test]
    public async Task StockInclinedDisk_RejectsUndersidesAndASteepWheel()
    {
        var world = CreateStockWorld();
        foreach (var foot in new Vector3[]
        {
            new(13525.112f, 15085.265f, 221.10141f), // Underside of the 16.45 degree wheel.
            new(10929.059f, 12422.911f, 139.83998f), // Underside of the pedestal.
            new(30035.223f, 7128.0547f, 399.89502f) // Same wheel model, exposed face at 54.99 degrees.
        })
        {
            world.Template.GetCell((int)foot.X / 1024, (int)foot.Y / 1024).VerifyCellLoaded();
            await Assert.That(world.Template.GeoData.HasBrushFallSupport(foot, true)).IsFalse();
        }
    }
}
