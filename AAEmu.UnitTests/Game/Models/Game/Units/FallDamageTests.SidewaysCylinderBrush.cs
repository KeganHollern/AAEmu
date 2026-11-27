using System.Numerics;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

public sealed partial class FallDamageTests
{
    [Test]
    [Arguments(11258.834f, 12032.668f, 146.2072f)]
    [Arguments(11258.375f, 12033.127f, 146.20682f)]
    public async Task SidewaysOakBarrel_PlayerLandsOnTheCurvedSide(float x, float y, float z)
    {
        // Release brush 5254 in main-world cell (10, 11) lies on its side beside dry ground.
        var world = CreateStockWorld();
        world.Template.GetCell(10, 11).VerifyCellLoaded();
        var ground = world.Template.GeoData;
        var player = new InclinePlayer { ParentWorld = world, Hp = 9516 };
        var foot = new Vector3(x, y, z);
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

        await Assert.That(ground.HasBrushFallSupport(foot, false)).IsFalse();
        await Assert.That(ground.HasBrushFallSupport(foot - Vector3.UnitZ * 0.02f, true)).IsFalse();
        // The stock barrel extends past the walkable slope on both sides.
        await Assert.That(ground.HasBrushFallSupport(new(11258.889f, 12032.615f, 146.11562f), true)).IsFalse();
        await Assert.That(ground.HasBrushFallSupport(new(11258.322f, 12033.182f, 146.11497f), true)).IsFalse();

        var lowerGround = new Vector3(11259.991f, 12032.324f, 145.31355f);
        player.Transform.Local.SetPosition(lowerGround + Vector3.UnitZ * 0.6f);
        player.ObserveFallMovement(false, 0.59);
        player.Transform.Local.SetPosition(lowerGround);
        player.ObserveFallMovement(false, 0.82);
        await Assert.That(player.Impacts.Count).IsEqualTo(2);
    }
}
