using System.Numerics;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Transfers;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

public sealed partial class CarriageBoardingTests
{
    [Test]
    public async Task BeaktalonLift_OnlyTheTwoPublicUnboundLiftsUseTheRoutePose()
    {
        var lift = Motor(548, AttachPointKind.Trailed0, 46);
        lift.Template.TransferBindings.Clear();
        // The six unspawned Andelph templates share the vehicle model.
        foreach (var id in new uint[] { 17, 18, 19, 20, 21, 22 })
        {
            lift.Template.Id = id;
            await Assert.That(lift.UsesRoutePose).IsFalse();
        }
        lift.Template.Id = 68;
        lift.Template.TransferBindings.Add(new TransferBindings { TransferId = 46, AttachPointId = AttachPointKind.Trailed0 });
        await Assert.That(lift.UsesRoutePose).IsFalse();
    }

    [Test]
    [Arguments(68u)]
    [Arguments(69u)]
    public async Task BeaktalonLift_ObserversSeeTheMovingRollerAlongTheCableAtBothStops(uint id)
    {
        var world = SetUpStation(cells: 16, modelId: 548, vehicleModelId: 16);
        var template = new TransferTemplate { Id = id, ModelId = 548, Name = "Beaktalon Lift", WaitTime = 10, Cyclic = true, PathSmoothing = 8 };
        // These are the two complete stock legs from main_world zone 154.
        Vector3[] leg1 =
        [
            new(6125.6553f, 12822.7323f, 1113.042f),
            new(6126.0944f, 12822.7092f, 1113.0657f),
            new(6126.5602f, 12822.8428f, 1113.0652f),
            new(6127.2448f, 12823.2831f, 1113.0757f),
            new(6127.6694f, 12823.8638f, 1113.0923f),
            new(6128.3262f, 12825.5106f, 1113.0146f),
            new(6134.4469f, 12841.4332f, 1107.3848f),
            new(6140.5275f, 12857.2354f, 1102.49f),
            new(6142.9538f, 12863.6259f, 1100.9003f),
            new(6146.2898f, 12872.2941f, 1099.0391f),
            new(6152.4231f, 12888.2488f, 1096.5406f),
            new(6158.7954f, 12904.825f, 1095.0951f),
            new(6163.6443f, 12917.4384f, 1094.4832f),
            new(6173.3353f, 12942.7393f, 1094.4618f),
            new(6180.1477f, 12960.5854f, 1094.8311f),
            new(6180.8665f, 12962.5181f, 1094.8428f),
            new(6180.9244f, 12963.5476f, 1094.8213f),
            new(6180.4639f, 12964.3833f, 1094.8052f),
            new(6180.1125f, 12964.6869f, 1094.8317f),
            new(6179.7133f, 12964.8733f, 1094.8616f),
        ];
        Vector3[] leg2 =
        [
            new(6179.2825f, 12965.0033f, 1094.8187f),
            new(6178.3412f, 12964.8021f, 1094.7623f),
            new(6177.4917f, 12963.9055f, 1094.7999f),
            new(6176.8656f, 12962.2947f, 1094.7882f),
            new(6170.1014f, 12944.646f, 1094.4189f),
            new(6160.4104f, 12919.3451f, 1094.4403f),
            new(6155.5615f, 12906.7317f, 1095.0522f),
            new(6149.1892f, 12890.1555f, 1096.4978f),
            new(6143.0558f, 12874.2008f, 1098.9962f),
            new(6139.7199f, 12865.5326f, 1100.8574f),
            new(6137.2936f, 12859.1421f, 1102.4471f),
            new(6131.213f, 12843.34f, 1107.2253f),
            new(6125.0923f, 12827.4174f, 1112.9718f),
            new(6124.4355f, 12825.7705f, 1113.0494f),
            new(6124.1305f, 12824.6466f, 1113.0328f),
            new(6124.4255f, 12823.5465f, 1113.0223f),
            new(6124.8679f, 12823.0728f, 1113.0228f),
            new(6125.2928f, 12822.8562f, 1112.9991f),
        ];
        var outbound = id == 68 ? leg1 : leg2;
        var inbound = id == 68 ? leg2 : leg1;
        AddTransferLeg(template, id == 68 ? 361u : 363u, id == 68 ? "goldeneagle_1" : "goldeneagle_2", 154, 0, 10, outbound);
        AddTransferLeg(template, id == 68 ? 362u : 364u, id == 68 ? "goldeneagle_2" : "goldeneagle_1", 154, 0, 10, inbound);
        ConfigureSpawn(template);
        SetSingleton(new NameManager());
        world.TransferManager = new TransferManager();
        var (lift, viewer, session) = await SpawnUnboundTransfer(world, template);

        var start = outbound[0];
        // Earlier waypoints are within the ordinary 1.5 m arrival radius.
        var firstDirection = outbound[id == 68 ? 3 : 2] - start;
        await CheckPoint(lift.Transform.World.Position, start);
        await CheckObserverHeading(lift, firstDirection);
        await Assert.That(lift.TimeLeft).IsGreaterThan(9000u);
        lift.WaitTime = DateTime.MinValue;

        var lastDirection = Vector3.Zero;
        var steps = 0;
        for (var tick = 0; tick < 250 && lift.Steering == 0; tick++)
        {
            if (steps == 0)
                lift.WaitTime = DateTime.MinValue;
            var before = lift.Transform.World.Position;
            session.Packets.Clear();
            lift.MoveTo(lift);
            var travel = lift.Transform.World.Position - before;
            if (travel.LengthSquared() < 0.0001f)
                continue;
            if (steps == 0)
                await CheckMovementAndObserver(lift, session.Packets, firstDirection);
            steps++;
            lastDirection = travel;
        }
        await Assert.That(steps).IsGreaterThan(100);
        await Assert.That(lift.Steering).IsEqualTo(1);
        await Assert.That(lift.TimeLeft).IsGreaterThan(9000u);
        var dock = lift.Transform.World.Position;
        await Assert.That(Vector3.Distance(dock, outbound[^1])).IsLessThan(lift.RangeToCheckPoint);
        world.GetRegionByPos(start).RemoveObject(viewer);
        viewer.Transform.Local.SetPosition(dock);
        world.GetRegionByPos(dock).AddObject(viewer);
        await CheckObserverHeading(lift, lastDirection);
        session.Packets.Clear();
        lift.MoveTo(lift);
        await CheckPoint(lift.Transform.World.Position, dock);
        await Assert.That(session.Packets).IsEmpty();

        lift.WaitTime = DateTime.MinValue;
        for (var tick = 0; tick < 8 && lift.Transform.World.Position == dock; tick++)
            lift.MoveTo(lift);
        var departure = lift.Transform.World.Position - dock;
        await Assert.That(departure.LengthSquared()).IsGreaterThan(0.5f);
        await CheckMovementAndObserver(lift, session.Packets, departure);
    }
}
