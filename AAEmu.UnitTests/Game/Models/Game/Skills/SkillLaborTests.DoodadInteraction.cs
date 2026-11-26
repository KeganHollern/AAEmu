using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Tasks.Skills;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    private (Doodad sign, ExecutionCharacter other, RankFeedbackSession session) SetUpSign(bool singleUser)
    {
        SetInstance(new TaskManager(null));
        SetInstance(new PermissionManager(Mock.Of<IAccountManager>().Object));
        SetInstance(new SkillRequirementsGameData());
        SetInstance(new ZoneManager(null, null));
        var models = new ModelManager();
        SetField(models, "_modelTypes", new Dictionary<uint, ModelType>());
        SetField(models, "_models", new Dictionary<string, Dictionary<uint, Model>>());
        SetInstance(models);
        var doodads = new DoodadManager(null, null, null, null, null);
        SetField(SkillManager.Instance, "_skills", new Dictionary<uint, SkillTemplate> { [21427] = ReadSign().Template });
        SetField(doodads, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>());
        var group = singleUser ? 17186u : 16861u;
        var paper = singleUser ? 107u : 104u;
        SetField(doodads, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>>
        {
            [group] = [new() { SkillId = 21427, GroupId = group, FuncId = paper, FuncType = nameof(DoodadFuncOpenPaper), NextPhase = -1 }]
        });
        SetField(doodads, "_funcTemplates", new Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>
        {
            [nameof(DoodadFuncOpenPaper)] = new() { [paper] = new DoodadFuncOpenPaper() }
        });
        SetInstance(doodads);
        var sign = new Doodad
        {
            ObjId = 80, TemplateId = singleUser ? 6486u : 6372u, IsVisible = true, FuncGroupId = group,
            Template = new DoodadTemplate { Id = singleUser ? 6486u : 6372u, OnceOneMan = singleUser, OnceOneInteraction = true }
        };
        var other = new ExecutionCharacter { Id = 8, ObjId = 71, Name = "Second", Hp = 100 };
        other.InitializeLaborCache(20, DateTime.UtcNow);
        var session = new RankFeedbackSession();
        other.Connection = new GameConnection(session) { ActiveChar = other };
        var world = new WorldInstance(new WorldTemplate { Id = 10 }, 0, true, 0);
        var worldField = typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!;
        worldField.SetValue(sign, world);
        foreach (var player in new[] { _owner, other })
        {
            worldField.SetValue(player, world);
            player.IsVisible = true;
            player.Level = 1;
            typeof(Character).GetField("<IsOnline>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(player, true);
        }
        var objects = (ConcurrentDictionary<uint, BaseUnit>)typeof(WorldInstance)
            .GetField("_baseUnits", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(world)!;
        objects[sign.ObjId] = sign;
        return (sign, other, session);
    }

    private static Skill ReadSign(int effectDelay = 0)
    {
        var skill = new Skill(new SkillTemplate { Id = 21427, TargetType = SkillTargetType.Doodad, MaxRange = 3,
            CastingTime = 5000, EffectDelay = effectDelay, CustomGcd = 1000 });
        skill.Template.Effects.Add(TargetUse(3679));
        return skill;
    }

    private static SkillEffect TargetUse(uint id) => new()
    {
        ApplicationMethod = SkillEffectApplicationMethod.Target, Chance = 100, StartLevel = 1, EndLevel = 99,
        Friendly = true, NonFriendly = true, Template = new InteractionEffect { Id = id, WorldInteraction = WorldInteractionType.Use }
    };

    private static SkillResult StartReading(Skill skill, Character who, Doodad sign, bool bypassGcd = true) =>
        skill.Use(who, new SkillCasterUnit(who.ObjId), new SkillCastDoodadTarget { ObjId = sign.ObjId }, null, bypassGcd, out _);

    private (Doodad tome, ExecutionCharacter other, RankFeedbackSession session, SkillTemplate binding)
        SetUpMemoryTome(bool singleUser)
    {
        var (tome, other, session) = SetUpSign(singleUser);
        tome.TemplateId = tome.Template.Id = singleUser ? 7425u : 5041u;
        tome.FuncGroupId = singleUser ? 20340u : 13066u;
        var binding = new SkillTemplate
        {
            Id = 14602, TargetType = SkillTargetType.Doodad, MaxRange = 4, CastingTime = 3000
        };
        binding.Effects.Add(TargetUse(1266));
        SetField(SkillManager.Instance, "_skills", new Dictionary<uint, SkillTemplate> { [binding.Id] = binding });
        var function = new DoodadFunc
        {
            GroupId = tome.FuncGroupId, FuncId = singleUser ? 132u : 102u, FuncType = nameof(DoodadFuncBinding),
            SkillId = binding.Id, NextPhase = singleUser ? 20346 : 13067
        };
        SetField(DoodadManager.Instance, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>> { [tome.FuncGroupId] = [function] });
        SetField(DoodadManager.Instance, "_funcTemplates", new Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>
        {
            [nameof(DoodadFuncBinding)] = new() { [function.FuncId] = new DoodadFuncBinding { DistrictId = singleUser ? 367u : 318u } }
        });
        return (tome, other, session, binding);
    }

    [Test]
    public async Task DoodadCast_MemoryTomeRejectsSecondBindingUntilFirstCastStops()
    {
        var (tome, other, session, binding) = SetUpMemoryTome(true);
        await Assert.That(StartReading(new Skill(binding), _owner, tome)).IsEqualTo(SkillResult.Success);
        await Assert.That(StartReading(new Skill(binding), other, tome)).IsEqualTo(SkillResult.Failure);
        var error = session.Packets.Single(packet => BitConverter.ToUInt16(packet, 6) == SCOffsets.SCErrorMsgPacket);
        await Assert.That(BitConverter.ToInt16(error, 8)).IsEqualTo((short)ErrorMessageType.AlreadyInteractingSomeoneElse);
        await Assert.That(other.SkillTask).IsNull();
        _owner.InterruptSkills();
        await Assert.That(StartReading(new Skill(binding), other, tome)).IsEqualTo(SkillResult.Success);
    }

    [Test]
    public async Task DoodadCast_SharedMemoryTomeAcceptsBothBindings()
    {
        var (tome, other, _, binding) = SetUpMemoryTome(false);
        await Assert.That(StartReading(new Skill(binding), _owner, tome)).IsEqualTo(SkillResult.Success);
        await Assert.That(StartReading(new Skill(binding), other, tome)).IsEqualTo(SkillResult.Success);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DoodadCast_SingleUserBuffAndPickupReserveOnlyTheirAuthoredSkill(bool pickup)
    {
        var (target, other, _) = SetUpSign(true);
        target.TemplateId = target.Template.Id = pickup ? 6271u : 6075u;
        target.FuncGroupId = pickup ? 16601u : 15780u;
        var template = new SkillTemplate
        {
            Id = pickup ? 21112u : 20420u, TargetType = SkillTargetType.Doodad,
            MaxRange = 3, CastingTime = pickup ? 1000 : 1700
        };
        var effect = TargetUse(pickup ? 3609u : 3392u);
        ((InteractionEffect)effect.Template).WorldInteraction = pickup ? WorldInteractionType.RecoverItem : WorldInteractionType.Use;
        template.Effects.Add(effect);
        var func = new DoodadFunc
        {
            SkillId = template.Id, GroupId = target.FuncGroupId,
            FuncType = pickup ? nameof(DoodadFuncRecoverItem) : nameof(DoodadFuncBuff),
            FuncId = pickup ? 636u : 8u, NextPhase = pickup ? -1 : 15781
        };
        SetField(DoodadManager.Instance, "_funcsByGroups",
            new Dictionary<uint, List<DoodadFunc>> { [target.FuncGroupId] = [func] });
        await Assert.That(StartReading(new Skill(template), _owner, target)).IsEqualTo(SkillResult.Success);
        await Assert.That(StartReading(new Skill(template), other, target)).IsEqualTo(SkillResult.Failure);
        _owner.InterruptSkills();
        await Assert.That(StartReading(new Skill(template), other, target)).IsEqualTo(SkillResult.Success);
        other.InterruptSkills();

        func.SkillId = 0;
        await Assert.That(StartReading(new Skill(template), _owner, target)).IsEqualTo(SkillResult.Success);
        await Assert.That(StartReading(new Skill(template), other, target)).IsEqualTo(SkillResult.Success);
    }

    [Test]
    public async Task DoodadCast_SecondReaderGetsNativeErrorBeforeCastAndCanRetryAfterCancellation()
    {
        var (sign, other, session) = SetUpSign(true);
        var first = ReadSign();
        await Assert.That(StartReading(first, _owner, sign)).IsEqualTo(SkillResult.Success);
        var mana = other.Mp;
        var gcd = other.GlobalCooldown;
        await Assert.That(StartReading(ReadSign(), other, sign, false)).IsEqualTo(SkillResult.Failure);
        var error = session.Packets.Single(packet => BitConverter.ToUInt16(packet, 6) == SCOffsets.SCErrorMsgPacket);
        await Assert.That(BitConverter.ToInt16(error, 8)).IsEqualTo((short)ErrorMessageType.AlreadyInteractingSomeoneElse);
        await Assert.That(other.SkillTask).IsNull();
        await Assert.That(other.GlobalCooldown).IsEqualTo(gcd);
        await Assert.That(other.Mp).IsEqualTo(mana);
        await Assert.That(other.LaborPower).IsEqualTo((short)20);
        _owner.InterruptSkills();
        await Assert.That(StartReading(ReadSign(), other, sign, false)).IsEqualTo(SkillResult.Success);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DoodadCast_HoldsThroughDelayedEffectAndReleasesAfterCompletionOrLogout(bool logout)
    {
        var (sign, other, _) = SetUpSign(true);
        var skill = ReadSign(100);
        var completions = 0;
        var interactions = new List<OnInteractionArgs>();
        skill.Callback = () => completions++;
        _owner.Events.OnInteraction += (_, args) => interactions.Add(args);
        await Assert.That(StartReading(skill, _owner, sign)).IsEqualTo(SkillResult.Success);
        var cast = (CastTask)_owner.SkillTask;
        cast.CastWindow = new CastWindow(DateTime.UtcNow.AddSeconds(-1), true, false, false);
        cast.Execute();
        await Assert.That(_owner.SkillTask).IsNull();
        await Assert.That(StartReading(ReadSign(), other, sign)).IsEqualTo(SkillResult.Failure);
        if (logout)
        {
            typeof(Character).GetField("<IsOnline>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_owner, false);
            await Assert.That(StartReading(ReadSign(), other, sign)).IsEqualTo(SkillResult.Success);
        }
        var queue = (ConcurrentDictionary<uint, AAEmu.Game.Models.Tasks.Task>)typeof(TaskManager)
            .GetField("_queue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(TaskManager.Instance)!;
        var apply = queue.Values.OfType<ApplySkillTask>().Single();
        apply.Execute();
        apply.Execute();
        await Assert.That(completions).IsEqualTo(1);
        await Assert.That(_owner.Packets.OfType<SCSkillEndedPacket>().Count()).IsEqualTo(1);
        await Assert.That(interactions.Count).IsEqualTo(logout ? 0 : 1);
        if (!logout)
            await Assert.That(interactions.Single().DoodadId).IsEqualTo(sign.TemplateId);
        await Assert.That(skill.Cancelled).IsEqualTo(logout);
        if (logout)
            await Assert.That(sign.InteractionCasts.Single()).IsSameReferenceAs(other.SkillTask.Skill);
        else
            await Assert.That(StartReading(ReadSign(), other, sign)).IsEqualTo(SkillResult.Success);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DoodadCast_SharedTemplateAndMultipleSeatsStillAllowBothReaders(bool multipleSeats)
    {
        var (sign, other, _) = SetUpSign(multipleSeats);
        sign.Template.HasMultipleSeats = multipleSeats;
        await Assert.That(StartReading(ReadSign(), _owner, sign)).IsEqualTo(SkillResult.Success);
        await Assert.That(StartReading(ReadSign(), other, sign)).IsEqualTo(SkillResult.Success);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DoodadCast_StopAndFireAtTheDeadlineEndOnce(bool stopFirst)
    {
        var (sign, other, _) = SetUpSign(true);
        var skill = ReadSign();
        var completions = 0;
        var interactions = 0;
        skill.Callback = () => completions++;
        _owner.Events.OnInteraction += (_, _) => interactions++;
        await Assert.That(StartReading(skill, _owner, sign)).IsEqualTo(SkillResult.Success);
        // Both callbacks already have the skill reference at this point.
        var source = new SkillCasterUnit(_owner.ObjId);
        var target = new SkillCastDoodadTarget { ObjId = sign.ObjId };
        if (stopFirst)
            skill.Stop(_owner);
        skill.Cast(_owner, source, sign, target, new SkillObject());
        if (!stopFirst)
            skill.Stop(_owner);
        await Assert.That(completions).IsEqualTo(1);
        await Assert.That(_owner.Packets.OfType<SCSkillEndedPacket>().Count()).IsEqualTo(1);
        await Assert.That(_owner.Packets.OfType<SCSkillFiredPacket>().Count()).IsEqualTo(stopFirst ? 0 : 1);
        await Assert.That(interactions).IsEqualTo(stopFirst ? 0 : 1);
        await Assert.That(StartReading(ReadSign(), other, sign)).IsEqualTo(SkillResult.Success);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DoodadCast_QueuedChildKeepsTheHoldOnlyAfterCommit(bool commit)
    {
        var (lamp, other, _) = SetUpSign(true);
        var subZones = Mock.Of<ISubZoneManager>();
        subZones.GetSubZoneByPosition(Any<WorldTemplate>(), Any<Vector3>()).Returns([]);
        SetInstance(new PublicFarmManager(null, null, subZones.Object));
        var light = new SkillTemplate { Id = 11621, CastingTime = 1400, MaxRange = 3, TargetType = SkillTargetType.Doodad };
        light.Effects.Add(TargetUse(163));
        SetField(SkillManager.Instance, "_skills", new Dictionary<uint, SkillTemplate> { [light.Id] = light });
        var func = new DoodadFunc { GroupId = 20101, FuncId = 4036, FuncType = nameof(DoodadFuncUse), NextPhase = 20102 };
        SetField(DoodadManager.Instance, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>> { [20101] = [func] });
        SetField(DoodadManager.Instance, "_funcTemplates", new Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>
        {
            [nameof(DoodadFuncUse)] = new() { [4036] = new DoodadFuncUse { SkillId = light.Id } }
        });
        lamp.Template.Id = 7361;
        lamp.FuncGroupId = 20101;
        var parent = NewSkill();
        parent.CommitLaborBatch = (_, _) => commit;
        await Assert.That(SkillLaborBatch.Run(_owner, parent, true, () => lamp.DoFunc(_owner, 0, func))).IsEqualTo(commit);
        if (commit)
        {
            await Assert.That(StartReading(new Skill(light), other, lamp)).IsEqualTo(SkillResult.Failure);
            var queue = (ConcurrentDictionary<uint, AAEmu.Game.Models.Tasks.Task>)typeof(TaskManager)
                .GetField("_queue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(TaskManager.Instance)!;
            queue.Values.OfType<UseSkillTask>().Single().Execute();
            await Assert.That(_owner.SkillTask.Skill.Id).IsEqualTo(light.Id);
            await Assert.That(StartReading(new Skill(light), other, lamp)).IsEqualTo(SkillResult.Failure);
            _owner.InterruptSkills();
        }
        else
            await Assert.That(TaskManager.Instance.GetQueueCount()).IsEqualTo(0);
        await Assert.That(StartReading(new Skill(light), other, lamp)).IsEqualTo(SkillResult.Success);
    }

    [Test]
    public async Task DoodadCast_TwoSimultaneousReadersAdmitExactlyOne()
    {
        var (sign, other, _) = SetUpSign(true);
        using var start = new Barrier(2);
        Task<SkillResult> ReadTogether(Character reader) => Task.Run(() =>
        {
            start.SignalAndWait();
            return StartReading(ReadSign(), reader, sign);
        });
        var results = await Task.WhenAll(ReadTogether(_owner), ReadTogether(other));
        await Assert.That(results.Count(result => result == SkillResult.Success)).IsEqualTo(1);
        await Assert.That(results.Count(result => result == SkillResult.Failure)).IsEqualTo(1);
        await Assert.That(sign.InteractionCasts.Count).IsEqualTo(1);
    }
}
