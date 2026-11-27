using System.Numerics;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Tasks.Skills;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    [Test]
    [Arguments("complete")]
    [Arguments("blocked")]
    [Arguments("too-far")]
    [Arguments("other-owner")]
    [Arguments("no-cart")]
    [Arguments("moved-during-cast")]
    [Arguments("blocked-at-finish")]
    [Arguments("blocked-at-effect")]
    [Arguments("recalled-during-cast")]
    public async Task RidersEscape_MovesOnlyTheOwnersOriginalCartAtAValidDestination(string action)
    {
        SetInstance(new TaskManager(null));
        SetInstance(new PermissionManager(Mock.Of<IAccountManager>().Object));
        SetInstance(new SkillRequirementsGameData());
        var models = new ModelManager();
        SetField(models, "_modelTypes", new Dictionary<uint, ModelType>());
        SetField(models, "_models", new Dictionary<string, Dictionary<uint, Model>>());
        SetInstance(models);
        var (world, cart, scroll) = PrepareVehiclePlacementBoundary();
        world.AddObject(_owner);
        cart.Template = new SlaveTemplate
        {
            Id = 555, ModelId = 8000009, SlaveKind = SlaveKind.Machine,
            SpawnXOffset = 5, SpawnYOffset = 5
        };
        cart.TemplateId = cart.Template.Id;
        cart.ModelId = cart.Template.ModelId;
        cart.Transform.Local.SetPosition(action == "too-far" ? 150 : 101, 104, 10);
        if (action == "other-owner")
            cart.Summoner = null;
        if (action == "no-cart")
            world.RemoveObject(cart);
        var before = cart.Transform.World.Position;
        // The normal client chose a point 7 m in front of the owner and supplied the yaw.
        var selected = new SkillCastPositionTarget { PosX = 100, PosY = 107, PosZ = 10, PosRot = MathF.PI / 2 };
        var geometryCalls = 0;
        world.SlaveManager.PlacementGeometry = (_, _, _) =>
        {
            geometryCalls++;
            var blocked = action == "blocked" || action == "blocked-at-finish" && geometryCalls == 2 ||
                action == "blocked-at-effect" && geometryCalls == 3;
            return blocked ? ErrorMessageType.SlaveSpawnErrorInvalidArea : ErrorMessageType.NoErrorMessage;
        };
        var template = CreateOwnerEscapeTemplate();
        var skill = new Skill(template);
        var session = new RankFeedbackSession();
        _owner.Connection = new GameConnection(session) { ActiveChar = _owner };
        var result = skill.Use(_owner, new SkillCasterUnit(_owner.ObjId), selected, new SkillObject(), true, out _);
        if (action is "blocked" or "too-far" or "other-owner" or "no-cart")
        {
            await Assert.That(result).IsNotEqualTo(SkillResult.Success);
            await Assert.That(_owner.SkillTask).IsNull();
            await Assert.That(skill.Cancelled).IsTrue();
            await Assert.That(_owner.Cooldowns.CheckCooldown(template)).IsFalse();
            await Assert.That(cart.Transform.World.Position).IsEqualTo(before);
            return;
        }

        await Assert.That(result).IsEqualTo(SkillResult.Success);
        var cast = (CastTask)_owner.SkillTask;
        if (action == "moved-during-cast")
            _owner.Transform.Local.SetPosition(160, 100, 10);
        if (action == "recalled-during-cast")
            world.RemoveObject(cart);
        cast.CastWindow = new CastWindow(DateTime.UtcNow.AddSeconds(-1), true, false, false);
        cast.Execute();
        cast.Execute();
        await Assert.That(skill.Cancelled).IsEqualTo(action != "complete");
        await Assert.That(_owner.Cooldowns.CheckCooldown(template)).IsEqualTo(action == "complete");
        var resets = session.Packets.Where(packet => BitConverter.ToUInt16(packet, 6) == SCOffsets.SCSkillCooldownResetPacket).ToArray();
        await Assert.That(resets.Length).IsEqualTo(action == "blocked-at-effect" ? 1 : 0);
        if (action == "blocked-at-effect")
        {
            var reset = new PacketStream(resets.Single()[8..]);
            await Assert.That(reset.ReadBc()).IsEqualTo(_owner.ObjId);
            await Assert.That(reset.ReadUInt32()).IsEqualTo(template.Id);
            await Assert.That(reset.ReadUInt32()).IsEqualTo(0u);
            await Assert.That(reset.ReadBoolean()).IsFalse();
        }
        var expected = action == "complete" ? new Vector3(100, 107, 10) : before;
        await Assert.That(cart.Transform.World.Position).IsEqualTo(expected);
        await Assert.That(cart.SummoningItem).IsSameReferenceAs(scroll);
        if (action == "complete")
        {
            await Assert.That(geometryCalls).IsEqualTo(3);
            await Assert.That(world.GetAllSlaves().Single()).IsSameReferenceAs(cart);
            await Assert.That(cart.Transform.World.Rotation.Z).IsEqualTo(selected.PosRot);
            await Assert.That(cart.TlId).IsEqualTo((ushort)30);
            await Assert.That(cart.Id).IsEqualTo(77u);
            await Assert.That(scroll.SlaveDbId).IsEqualTo(cart.Id);
        }
    }

    private static SkillTemplate CreateOwnerEscapeTemplate()
    {
        var template = new SkillTemplate
        {
            Id = 22143, TargetType = SkillTargetType.SummonPos, MaxRange = 4,
            CastingTime = 10000, CooldownTime = 1800000
        };
        template.Effects.Add(new SkillEffect
        {
            ApplicationMethod = SkillEffectApplicationMethod.SourceOnce, Chance = 100, EndLevel = byte.MaxValue,
            Template = new SpecialEffect { SpecialEffectTypeId = SpecialType.EscapeMySlave }
        });
        return template;
    }
}
