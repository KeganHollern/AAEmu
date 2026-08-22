using System.Globalization;
using System.Numerics;
using System.Reflection;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Scripts.Commands;
using AAEmu.Game.Scripts.SubCommands.Doodads;
using AAEmu.Game.Utils;
using AAEmu.Game.Utils.Scripts;
using Newtonsoft.Json.Linq;

namespace AAEmu.UnitTests.Game.Utils.Scripts.SubCommands.Doodads;

public class DoodadEditSubCommandTests
{
    [Test]
    public async Task NudgeAndRotate_UseSpawnAxesAndDegrees()
    {
        var original = new DoodadPlacementSnapshot(
            new Vector3(486.4f, 327.8f, 165.8f),
            new Vector3(0f, 0f, (-30f).DegToRad()),
            1f,
            14240);

        var result = original
            .Nudge("x", 0.25f)
            .Nudge("z", -0.1f)
            .RotateDegrees("yaw", 15f);

        await Assert.That(result.Position.X).IsEqualTo(486.65f).Within(0.0001f);
        await Assert.That(result.Position.Y).IsEqualTo(327.8f).Within(0.0001f);
        await Assert.That(result.Position.Z).IsEqualTo(165.7f).Within(0.0001f);
        await Assert.That(result.Rotation.Z.RadToDeg()).IsEqualTo(-15f).Within(0.0001f);
        await Assert.That(result.Scale).IsEqualTo(1f);
        await Assert.That(result.FuncGroupId).IsEqualTo(14240u);
    }

    [Test]
    public async Task SetAndRotate_NormalizeRotationToSignedDegrees()
    {
        var original = new DoodadPlacementSnapshot(Vector3.Zero, Vector3.Zero, 1f, 1);

        var result = original
            .SetValue("roll", -181f)
            .SetValue("pitch", 540f)
            .SetValue("yaw", 190f)
            .RotateDegrees("yaw", -30f);

        await Assert.That(result.Rotation.X.RadToDeg()).IsEqualTo(179f).Within(0.0001f);
        await Assert.That(result.Rotation.Y.RadToDeg()).IsEqualTo(-180f).Within(0.0001f);
        await Assert.That(result.Rotation.Z.RadToDeg()).IsEqualTo(160f).Within(0.0001f);
    }

    [Test]
    public async Task Validation_RejectsNonFiniteValuesAndUnsafeScale()
    {
        var valid = new DoodadPlacementSnapshot(Vector3.One, Vector3.Zero, 1f, 1);
        var tooSmall = valid.SetValue("scale", 0.009f);
        var tooLarge = valid.SetValue("scale", 100.01f);
        var notFinite = valid.SetValue("x", float.NaN);
        var wrappedX = valid.SetValue("x", 32768f);
        var wrappedZ = valid.SetValue("z", 4096f);
        var lowestZ = valid.SetValue("z", -100f);

        await Assert.That(valid.IsValid()).IsTrue();
        await Assert.That(tooSmall.IsValid()).IsFalse();
        await Assert.That(tooLarge.IsValid()).IsFalse();
        await Assert.That(notFinite.IsValid()).IsFalse();
        await Assert.That(wrappedX.IsValid()).IsFalse();
        await Assert.That(wrappedZ.IsValid()).IsFalse();
        await Assert.That(lowestZ.IsValid()).IsTrue();
    }

    [Test]
    public async Task PlacementJson_IsInvariantAndPasteReady()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            var snapshot = new DoodadPlacementSnapshot(
                new Vector3(486.4f, 327.8f, 165.8f),
                new Vector3(0f, 0f, (-30f).DegToRad()),
                1f,
                14298);

            var json = snapshot.ToPlacementJson(5541, [812u, 813u]);
            var root = JObject.Parse(json);

            await Assert.That((uint)root["UnitId"]).IsEqualTo(5541u);
            await Assert.That((float)root["Position"]["X"]).IsEqualTo(486.4f);
            await Assert.That((float)root["Position"]["Yaw"]).IsEqualTo(-30f).Within(0.0001f);
            await Assert.That((uint)root["RelatedIds"][0]).IsEqualTo(812u);
            // Scale must remain explicit when an authored value is changed from non-default to 1.
            await Assert.That((float)root["Scale"]).IsEqualTo(1f);
            await Assert.That(root.ContainsKey("FuncGroupId")).IsFalse();
            await Assert.That(json.Contains("486,4", StringComparison.Ordinal)).IsFalse();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Test]
    public async Task PlacementSession_RequiresDistinctPreviewIdAndSingleCleanupCallback()
    {
        var editor = new Character(new UnitCustomModelParams());
        var snapshot = new DoodadPlacementSnapshot(Vector3.Zero, Vector3.Zero, 1f, 1);
        var spawner = new DoodadSpawner();
        var session = new DoodadPlacementSession(editor, 1, 500, 501, Guid.NewGuid(), 5541,
            snapshot, spawner);
        var cleanupCalls = 0;
        var subscription = new DoodadPlacementSubscription(() => cleanupCalls++);

        subscription.Dispose();
        subscription.Dispose();

        await Assert.That(session.ObjId).IsEqualTo(500u);
        await Assert.That(session.PreviewObjId).IsEqualTo(501u);
        await Assert.That(cleanupCalls).IsEqualTo(1);
        Assert.Throws<ArgumentException>(() => new DoodadPlacementSession(editor, 1, 500, 500,
            Guid.NewGuid(), 5541, snapshot, spawner));
    }

    [Test]
    public async Task DoodadRoot_RegistersEditorAndAlias()
    {
        var command = new DoodadCmd();
        var help = command.GetCommandLineHelp();

        await Assert.That(help).Contains("edit");
        await Assert.That(help).Contains("place");
    }

    [Test]
    public async Task AuthoredSpawnerRegistry_RequiresExactLoadedInstance()
    {
        var world = new WorldInstance(new WorldTemplate { Id = 1, CellX = 1, CellY = 1 }, 0, true, 1);
        var manager = new SpawnManager(world);
        world.SpawnManager = manager;
        var authored = new DoodadSpawner
        {
            Id = 7,
            UnitId = 5541,
            ParentWorld = world
        };
        var registryProperty = typeof(SpawnManager).GetProperty("DoodadSpawners",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var registry = (Dictionary<uint, DoodadSpawner>)registryProperty.GetValue(manager);
        registry.Add(authored.Id, authored);
        var adHoc = new DoodadSpawner
        {
            Id = authored.Id,
            UnitId = authored.UnitId,
            ParentWorld = world
        };

        await Assert.That(manager.IsAuthoredDoodadSpawner(authored)).IsTrue();
        await Assert.That(manager.IsAuthoredDoodadSpawner(adHoc)).IsFalse();
    }

    [Test]
    public async Task SafetyBoundary_AllowsOnlyRegisteredUnownedRootWorldSpawns()
    {
        var doodad = new Doodad
        {
            Spawner = new DoodadSpawner(),
            OwnerType = DoodadOwnerType.System
        };

        // A bare/ad-hoc runtime spawner is not proof that the doodad came from world JSON.
        await Assert.That(DoodadEditSubCommand.IsSafeAuthoredWorldDoodad(doodad, out _)).IsFalse();
        await Assert.That(DoodadEditSubCommand.IsSafeAuthoredWorldDoodad(doodad, true, out _)).IsTrue();

        doodad.DbId = 9;
        await Assert.That(DoodadEditSubCommand.IsSafeAuthoredWorldDoodad(doodad, true, out _)).IsFalse();
        doodad.DbId = 0;

        doodad.OwnerType = DoodadOwnerType.Character;
        await Assert.That(DoodadEditSubCommand.IsSafeAuthoredWorldDoodad(doodad, true, out _)).IsFalse();
        doodad.OwnerType = DoodadOwnerType.System;

        doodad.OwnerId = 10;
        await Assert.That(DoodadEditSubCommand.IsSafeAuthoredWorldDoodad(doodad, true, out _)).IsFalse();
        doodad.OwnerId = 0;

        doodad.ParentObjId = 20;
        await Assert.That(DoodadEditSubCommand.IsSafeAuthoredWorldDoodad(doodad, true, out _)).IsFalse();
        doodad.ParentObjId = 0;

        doodad.AttachPoint = AttachPointKind.Driver;
        await Assert.That(DoodadEditSubCommand.IsSafeAuthoredWorldDoodad(doodad, true, out _)).IsFalse();
        doodad.AttachPoint = AttachPointKind.System;

        doodad.Spawner = null;
        await Assert.That(DoodadEditSubCommand.IsSafeAuthoredWorldDoodad(doodad, true, out _)).IsFalse();
    }

}
