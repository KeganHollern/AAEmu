using System.Numerics;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

public sealed partial class FallDamageTests
{
    [Test]
    [Arguments(10643.375f, 14771.43f, 300.64102f, 10646.75f, 300.2123f)] // Ruins base, local axis points down.
    [Arguments(20748.084f, 13123.707f, 116.75313f, 20749.048828125f, 115.87909f)] // Round table, local axis points up.
    public async Task StockCylinder_PlayerLandsOnTheTopAndStopsAtTheCircularRim(
        float x, float y, float top, float lastInsideX, float underside)
    {
        var world = CreateStockWorld();
        world.Template.GetCell((int)x / 1024, (int)y / 1024).VerifyCellLoaded();
        var ground = world.Template.GeoData;
        var foot = new Vector3(x, y, top);
        // The next representable position on the client's 1/512 m grid lies outside the disk.
        await Assert.That(ground.HasBrushFallSupport(new(lastInsideX, y, top), true)).IsTrue();
        await Assert.That(ground.HasBrushFallSupport(new(lastInsideX + 1f / 512, y, top), true)).IsFalse();
        await Assert.That(ground.HasBrushFallSupport(new(lastInsideX, y + 0.25f, top), true)).IsFalse();
        await Assert.That(ground.HasBrushFallSupport(new(lastInsideX, y, (top + underside) / 2), true)).IsFalse();
        await Assert.That(ground.HasBrushFallSupport(new(x, y, underside), true)).IsFalse();
        await Assert.That(ground.HasBrushFallSupport(foot - Vector3.UnitZ * 0.01f, true)).IsFalse();
        await Assert.That(ground.HasBrushFallSupport(foot + Vector3.UnitZ * 0.01f, true)).IsFalse();

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
}
