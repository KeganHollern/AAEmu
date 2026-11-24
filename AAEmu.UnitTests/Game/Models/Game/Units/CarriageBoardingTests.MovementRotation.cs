using System.Numerics;
using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Transfers;
using AAEmu.Game.Models.Game.Units.Movements;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

public sealed partial class CarriageBoardingTests
{
    // Each pair is one real segment from the r208022 transfer path named in the row.
    [Test]
    [Arguments(10u, 41u, "whiteforest_roy_out_1", 143u, 9697.0868f, 12787.4249f, 162.2975f, 9695.0465f, 12786.3815f, 162.2185f)]
    [Arguments(109u, 562u, "maha_sunrise_in", 141u, 17767.5117f, 8366.2820f, 260.4665f, 17758.3342f, 8365.3018f, 260.2418f)]
    [Arguments(123u, 658u, "safari_4", 301u, 20442.2742f, 27360.0087f, 138.8135f, 20414.1025f, 27355.8923f, 134.4822f)]
    [Arguments(124u, 662u, "safari_7", 301u, 20441.9680f, 27359.6658f, 138.7548f, 20413.7964f, 27355.5494f, 134.4236f)]
    [Arguments(5u, 19u, "tiger01", 258u, 22085.9347f, 8411.0393f, 433.9845f, 22087.0254f, 8412.5343f, 433.9839f)]
    [Arguments(25u, 117u, "tiger02", 258u, 22137.9678f, 8482.7140f, 481.4974f, 22138.8788f, 8483.9543f, 482.0191f)]
    [Arguments(26u, 129u, "tiger03", 258u, 22207.9316f, 8578.9711f, 525.1285f, 22211.5255f, 8584.0393f, 525.1462f)]
    [Arguments(27u, 141u, "tiger04", 258u, 22295.9017f, 8700.0212f, 620.0759f, 22297.3950f, 8702.0790f, 620.0846f)]
    public async Task GroundTransfer_MovementAndNewObserverFaceTheStockSegment(uint id, uint legId, string legName, uint zone,
        float x, float y, float z, float nextX, float nextY, float nextZ)
    {
        var (modelId, vehicleModelId, trailerId, trailerModelId) = id switch
        {
            10 or 109 => (654u, 22u, 46u, 653u),
            123 or 124 => (1463u, 57u, 122u, 1459u),
            5 => (565u, 17u, 28u, 565u),
            25 => (565u, 17u, 29u, 565u),
            26 => (565u, 17u, 42u, 565u),
            27 => (565u, 17u, 43u, 565u),
            _ => throw new ArgumentOutOfRangeException(nameof(id))
        };
        var world = SetUpStation(cells: 28, modelId: modelId, vehicleModelId: vehicleModelId);
        var template = Motor(modelId, AttachPointKind.Trailed0, trailerId).Template;
        template.Id = id;
        template.Name = legName;
        var start = new WorldSpawnPosition { X = x, Y = y, Z = z, ZoneId = zone };
        var next = new WorldSpawnPosition { X = nextX, Y = nextY, Z = nextZ, ZoneId = zone };
        template.TransferAllPaths.Add(new TransferPaths { Id = legId, OwnerId = id, PathName = legName });
        template.TransferRoads.Add(new TransferRoads { Name = legName, Pos = [start, next] });
        ConfigureSpawn(template, cabinModelId: trailerModelId);
        if (id == 25)
            template.TransferBindings.Clear();
        SetSingleton(new NameManager());
        world.TransferManager = new TransferManager();
        var file = Path.Combine(AppContext.BaseDirectory, "Data", "Worlds", "main_world", "transfer_spawns.json");
        var spawner = JsonHelper.DeserializeObject<List<TransferSpawner>>(await File.ReadAllTextAsync(file)).Single(s => s.UnitId == id);
        spawner.ParentWorld = world;
        var motor = spawner.Spawn(0);
        var session = new TransferVisibilityTests.RecordingSession();
        var viewer = new CharacterMock { ObjId = 400, ParentWorld = world, Connection = new GameConnection(session) };
        viewer.Transform.Local.SetPosition(start.AsPositionVector());
        world.GetRegionByPos(start.AsPositionVector()).AddObject(viewer);
        var initial = motor.Transform.World.Position;
        for (var tick = 0; tick < 3 && motor.Transform.World.Position == initial; tick++)
        {
            motor.WaitTime = DateTime.MinValue;
            session.Packets.Clear();
            motor.MoveTo(motor);
        }
        var direction = motor.Transform.World.Position - initial;
        await Assert.That(direction.Length()).IsGreaterThan(0.1f);
        var packet = session.Packets.Single(p => BitConverter.ToUInt16(p, 6) == SCOffsets.SCOneUnitMovementPacket
            && new PacketStream(p, 8, p.Length - 8).ReadBc() == motor.ObjId);
        var stream = new PacketStream(packet, 8, packet.Length - 8);
        await Assert.That(stream.ReadBc()).IsEqualTo(motor.ObjId);
        await Assert.That(stream.ReadByte()).IsEqualTo((byte)MoveTypeEnum.Transfer);
        var movement = new TransferData();
        movement.Read(stream);
        await CheckObserverHeading(motor, viewer, session, direction);
        await CheckHeading(direction, Vector3.Transform(Vector3.UnitY, movement.Rot));
    }

    [Test]
    public async Task TransferMovement_WritesTheSameRotationsForBothQuaternionSigns()
    {
        var rotation = Quaternion.CreateFromYawPitchRoll(0.6f, -0.3f, 0.9f);
        var positive = new TransferData { Rot = rotation };
        var negative = new TransferData { Rot = Quaternion.Negate(rotation) };
        var forwardBytes = positive.Write(new PacketStream()).GetBytes();
        var reverseBytes = negative.Write(new PacketStream()).GetBytes();
        await Assert.That(reverseBytes.SequenceEqual(forwardBytes)).IsTrue();
        await Assert.That(negative.Rot).IsEqualTo(Quaternion.Negate(rotation));
    }
}
