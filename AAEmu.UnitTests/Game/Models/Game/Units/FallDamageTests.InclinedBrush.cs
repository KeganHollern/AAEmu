using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

public sealed partial class FallDamageTests
{
    [Test]
    [Arguments(22133.559f, 8469.82f, 480.49075f, 22137.559f)] // Tigerspine bridge above the rail bed.
    [Arguments(10655.814f, 14765.314f, 300.90854f, 10656.814f)] // Wooden crate beside the bank.
    public async Task StockLevelBox_PlayerLandsOnceOnTheTopFace(float x, float y, float top, float outsideX)
    {
        var world = CreateStockWorld();
        world.Template.GetCell((int)x / 1024, (int)y / 1024).VerifyCellLoaded();
        var ground = world.Template.GeoData;
        var foot = new Vector3(x, y, top);
        await Assert.That(ground.HasBrushFallSupport(foot + Vector3.UnitZ * 0.01f, true)).IsFalse();
        await Assert.That(ground.HasBrushFallSupport(foot - Vector3.UnitZ * 0.01f, true)).IsFalse();
        await Assert.That(ground.HasBrushFallSupport(new(outsideX, y, top), true)).IsFalse();

        var actor = new InclinePlayer { ParentWorld = world, Hp = 9516 };
        actor.Transform.Local.SetPosition(foot + Vector3.UnitZ * 1.3f);
        actor.ObserveFallMovement(false, 0);
        actor.Transform.Local.SetPosition(foot + Vector3.UnitZ * 0.975f);
        actor.ObserveFallMovement(false, 0.16);
        await Assert.That(actor.Impacts).IsEmpty();
        actor.Transform.Local.SetPosition(foot);
        actor.ObserveFallMovement(false, 0.32);
        await Assert.That(actor.Impacts).IsEquivalentTo(new ushort[] { 3120 });
        actor.ObserveFallMovement(true, 0.42);
        await Assert.That(actor.Impacts.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(22245.4f, 8624.9f, 565.23505f)] // Wooden bridge, rotated solid box.
    [Arguments(22260.2f, 8655.7f, 600.51587f)] // Rail mesh, upward triangle 4.
    public async Task StockTigerspineIncline_ConfirmsThePlayersJumpBeforeTheLandingNotice(float x, float y, float floor)
    {
        var world = CreateStockWorld();
        var actor = new InclinePlayer { ParentWorld = world, Hp = 9516 };
        void Move(float offset, double time, bool notice = false)
        {
            actor.Transform.Local.SetPosition(x, y, floor + offset);
            actor.ObserveFallMovement(notice, time);
        }

        Move(0, 0);
        Move(0.975f, 0.16);
        Move(1.3f, 0.32);
        Move(0.975f, 0.48);
        await Assert.That(actor.Impacts).IsEmpty();
        Move(0, 0.64);
        await Assert.That(actor.Impacts).IsEquivalentTo(new ushort[] { 3120 });
        Move(0, 0.74, true);
        Move(0, 60);
        await Assert.That(actor.Impacts.Count).IsEqualTo(1);

        var other = new FallUnit { ParentWorld = world, Hp = 9516, MaxHp = 9516 };
        other.Transform.Local.SetPosition(x, y, floor + 1);
        other.ObserveFallMovement(false, 0);
        other.Transform.Local.SetPosition(x, y, floor);
        other.ObserveFallMovement(false, 0.4);
        await Assert.That(other.Impacts).IsEmpty();
        other.ObserveFallMovement(true, 0.5);
        await Assert.That(other.Impacts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task StockTigerspineBrush_RejectsBothBottomsAndTheSpaceBeyondEachEdge()
    {
        var world = CreateStockWorld();
        world.Template.GetCell(21, 8).VerifyCellLoaded();
        var ground = world.Template.GeoData;
        foreach (var foot in new Vector3[]
        {
            new(22245.4f, 8624.9f, 564.2401f), // Bridge underside.
            new(22260.2f, 8655.7f, 598.8016f), // Rail underside, triangle 18.
            new(22244.121f, 8625.797f, 565.2344f), // Outside the bridge.
            new(22259.744f, 8656.031f, 600.5153f) // Outside the rail.
        })
            await Assert.That(ground.HasBrushFallSupport(foot, true)).IsFalse();
    }

    [Test]
    [Arguments(3382.5798f, 4080.9233f, 104.6072f)]   // Rock face 30, 3.03 mm above its height at decoded XY.
    [Arguments(3388.4932f, 4079.1853f, 108.935104f)] // Face 143, close to an edge of the triangle.
    public async Task StockMirageIncline_UsesOnlyTheHeightChangeWithinTheEncodedHorizontalPosition(float x, float y, float z)
    {
        var world = CreateStockWorld("arche_mall_world");
        world.Template.GetCell(3, 3).VerifyCellLoaded();
        // Use the signed rock_mossy_a04.cgf and the ordinary coordinate conversion.
        var position = Helpers.ConvertPosition(Helpers.ConvertPosition(x, y, z));
        var foot = new Vector3(position.x, position.y, position.z);
        var ground = world.Template.GeoData;
        await Assert.That(ground.HasBrushFallSupport(foot, true)).IsTrue();
        await Assert.That(ground.HasBrushFallSupport(foot + Vector3.UnitZ * 0.004f, true)).IsFalse();
        await Assert.That(ground.HasBrushFallSupport(foot - Vector3.UnitZ * 0.007f, true)).IsFalse();
    }

    [Test]
    public async Task StockTigerspineRail_DownhillStepsStayGroundedAndTheNextJumpHasOneImpact()
    {
        var world = CreateStockWorld();
        var actor = new InclinePlayer { ParentWorld = world, Hp = 9516 };
        // Half-metre steps along the downhill direction of triangle 4.
        Vector3[] walk =
        [
            new(22260.2f, 8655.7f, 600.51587f), new(22259.906f, 8655.296f, 599.9803f),
            new(22259.611f, 8654.892f, 599.4435f), new(22259.318f, 8654.486f, 598.90704f),
            new(22259.023f, 8654.082f, 598.37024f)
        ];
        for (var i = 0; i < walk.Length; i++)
        {
            actor.Transform.Local.SetPosition(walk[i]);
            actor.ObserveFallMovement(false, i * 0.1);
        }
        await Assert.That(actor.Impacts).IsEmpty();
        actor.Transform.Local.SetPosition(walk[^1] + Vector3.UnitZ * 1.3f);
        actor.ObserveFallMovement(false, 0.72);
        actor.Transform.Local.SetPosition(walk[^1]);
        actor.ObserveFallMovement(false, 1.04);
        await Assert.That(actor.Impacts.Count).IsEqualTo(1);
    }

    private sealed class InclinePlayer() : Character(null)
    {
        public List<ushort> Impacts { get; } = [];

        public override int DoFallDamage(ushort impact)
        {
            Impacts.Add(impact);
            return 0;
        }
    }
}
