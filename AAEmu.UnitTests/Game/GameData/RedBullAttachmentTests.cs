using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.GameData;
using AAEmu.Game.IO;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.World.Transform;

namespace AAEmu.UnitTests.Game.GameData;

public sealed class RedBullAttachmentTests
{
    private static readonly Dictionary<AttachPointKind, string> NativeHelpers = new()
    {
        [AttachPointKind.Driver] = "driver",
        [AttachPointKind.Sail0] = "sail0",
        [AttachPointKind.Box0] = "box0",
        [AttachPointKind.Box1] = "box1"
    };

    [Test]
    public async Task Load_RedBullSupplementAddsOnlySupportedPointsAndKeepsOtherVehicles()
    {
        var data = LoadAttachments();
        var points = data.GetAttachPointsForSlave(1427);

        await Assert.That(points).IsNotNull();
        await Assert.That(points.Keys).IsEquivalentTo(NativeHelpers.Keys);
        await Assert.That(points.ContainsKey(AttachPointKind.Passenger0)).IsFalse();
        await Assert.That(data.GetAttachPointsForSlave(128)).IsNotNull();
        await Assert.That(data.GetAttachPointsForSlave(128).ContainsKey(AttachPointKind.Driver)).IsTrue();
    }

    [Test]
    public async Task Load_RedBullSupplementConvertsDiskDegreesOnce()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "slave_attach_points_redbull.json");
        var contents = await File.ReadAllTextAsync(path);
        var parsed = JsonHelper.TryDeserializeObject(contents, out List<SlaveModelAttachPoint> records, out var error);
        await Assert.That(parsed).IsTrue();
        await Assert.That(error).IsNull();
        var raw = records.Single().AttachPoints;
        var data = LoadAttachments();

        foreach (var point in data.GetAttachPointsForSlave(1427))
        {
            await Assert.That(point.Value.AsPositionVector()).IsEqualTo(raw[point.Key].AsPositionVector());
            await Assert.That(MathF.Abs(point.Value.Roll - raw[point.Key].Roll * MathF.PI / 180)).IsLessThan(0.000001f);
            await Assert.That(MathF.Abs(point.Value.Pitch - raw[point.Key].Pitch * MathF.PI / 180)).IsLessThan(0.000001f);
            await Assert.That(MathF.Abs(point.Value.Yaw - raw[point.Key].Yaw * MathF.PI / 180)).IsLessThan(0.000001f);
        }

        var beforeReload = data.GetAttachPointsForSlave(1427)[AttachPointKind.Box0].Roll;
        data.LoadSlaveAttachmentPointLocations();
        await Assert.That(data.GetAttachPointsForSlave(1427)[AttachPointKind.Box0].Roll).IsEqualTo(beforeReload);
    }

    [Test]
    [Arguments(0f)]
    [Arguments(1.2f)]
    public async Task ExactClient_RedBullPointsFollowNativeHelpersUnderParentRotation(float yaw)
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_HOUSING_GAME_PAK");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_HOUSING_GAME_PAK for the native Speedcar attachment check.");
        var source = new ClientSource { PathName = path, SourceType = ClientSourceType.GamePak };
        await Assert.That(source.Open()).IsTrue();
        try
        {
            using var stream = source.GetFileStream("game/objects/env/06_unit/05_carriage/speedcar_body.chr");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var native = CryGeometryResolver.ReadCgf(buffer.ToArray());
            var points = LoadAttachments().GetAttachPointsForSlave(1427);
            using var parent = new Transform(null);
            parent.Local.SetPosition(123, 456, 78, 0.2f, -0.3f, yaw);
            var parentMatrix = Matrix4x4.CreateFromQuaternion(parent.World.ToQuaternion()) *
                Matrix4x4.CreateTranslation(parent.World.Position);

            foreach (var (kind, helperName) in NativeHelpers)
            {
                var expected = native.CharacterBones.Single(bone => bone.Name == helperName).BindTransform * parentMatrix;
                var point = points[kind];
                using var child = parent.CloneAttached(null);
                child.Local.SetPosition(point.X, point.Y, point.Z, point.Roll, point.Pitch, point.Yaw);
                var actual = Matrix4x4.CreateFromQuaternion(child.World.ToQuaternion()) *
                    Matrix4x4.CreateTranslation(child.World.Position);

                await Assert.That(Vector3.Distance(actual.Translation, expected.Translation)).IsLessThan(0.0001f);
                foreach (var basis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                {
                    await Assert.That(Vector3.Distance(Vector3.TransformNormal(basis, actual),
                        Vector3.TransformNormal(basis, expected))).IsLessThan(0.00001f);
                }
            }
        }
        finally { source.Close(); }
    }

    private static SlaveGameData LoadAttachments()
    {
        var data = new SlaveGameData();
        data.LoadSlaveAttachmentPointLocations();
        return data;
    }
}
