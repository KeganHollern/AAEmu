using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Skills.SkillControllers;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Skills.SkillControllers;

public class FloatingSkillControllerTests
{
    [Test]
    public async Task ResolveBuffLiftParameters_BubbleTrap_ReturnsControllerHeightAndSpeedWithoutPrivateDuration()
    {
        var template = CreateFloatingTemplate(5200, 2000);

        var result = FloatingSkillController.ResolveBuffLiftParameters(template);

        await Assert.That(result.Height).IsEqualTo(5.2f);
        await Assert.That(result.Speed).IsEqualTo(2f);
        await Assert.That(result.Duration).IsEqualTo(0f);
    }

    [Test]
    public async Task ResolveBuffLiftParameters_BurningBubbleTrap_ReturnsControllerHeightAndSpeedWithoutPrivateDuration()
    {
        var template = CreateFloatingTemplate(20000, 4000);

        var result = FloatingSkillController.ResolveBuffLiftParameters(template);

        await Assert.That(result.Height).IsEqualTo(20f);
        await Assert.That(result.Speed).IsEqualTo(4f);
        await Assert.That(result.Duration).IsEqualTo(0f);
    }

    [Test]
    public async Task ResolveBuffLiftParameters_NonFloatingTemplate_ReturnsZeros()
    {
        var template = new SkillControllerTemplate
        {
            KindId = (uint)SkillControllerKind.Dash,
            Value = [5200, 2000]
        };

        var result = FloatingSkillController.ResolveBuffLiftParameters(template);

        await Assert.That(result).IsEqualTo((0f, 0f, 0f));
    }

    [Test]
    public async Task ResolveBuffLiftParameters_MalformedTemplate_ReturnsZeros()
    {
        var shortTemplate = new SkillControllerTemplate
        {
            KindId = (uint)SkillControllerKind.Floating,
            Value = [5200]
        };

        var shortResult = FloatingSkillController.ResolveBuffLiftParameters(shortTemplate);
        var nullResult = FloatingSkillController.ResolveBuffLiftParameters(null);

        await Assert.That(shortResult).IsEqualTo((0f, 0f, 0f));
        await Assert.That(nullResult).IsEqualTo((0f, 0f, 0f));
    }

    [Test]
    public async Task Tick_TemplateBackedLift_ReachesConfiguredHeightAndHoldsUntilEnd()
    {
        const ushort groundZ = 10;
        const float expectedLiftedZ = 15.2f;
        var owner = CreateOwner(groundZ);
        var target = new TestUnit { ObjId = 2, Name = "target", DisabledSetPosition = true };
        target.Transform.Local.SetPosition(new Vector3(10.5f, 0.5f, groundZ));
        var ticks = new TickManager.TickEventHandler();
        var template = CreateFloatingTemplate(5200, 2000);
        var lift = FloatingSkillController.ResolveBuffLiftParameters(template);
        var controller = new FloatingSkillController(
            template, owner, target, ticks,
            liftHeight: lift.Height, liftSpeed: lift.Speed, liftDuration: lift.Duration)
        {
            SourceBuffId = 96
        };
        owner.ActiveSkillController = controller;

        controller.Execute();
        try
        {
            controller.Tick(TimeSpan.FromSeconds(2.6));

            await Assert.That(MathF.Abs(owner.Transform.Local.Position.Z - expectedLiftedZ)).IsLessThan(0.001f);
            await Assert.That(controller.State).IsEqualTo(SkillController.SCState.Running);
            await Assert.That(owner.ActiveSkillController).IsSameReferenceAs(controller);

            controller.Tick(TimeSpan.FromSeconds(30));

            await Assert.That(MathF.Abs(owner.Transform.Local.Position.Z - expectedLiftedZ)).IsLessThan(0.001f);
            await Assert.That(controller.State).IsEqualTo(SkillController.SCState.Running);
            await Assert.That(owner.ActiveSkillController).IsSameReferenceAs(controller);

            controller.End();

            await Assert.That(controller.State).IsEqualTo(SkillController.SCState.Running);
            controller.Tick(TimeSpan.FromMilliseconds(100));
            await Assert.That(owner.Transform.Local.Position.Z).IsLessThan(expectedLiftedZ);
        }
        finally
        {
            controller.End(force: true);
            ticks.Invoke();
        }
    }

    private static SkillControllerTemplate CreateFloatingTemplate(int heightMillimeters, int speedMillimetersPerSecond)
    {
        return new SkillControllerTemplate
        {
            KindId = (uint)SkillControllerKind.Floating,
            Value = [heightMillimeters, speedMillimetersPerSecond]
        };
    }

    private static TestUnit CreateOwner(ushort groundZ)
    {
        var template = new WorldTemplate
        {
            Id = 1,
            Name = "floating_skill_controller_test",
            CellX = 1,
            CellY = 1,
            HeightMaxCoefficient = 1d,
            Cells = new WorldCell[1, 1]
        };
        var heightMap = new ushort[WorldManager.CELL_HMAP_RESOLUTION, WorldManager.CELL_HMAP_RESOLUTION];
        for (var x = 0; x < heightMap.GetLength(0); x++)
        for (var y = 0; y < heightMap.GetLength(1); y++)
            heightMap[x, y] = groundZ;

        var cell = new WorldCell(0, 0, template);
        SetPrivateMember(cell, nameof(WorldCell.HeightMap), heightMap);
        SetPrivateMember(cell, nameof(WorldCell.Loaded), true);
        template.Cells[0, 0] = cell;
        template.GeoData = new AiGeoDataManager(template);

        var owner = new TestUnit { ObjId = 1, Name = "owner", DisabledSetPosition = true };
        var world = new WorldInstance(template, 1, true, 1);
        SetPrivateMember(owner, "_parentWorld", world, typeof(GameObject));
        owner.Transform.Local.SetPosition(new Vector3(0.5f, 0.5f, groundZ));
        return owner;
    }

    private static void SetPrivateMember(object target, string name, object value, Type declaringType = null)
    {
        var type = declaringType ?? target.GetType();
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is not null)
        {
            field.SetValue(target, value);
            return;
        }

        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(target, value);
    }

    private sealed class TestUnit : Unit
    {
        public override void BroadcastPacket(GamePacket packet, bool self)
        {
        }
    }
}
