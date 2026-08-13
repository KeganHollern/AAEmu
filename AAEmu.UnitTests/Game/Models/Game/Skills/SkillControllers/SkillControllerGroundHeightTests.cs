using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills.SkillControllers;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Skills.SkillControllers;

public class SkillControllerGroundHeightTests
{
    [Test]
    public async Task TryGetOwnerGroundHeight_ResolvedSeaLevel_ReturnsValidZero()
    {
        var owner = CreateOwner(new Npc(), 0, 0.5f);
        var controller = new TestSkillController(owner);

        var found = controller.TryGetGroundHeight(out var height);
        var snapped = controller.SnapToGround(owner.Transform.Local.Position.Z, 1f);

        await Assert.That(found).IsTrue();
        await Assert.That(height).IsEqualTo(0f);
        await Assert.That(snapped).IsTrue();
        await Assert.That(owner.Transform.Local.Position.Z).IsEqualTo(0f);
    }

    [Test]
    public async Task TryGetOwnerGroundHeight_FlyingNpc_DoesNotResolveOrSnap()
    {
        const float candidateZ = 10.5f;
        var owner = CreateOwner(new Npc { CanFly = true }, 10, candidateZ);
        var controller = new TestSkillController(owner);

        var found = controller.TryGetGroundHeight(out var height);
        var snapped = controller.SnapToGround(candidateZ, 1f);

        await Assert.That(found).IsFalse();
        await Assert.That(height).IsEqualTo(0f);
        await Assert.That(snapped).IsFalse();
        await Assert.That(owner.Transform.Local.Position.Z).IsEqualTo(candidateZ);
    }

    [Test]
    public async Task TryGetOwnerGroundHeight_NonNpcUnit_ResolvesAndSnaps()
    {
        const float candidateZ = 10.5f;
        var owner = CreateOwner(new Unit(), 10, candidateZ);
        var controller = new TestSkillController(owner);

        var found = controller.TryGetGroundHeight(out var height);
        var snapped = controller.SnapToGround(candidateZ, 1f);

        await Assert.That(found).IsTrue();
        await Assert.That(height).IsEqualTo(10f);
        await Assert.That(snapped).IsTrue();
        await Assert.That(owner.Transform.Local.Position.Z).IsEqualTo(10f);
    }

    [Test]
    public async Task TrySnapOwnerToGround_UnavailableGround_PreservesCandidate()
    {
        const float candidateZ = 10.5f;
        var owner = CreateOwnerWithoutTerrain(new Npc(), candidateZ);
        var controller = new TestSkillController(owner);

        var found = controller.TryGetGroundHeight(out _);
        var snapped = controller.SnapToGround(candidateZ, 1f);

        await Assert.That(found).IsFalse();
        await Assert.That(snapped).IsFalse();
        await Assert.That(owner.Transform.Local.Position.Z).IsEqualTo(candidateZ);
    }

    [Test]
    public async Task TrySnapOwnerToGround_HeightDifferenceEqualsTolerance_PreservesCandidate()
    {
        const float candidateZ = 11f;
        var owner = CreateOwner(new Npc(), 10, candidateZ);
        var controller = new TestSkillController(owner);

        var snapped = controller.SnapToGround(candidateZ, 1f);

        await Assert.That(snapped).IsFalse();
        await Assert.That(owner.Transform.Local.Position.Z).IsEqualTo(candidateZ);
    }

    [Test]
    public async Task TrySnapOwnerToGround_NaNCandidate_PreservesFiniteOwnerHeight()
    {
        const float ownerZ = 10.5f;
        var owner = CreateOwner(new Npc(), 10, ownerZ);
        var controller = new TestSkillController(owner);

        var snapped = controller.SnapToGround(float.NaN, 1f);

        await Assert.That(snapped).IsFalse();
        await Assert.That(owner.Transform.Local.Position.Z).IsEqualTo(ownerZ);
    }

    private static T CreateOwner<T>(T owner, ushort groundZ, float candidateZ) where T : Unit
    {
        var template = CreateWorldTemplate();
        var heightMap = new ushort[WorldManager.CELL_HMAP_RESOLUTION, WorldManager.CELL_HMAP_RESOLUTION];
        for (var x = 0; x < heightMap.GetLength(0); x++)
        for (var y = 0; y < heightMap.GetLength(1); y++)
            heightMap[x, y] = groundZ;

        var cell = new WorldCell(0, 0, template);
        SetPrivateMember(cell, nameof(WorldCell.HeightMap), heightMap);
        SetPrivateMember(cell, nameof(WorldCell.Loaded), true);
        template.Cells[0, 0] = cell;

        return AttachToWorld(owner, template, candidateZ);
    }

    private static T CreateOwnerWithoutTerrain<T>(T owner, float candidateZ) where T : Unit
    {
        return AttachToWorld(owner, CreateWorldTemplate(), candidateZ);
    }

    private static WorldTemplate CreateWorldTemplate()
    {
        var template = new WorldTemplate
        {
            Id = 1,
            Name = "skill_controller_ground_height_test",
            CellX = 1,
            CellY = 1,
            HeightMaxCoefficient = 1d,
            Cells = new WorldCell[1, 1]
        };
        template.GeoData = new AiGeoDataManager(template);
        return template;
    }

    private static T AttachToWorld<T>(T owner, WorldTemplate template, float candidateZ) where T : Unit
    {
        var world = new WorldInstance(template, 1, true, 1);
        SetPrivateMember(owner, "_parentWorld", world, typeof(GameObject));
        owner.Transform.Local.SetPosition(new Vector3(0.5f, 0.5f, candidateZ));
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

    private sealed class TestSkillController : SkillController
    {
        public TestSkillController(Unit owner)
        {
            Owner = owner;
        }

        public bool TryGetGroundHeight(out float height)
        {
            return TryGetOwnerGroundHeight(out height);
        }

        public bool SnapToGround(float candidateZ, float tolerance)
        {
            return TrySnapOwnerToGround(candidateZ, tolerance);
        }
    }
}
