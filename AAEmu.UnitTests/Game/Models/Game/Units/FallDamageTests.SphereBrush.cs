using System.Numerics;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

public sealed partial class FallDamageTests
{
    [Test]
    [Arguments(9143.576f, 582.8638f, -1f)]
    [Arguments(9143.977f, 582.86523f, 1f)]
    public async Task FalconryBall_PlayerLandsOnEitherSideOfTheCrown(float y, float z, float approach)
    {
        var world = CreateStockWorld();
        world.Template.GetCell(23, 8).VerifyCellLoaded();
        var player = new InclinePlayer { ParentWorld = world, Hp = 9516 };
        var foot = new Vector3(23725.125f, y, z);
        var ground = world.Template.GeoData;
        player.Transform.Local.SetPosition(foot + new Vector3(0, 0.4f * approach, 1.3f));
        player.ObserveFallMovement(false, 0);
        player.Transform.Local.SetPosition(foot + new Vector3(0, 0.2f * approach, 0.975f));
        player.ObserveFallMovement(false, 0.16);
        await Assert.That(player.Impacts).IsEmpty();
        player.Transform.Local.SetPosition(foot);
        player.ObserveFallMovement(false, 0.32);
        await Assert.That(player.Impacts).IsEquivalentTo(new ushort[] { 3120 });
        player.ObserveFallMovement(true, 0.42);
        await Assert.That(player.Impacts.Count).IsEqualTo(1);
        await Assert.That(ground.HasBrushFallSupport(foot, false)).IsFalse();

        // The other side of the 50 degree boundary and the underside still leave the player airborne.
        var steep = new Vector3(foot.X, 9144.018f, 582.8227f);
        await Assert.That(ground.HasBrushFallSupport(steep, true)).IsFalse();
        await Assert.That(ground.HasBrushFallSupport(new(foot.X, 9143.777f, 582.32495f), true)).IsFalse();
        await Assert.That(ground.HasBrushFallSupport(foot - Vector3.UnitZ * 0.01f, true)).IsFalse();
        player.Transform.Local.SetPosition(steep);
        player.ObserveFallMovement(false, 0.52);
        await Assert.That(player.Impacts.Count).IsEqualTo(1);

        // These two adjacent stock placements already catch landings on their own surfaces.
        await Assert.That(ground.HasBrushFallSupport(new(23724.375f, 9143.527f, 582.6475f), true)).IsTrue(); // mesh 382
        await Assert.That(ground.HasBrushFallSupport(new(23724.625f, 9144.027f, 582.74115f), true)).IsTrue(); // box 381

        var lowerFloor = new Vector3(23724.168f, 9141.544f, 582.09827f);
        player.Transform.Local.SetPosition(lowerFloor);
        player.ObserveFallMovement(false, 0.82);
        await Assert.That(player.Impacts.Count).IsEqualTo(2);
    }
}
