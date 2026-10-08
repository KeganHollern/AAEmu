using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Movements;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

[NotInParallel]
public sealed class PhysicalVehicleRequirementsTests
{
    private static readonly FieldInfo ModelsInstance = typeof(Singleton<ModelManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private object _previousModels;
    private bool _previousDebugInfo;
    private Character _driver;
    private Slave _car;
    private VehicleModel _model;

    [Before(Test)]
    public void SetUp()
    {
        _previousDebugInfo = AppConfiguration.Instance.DebugInfo;
        AppConfiguration.Instance.DebugInfo = false;
        _previousModels = ModelsInstance.GetValue(null);
        _model = new VehicleModel { Id = 45, UseWheeledVehicleSimulation = true };
        var models = new ModelManager();
        typeof(ModelManager).GetField("_models", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(models, new Dictionary<string, Dictionary<uint, Model>>
            { ["VehicleModel"] = new() { [45] = _model } });
        typeof(ModelManager).GetField("_modelTypes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(models, new Dictionary<uint, ModelType>
            { [450] = new() { Id = 450, SubId = 45, SubType = "VehicleModel" } });
        ModelsInstance.SetValue(null, models);
        _driver = new Character(null) { ObjId = 10, AttachedPoint = AttachPointKind.Driver };
        _car = new Slave { ObjId = 20, ModelId = 450, VehicleVelocity = new Vector3(6, 0, 0) };
        _car.AttachedCharacters[AttachPointKind.Driver] = _driver;
        _driver.Transform.Parent = _car.Transform;
    }

    [After(Test)]
    public void TearDown()
    {
        if (_driver != null)
            _driver.Transform.Parent = null;
        ModelsInstance.SetValue(null, _previousModels);
        AppConfiguration.Instance.DebugInfo = _previousDebugInfo;
    }

    [Test]
    [Arguments(0f, 0f, 0f, true)]
    [Arguments(5f, 0f, 0f, true)]
    [Arguments(-5f, 0f, 0f, true)]
    [Arguments(3f, 4f, 0f, true)]
    [Arguments(0f, 0f, 5f, true)]
    [Arguments(5.0005f, 0f, 0f, false)]
    [Arguments(-5.0005f, 0f, 0f, false)]
    [Arguments(3f, 4f, 0.1f, false)]
    [Arguments(0f, 0f, 5.0005f, false)]
    public async Task PhysicalDriver_UsesNativeThreeDimensionalSpeedBoundary(float x, float y, float z, bool allowed)
    {
        _car.VehicleVelocity = new Vector3(x, y, z);
        var requirement = new UnitReqs { KindType = UnitReqsKindType.NotOnMovingPhysicalVehicle };
        var result = requirement.Validate(_driver, _car);
        await Assert.That(result.ResultKey).IsEqualTo(allowed ? SkillResultKeys.ok : SkillResultKeys.skill_urk_unknown);
        await Assert.That(result.ResultUShort).IsEqualTo(allowed ? (ushort)0 : (ushort)0x308);
        await Assert.That(result.ResultUInt).IsEqualTo(0u);
    }

    [Test]
    [Arguments("none")]
    [Arguments("deck")]
    [Arguments("passenger")]
    [Arguments("stale-seat")]
    [Arguments("other-driver")]
    [Arguments("non-physical")]
    [Arguments("other-model")]
    [Arguments("retired")]
    [Arguments("nested-deck")]
    public async Task OtherAttachments_DoNotApplyDriverVehicleRule(string attachment)
    {
        switch (attachment)
        {
            case "none": _driver.Transform.Parent = null; break;
            case "deck": _driver.AttachedPoint = AttachPointKind.None; break;
            case "passenger": _driver.AttachedPoint = AttachPointKind.Passenger0; break;
            case "stale-seat": _car.AttachedCharacters.Clear(); break;
            case "other-driver": _car.AttachedCharacters[AttachPointKind.Driver] = new Character(null); break;
            case "non-physical": _model.UseWheeledVehicleSimulation = false; break;
            case "other-model": _car.ModelId = 999; break;
            case "retired": _car.AttachmentsRetired = true; break;
            case "nested-deck":
                var child = new Slave();
                child.Transform.Parent = _car.Transform;
                _driver.Transform.Parent = child.Transform;
                _driver.AttachedPoint = AttachPointKind.None;
                break;
        }
        await Assert.That(PhysicalVehicleRequirements.Validate(_driver).ResultKey).IsEqualTo(SkillResultKeys.ok);
    }

    [Test]
    public async Task NestedDrivenSlave_UsesItsOwnModelAndVelocity()
    {
        var ship = new Slave { VehicleVelocity = new Vector3(15, 0, 0) };
        _car.Transform.Parent = ship.Transform;
        _car.VehicleVelocity = Vector3.Zero;
        await Assert.That(PhysicalVehicleRequirements.Validate(_driver).ResultKey).IsEqualTo(SkillResultKeys.ok);
        _car.VehicleVelocity = new Vector3(6, 0, 0);
        await Assert.That(PhysicalVehicleRequirements.Validate(_driver).ResultKey).IsEqualTo(SkillResultKeys.skill_urk_unknown);
    }

    [Test]
    public async Task NoCharacter_DoesNotApplyLocalDriverRule()
    {
        await Assert.That(PhysicalVehicleRequirements.Validate(null).ResultKey).IsEqualTo(SkillResultKeys.ok);
    }

    [Test]
    public async Task VehiclePacket_DecodesSignedLinearVelocityAndSeparatesAngularVelocity()
    {
        var packet = new VehicleMoveType
        {
            VelX = -32767, VelY = 32767, VelZ = 16384,
            AngVelX = 15, AngVelY = 16, AngVelZ = 17
        };
        var stream = packet.Write(new PacketStream());
        stream.Rollback();
        var decoded = new VehicleMoveType();
        decoded.Read(stream);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
        await Assert.That(decoded.LinearVelocity).IsEqualTo(new Vector3(-30, 30, 15.000458f));
        packet.VelX = packet.VelY = packet.VelZ = 0;
        _car.VehicleVelocity = packet.LinearVelocity;
        await Assert.That(PhysicalVehicleRequirements.Validate(_driver).ResultKey).IsEqualTo(SkillResultKeys.ok);
    }

    [Test]
    [Arguments((short)5461, true)]
    [Arguments((short)5462, false)]
    [Arguments((short)-5461, true)]
    [Arguments((short)-5462, false)]
    [Arguments(short.MaxValue, false)]
    [Arguments(short.MinValue, false)]
    public async Task EncodedVelocity_UsesNativeValuesOnBothSidesOfFiveMetresPerSecond(short encoded, bool allowed)
    {
        _car.VehicleVelocity = new VehicleMoveType { VelX = encoded }.LinearVelocity;
        await Assert.That(PhysicalVehicleRequirements.Validate(_driver).ResultKey)
            .IsEqualTo(allowed ? SkillResultKeys.ok : SkillResultKeys.skill_urk_unknown);
        if (encoded == short.MinValue)
            await Assert.That(_car.VehicleVelocity.X).IsLessThan(-30f);
    }

    [Test]
    public async Task ExactClient_LoadsPhysicalVehicleFlagsThroughProductionReader()
    {
        var compact = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(compact), "Set AAEMU_COMBAT_TEST_COMPACT to the read-only r208022 compact.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = compact, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, use_wheeled_vehicle_simulation FROM vehicle_models";
        using var reader = new SQLiteWrapperReader(command.ExecuteReader());
        var physicalIds = new List<uint>();
        var nonPhysicalCount = 0;
        while (reader.Read())
        {
            var model = new VehicleModel();
            ModelManager.ReadVehicleSimulationSettings(reader, model);
            if (model.UseWheeledVehicleSimulation)
                physicalIds.Add(reader.GetUInt32("id"));
            else
                nonPhysicalCount++;
        }
        await Assert.That(physicalIds.Order().SequenceEqual(new uint[] { 45, 46, 47, 48, 50, 52, 54, 55, 56 })).IsTrue();
        await Assert.That(nonPhysicalCount).IsEqualTo(54);
    }
}
