using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    [Test]
    [Arguments(5773u, 14808u, 163u, 1)]
    [Arguments(5890u, 15181u, 167u, 2)]
    public async Task BondedSeat_UsesAllPlacesThenRefusesBeforeEffectsAndAllowsRetry(
        uint doodadId, uint phaseId, uint actionId, int capacity)
    {
        var (seat, template) = ConfigureSeat(doodadId, phaseId, actionId, capacity);
        var occupants = new List<ExecutionCharacter>();
        for (var i = 0; i < capacity; i++)
        {
            var rider = new ExecutionCharacter { Id = (uint)(10 + i), ObjId = (uint)(100 + i), Hp = 100, ParentWorld = seat.ParentWorld };
            var credit = 0;
            rider.Events.OnInteraction += (_, _) => credit++;
            await Assert.That(Sit(new Skill(template), rider, seat)).IsEqualTo(SkillResult.Success);
            await Assert.That(BondedSpot(rider.Bonding)).IsEqualTo(i);
            await Assert.That(rider.Transform.Parent).IsSameReferenceAs(seat.Transform);
            await Assert.That(rider.Buffs.CheckBuff(4645)).IsTrue();
            await Assert.That(credit).IsEqualTo(1);
            occupants.Add(rider);
        }

        var received = 0;
        _owner.Events.OnInteraction += (_, _) => received++;
        var session = new RankFeedbackSession();
        _owner.Connection = new GameConnection(session) { ActiveChar = _owner };
        _owner.Transform.Local.Position = new Vector3(1, 0, 0);
        var position = _owner.Transform.World.Position;
        var denied = new Skill(template);
        await Assert.That(Sit(denied, _owner, seat)).IsEqualTo(SkillResult.Failure);
        await Assert.That(denied.Cancelled).IsTrue();
        await Assert.That(denied.TlId).IsEqualTo((ushort)0);
        await Assert.That(_owner.GlobalCooldown).IsEqualTo(DateTime.MinValue);
        await Assert.That(_owner.Bonding).IsNull();
        await Assert.That(_owner.Transform.Parent).IsNull();
        await Assert.That(_owner.Transform.World.Position).IsEqualTo(position);
        await Assert.That(_owner.Buffs.CheckBuff(4645)).IsFalse();
        await Assert.That(received).IsEqualTo(0);
        await Assert.That(session.Packets.Count(IsSeatBusyError)).IsEqualTo(1);

        var leaving = occupants[0];
        var unbond = new CSUnbondDoodadPacket { Connection = new GameConnection(null) { ActiveChar = leaving } };
        unbond.Read(new PacketStream().WriteBc(leaving.ObjId).WriteBc(seat.ObjId));
        await Assert.That(leaving.Bonding).IsNull();
        await Assert.That(Sit(new Skill(template), _owner, seat)).IsEqualTo(SkillResult.Success);
        await Assert.That(BondedSpot(_owner.Bonding)).IsEqualTo(0);
        await Assert.That(_owner.Buffs.CheckBuff(4645)).IsTrue();
        await Assert.That(received).IsEqualTo(1);
        if (capacity == 2)
            await Assert.That(BondedSpot(occupants[1].Bonding)).IsEqualTo(1);
    }

    [Test]
    public async Task BondedSeat_LastPlaceTakenBeforeEffectsRefusesWithoutBuffOrCredit()
    {
        var (seat, template) = ConfigureSeat(5773, 14808, 163, 1);
        var rider = new ExecutionCharacter { Id = 10, ObjId = 100, Hp = 100, ParentWorld = seat.ParentWorld };
        // Stock seating is instant. Hold this first cast open to exercise the late completion path.
        var skill = new Skill(template) { BaseCastingTime = 1 };
        SetInstance(new TaskManager(null));
        var received = 0;
        _owner.Events.OnInteraction += (_, _) => received++;
        var session = new RankFeedbackSession();
        _owner.Connection = new GameConnection(session) { ActiveChar = _owner };

        await Assert.That(Sit(skill, _owner, seat)).IsEqualTo(SkillResult.Success);
        var pending = _owner.SkillTask;
        await Assert.That(pending).IsNotNull();
        await Assert.That(Sit(new Skill(template), rider, seat)).IsEqualTo(SkillResult.Success);
        await Task.Delay(20);
        pending.Execute();
        await Assert.That(skill.Cancelled).IsTrue();
        await Assert.That(_owner.Buffs.CheckBuff(4645)).IsFalse();
        await Assert.That(received).IsEqualTo(0);
        await Assert.That(_owner.Bonding).IsNull();
        await Assert.That(_owner.Transform.Parent).IsNull();
        await Assert.That(rider.Bonding.GetOwner()).IsSameReferenceAs(seat);
        await Assert.That(session.Packets.Count(IsSeatBusyError)).IsEqualTo(1);
    }

    [Test]
    public async Task BondedSeat_TwoRidersCompeteForTheLastPlace()
    {
        var (seat, template) = ConfigureSeat(5773, 14808, 163, 1);
        var other = new ExecutionCharacter { Id = 10, ObjId = 100, Hp = 100, ParentWorld = seat.ParentWorld };
        var firstCredits = 0;
        var secondCredits = 0;
        _owner.Events.OnInteraction += (_, _) => firstCredits++;
        other.Events.OnInteraction += (_, _) => secondCredits++;
        var results = await Task.WhenAll(Task.Run(() => Sit(new Skill(template), _owner, seat)),
            Task.Run(() => Sit(new Skill(template), other, seat)));

        await Assert.That(results.Count(result => result == SkillResult.Success)).IsEqualTo(1);
        await Assert.That(results.Count(result => result == SkillResult.Failure)).IsEqualTo(1);
        var firstWon = results[0] == SkillResult.Success;
        var winner = firstWon ? _owner : other;
        var loser = firstWon ? other : _owner;
        await Assert.That(winner.Bonding.GetOwner()).IsSameReferenceAs(seat);
        await Assert.That(winner.Buffs.CheckBuff(4645)).IsTrue();
        await Assert.That(loser.Bonding).IsNull();
        await Assert.That(loser.Buffs.CheckBuff(4645)).IsFalse();
        await Assert.That(firstCredits).IsEqualTo(firstWon ? 1 : 0);
        await Assert.That(secondCredits).IsEqualTo(firstWon ? 0 : 1);
    }

    private static int BondedSpot(BondDoodad bond)
    {
        var state = bond.Write(new PacketStream());
        state.ReadByte(); // attach point
        state.ReadBc();   // doodad
        state.ReadByte(); // bond kind
        state.ReadInt32(); // capacity
        return state.ReadInt32();
    }

    private static bool IsSeatBusyError(byte[] packet) =>
        BitConverter.ToUInt16(packet, 6) == SCOffsets.SCErrorMsgPacket &&
        BitConverter.ToInt16(packet, 8) == (short)ErrorMessageType.AlreadyInteractingSomeoneElse;

    private static SkillResult Sit(Skill skill, ExecutionCharacter rider, Doodad seat) =>
        skill.Use(rider, new SkillCasterUnit(rider.ObjId), new SkillCastDoodadTarget { ObjId = seat.ObjId }, null, false, out _);

    private (Doodad Seat, SkillTemplate Template) ConfigureSeat(uint doodadId, uint phaseId, uint actionId, int capacity)
    {
        SetInstance(new SkillRequirementsGameData());
        SetInstance(new ZoneManager(null, null));
        SetInstance(new PermissionManager(Mock.Of<IAccountManager>().Object));
        var models = new ModelManager();
        SetField(models, "_modelTypes", new Dictionary<uint, ModelType>());
        SetField(models, "_models", new Dictionary<string, Dictionary<uint, Model>>());
        SetInstance(models);
        BuffBreakerTestData.Configure(SkillManager.Instance);
        SetInstance(BuffBreakerTestData.Load());
        SetInstance(new MateGameData());
        var buff = new BuffTemplate { Id = 4645 };
        SetField(SkillManager.Instance, "_buffs", new Dictionary<uint, BuffTemplate> { [buff.Id] = buff });
        var template = new SkillTemplate
        {
            Id = 15857, TargetType = SkillTargetType.Doodad, DefaultGcd = true, MaxRange = 4, CancelOngoingBuffs = true,
            Effects =
            [
                new SkillEffect { ApplicationMethod = SkillEffectApplicationMethod.SourceOnce, Chance = 100, EndLevel = 99,
                    Template = new BuffEffect { Id = 8029, Chance = 100, Buff = buff } },
                new SkillEffect { ApplicationMethod = SkillEffectApplicationMethod.Target, Chance = 100, EndLevel = 99,
                    Template = new InteractionEffect { Id = 3912, WorldInteraction = WorldInteractionType.Use } }
            ]
        };
        SetField(SkillManager.Instance, "_skills", new Dictionary<uint, SkillTemplate> { [template.Id] = template });
        var manager = new DoodadManager(null, null, null, null, null);
        var action = new DoodadFuncAttachment { Id = actionId, Space = capacity, AttachPointId = AttachPointKind.Passenger0,
            BondKindId = capacity == 1 ? BondKind.BondChairSingle : BondKind.BondChairDouble };
        SetField(manager, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>>
        {
            [phaseId] = [new() { GroupId = phaseId, SkillId = template.Id, FuncId = actionId, FuncType = nameof(DoodadFuncAttachment) }]
        });
        SetField(manager, "_funcTemplates", new Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>
            { [nameof(DoodadFuncAttachment)] = new() { [actionId] = action } });
        SetField(manager, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>());
        SetInstance(manager);

        var world = new WorldInstance(new WorldTemplate { Id = 10 }, 0, true, 0);
        var worlds = new WorldManager(null, null, null, null, null);
        SetField(worlds, "_worlds", new ConcurrentDictionary<uint, WorldInstance> { [world.Id] = world });
        SetInstance(worlds);
        _owner.ParentWorld = world;
        var seat = new Doodad { ObjId = 800, TemplateId = doodadId, Template = new DoodadTemplate { Id = doodadId, OnceOneMan = true },
            FuncGroupId = phaseId, ParentWorld = world };
        var units = (ConcurrentDictionary<uint, BaseUnit>)typeof(WorldInstance)
            .GetField("_baseUnits", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
        units[seat.ObjId] = seat;
        return (seat, template);
    }
}
