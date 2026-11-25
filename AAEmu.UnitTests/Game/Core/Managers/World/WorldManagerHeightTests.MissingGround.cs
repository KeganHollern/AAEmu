using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.AI.v2.AiCharacters;
using AAEmu.Game.Models.Game.AI.v2.Behaviors.Common;
using AAEmu.Game.Models.Game.AI.v2.Params;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

public sealed partial class WorldManagerHeightTests
{
    [Test]
    [Arguments(0f)]
    [Arguments(3f)]
    public async Task MoveTowards_NoGroundBeyondLastAcceptedFoot_HoldsAndRetries(float targetZ)
    {
        var npc = CreateDeckWalker(hasDeck: false);
        var world = npc.ParentWorld.Template;
        world.ZoneBaiLoader.Clear();
        var west = world.Cells[0, 0];
        var east = new WorldCell(1, 0, world);
        SetPrivateMember(east, nameof(WorldCell.Loaded), true);
        world.CellX = 2;
        world.Cells = new WorldCell[,] { { west }, { east } };
        npc.Ai.HomePosition = new Vector3(600f, 100f, 12f);
        var lastFoot = new Vector3(WorldManager.CELL_SIZE - 2f, 100f, 0f);
        npc.Transform.Local.SetPosition(lastFoot - Vector3.UnitX * Step);
        var notifications = 0;
        npc.Events.OnMovement += (_, _) => notifications++;
        npc.MoveTowards(lastFoot, Step, rangeTolerance: 0f);
        await Assert.That(npc.Transform.Local.Position).IsEqualTo(lastFoot);
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(0f);

        // The next terrain triangle needs a vertex from the missing neighboring heightmap.
        var destination = lastFoot + new Vector3(2f, 0f, targetZ);
        var rotation = npc.Transform.Local.Rotation;
        var trace = NpcStepSurfaceTrace.DescribeStep(npc, destination, Step).ToArray();
        await Assert.That(trace[1]).Contains("selected=HoldWithoutGround z=0.000");
        for (var retry = 0; retry < 2; retry++)
        {
            var arrived = npc.MoveTowards(destination, Step, rangeTolerance: 0f);
            await Assert.That(arrived).IsFalse();
            await Assert.That(npc.Transform.Local.Position).IsEqualTo(lastFoot);
            await Assert.That(npc.Transform.Local.Rotation).IsEqualTo(rotation);
        }
        await Assert.That(notifications).IsEqualTo(1);
        await Assert.That(npc.Movements.Count).IsEqualTo(1);

        SetPrivateMember(east, nameof(WorldCell.HeightMap), west.HeightMap);
        npc.MoveTowards(destination, Step, rangeTolerance: 0f);
        await Assert.That(npc.Transform.Local.Position.X).IsGreaterThan(lastFoot.X);
        await Assert.That(npc.Transform.Local.Position.Z).IsEqualTo(0f);
        await Assert.That(notifications).IsEqualTo(2);
        await Assert.That(npc.Movements.Count).IsEqualTo(2);
    }

    public enum MissingGroundControl { Flying, Parented, StickyParent, Authored, OtherWorld, HeightSourcesDisabled }

    [Test]
    [Arguments(MissingGroundControl.Flying)]
    [Arguments(MissingGroundControl.Parented)]
    [Arguments(MissingGroundControl.StickyParent)]
    [Arguments(MissingGroundControl.Authored)]
    [Arguments(MissingGroundControl.OtherWorld)]
    [Arguments(MissingGroundControl.HeightSourcesDisabled)]
    public async Task MoveTowards_NoGround_PreservesOtherMovementPaths(MissingGroundControl mode)
    {
        var npc = CreateDeckWalker(hasDeck: false);
        var world = npc.ParentWorld.Template;
        world.ZoneBaiLoader.Clear();
        SetPrivateMember(world.Cells[0, 0], nameof(WorldCell.HeightMap), null);
        npc.Ai.HomePosition = new Vector3(60f, 100f, 12f);
        if (mode == MissingGroundControl.Flying)
            npc.CanFly = true;
        if (mode is MissingGroundControl.Parented or MissingGroundControl.StickyParent)
        {
            var parent = new Npc { Template = new NpcTemplate { Scale = 1f } };
            npc.Template = new NpcTemplate { Scale = 1f };
            if (mode == MissingGroundControl.Parented)
                npc.Transform.Parent = parent.Transform;
            else
                npc.Transform.StickyParent = parent.Transform;
        }
        if (mode == MissingGroundControl.OtherWorld)
            world.Name = "instance";
        if (mode == MissingGroundControl.HeightSourcesDisabled)
        {
            AppConfiguration.Instance.World.GeoDataMode = false;
            AppConfiguration.Instance.HeightMapsEnable = false;
        }

        var from = new Vector3(100f, 100f, 6f);
        npc.Transform.Local.SetPosition(from);
        var authored = mode == MissingGroundControl.Authored;
        npc.MoveTowards(from + Vector3.UnitX, Step, rangeTolerance: 0f, authoredPathZ: authored);
        var expected = new Vector3(from.X + Step, from.Y, authored ? from.Z : npc.Ai.HomePosition.Z);
        await Assert.That(npc.Transform.Local.Position).IsEqualTo(expected);
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(expected.Z);
    }

    [Test]
    [Arguments(true, true)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(false, false)]
    public async Task CompletedReturn_UsesMeasuredGroundOrExistingHomeFallback(bool teleport, bool hasGround)
    {
        var world = CreateDeckWalker(hasDeck: false).ParentWorld;
        var npc = new ReturnWalker { ParentWorld = world, Template = new NpcTemplate { Scale = 1f },
            DisabledSetPosition = true, Hp = 100 };
        SetPrivateMember(npc.Transform, "_zoneId", 100u);
        var ai = new ReturnProbe { Owner = npc, HomePosition = new Vector3(85f, 100f, 12f),
            IdlePosition = new Vector3(92f, 100f, 8f),
            Param = new AiParams { AlwaysTeleportOnReturn = teleport, RestorationOnReturn = false } };
        npc.Ai = ai;
        world.Template.ZoneBaiLoader.Clear();
        if (!hasGround)
            SetPrivateMember(world.Template.Cells[0, 0], nameof(WorldCell.HeightMap), null);
        var from = new Vector3(100f, 100f, 6f);
        npc.Transform.Local.SetPosition(from);
        npc.Buffs = Mock.Of<IBuffs>().Object;
        var behavior = new ReturnStateBehavior { Ai = ai };
        behavior.Enter();
        if (!teleport)
        {
            behavior.Tick(TimeSpan.FromMilliseconds(200));
            await Assert.That(ai.Completed).IsFalse();
            if (!hasGround)
                await Assert.That(npc.Transform.Local.Position).IsEqualTo(from);
            SetPrivateMember(behavior, "_timeoutTime", DateTime.UtcNow.AddSeconds(-1));
            behavior.Tick(TimeSpan.FromMilliseconds(200));
        }
        await Assert.That(ai.Completed).IsTrue();
        var expected = new Vector3(ai.IdlePosition.X, ai.IdlePosition.Y, hasGround ? 0f : ai.HomePosition.Z);
        await Assert.That(npc.Transform.Local.Position).IsEqualTo(expected);
        await Assert.That(npc.Movements[^1].Z).IsEqualTo(expected.Z);
    }

    [Test]
    public async Task CompletedReturn_FromAmbiguousCaveFloor_TeleportsToItsHomeHeight()
    {
        Vector3 Point(float x, float y) => new(x, y, DeckZ + .007f * (x - 111f));
        var lower = new[] { Point(101, 98), Point(121, 98), Point(111, 105) };
        var mesh = new CryTriangleMesh([.. lower, .. lower.Select(p => p + Vector3.UnitZ * .007f)],
            [0, 1, 2, 3, 4, 5], []);
        var world = CreateDeckWalker(customMesh: mesh).ParentWorld;
        world.Template.ZoneBaiLoader.Clear();
        SetTerrainHeight(world.Template, 18);
        var npc = new ReturnWalker { ParentWorld = world, Template = new NpcTemplate { Scale = 1f },
            DisabledSetPosition = true, Hp = 100, Buffs = Mock.Of<IBuffs>().Object };
        SetPrivateMember(npc.Transform, "_zoneId", 100u);
        var home = Point(116, 100) + Vector3.UnitZ * .007f;
        var ai = new ReturnProbe { Owner = npc, IdlePosition = home, HomePosition = home,
            Param = new AiParams { AlwaysTeleportOnReturn = true, RestorationOnReturn = false } };
        npc.Ai = ai;
        var start = Point(111, 100);
        npc.Transform.Local.SetPosition(start);

        npc.MoveTowards(home, Step, rangeTolerance: 0f);
        await Assert.That(npc.Transform.Local.Position).IsEqualTo(start);
        await Assert.That(npc.Movements).IsEmpty();
        new ReturnStateBehavior { Ai = ai }.Enter();
        await Assert.That(ai.Completed).IsTrue();
        await Assert.That(npc.Transform.Local.Position).IsEqualTo(home);
        await Assert.That(npc.Movements.Count).IsEqualTo(2); // The return sends the relocation and its stop.
        await Assert.That(npc.Movements.All(move => move.Z == home.Z)).IsTrue();
    }

    private sealed class ReturnWalker : MovementProbeNpc
    {
        public override float BaseMoveSpeed => 2f;
        public override float MoveSpeedMul => 1f;
    }

    private sealed class ReturnProbe : DummyAiCharacter
    {
        public bool Completed { get; private set; }
        public override void GoToIdle() => Completed = true;
    }
}
