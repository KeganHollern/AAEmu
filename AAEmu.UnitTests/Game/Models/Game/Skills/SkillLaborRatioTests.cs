using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task RatioPhase_CommitsSelectionAndNotificationsOrRestoresBothRatioFields(bool succeeds)
    {
        var doodad = CreateRatioLaborDoodad(
            new DoodadFuncRatioChange { Id = 1, Ratio = 4000, NextPhase = 11 },
            new DoodadFuncRatioChange { Id = 2, Ratio = 6000, NextPhase = 12 });
        var callbacks = new List<uint>();
        doodad.ParentWorld.DoodadPhaseChanged += (_, phase) => callbacks.Add(phase);
        (uint Phase, int Roll, int Cumulative, int Packets, int Callbacks) prepared = default;
        var skill = NewSkill();
        skill.CommitLaborBatch = (_, _) =>
        {
            prepared = (doodad.FuncGroupId, doodad.PhaseRatio, doodad.CumulativePhaseRatio,
                doodad.Packets.Count, callbacks.Count);
            return succeeds;
        };

        var result = SkillLaborBatch.Run(_owner, skill, true, () => doodad.DoChangePhase(_owner, 10));

        await Assert.That(result).IsEqualTo(succeeds);
        await Assert.That(prepared).IsEqualTo((12u, 6000, 10000, 0, 0));
        await Assert.That(doodad.RollCalls).IsEqualTo(1);
        await Assert.That(doodad.FuncGroupId).IsEqualTo(succeeds ? 12u : 1u);
        await Assert.That(doodad.PhaseRatio).IsEqualTo(succeeds ? 6000 : 123);
        await Assert.That(doodad.CumulativePhaseRatio).IsEqualTo(succeeds ? 10000 : 456);
        await Assert.That(_owner.LaborPower).IsEqualTo(succeeds ? (short)10 : (short)20);
        if (succeeds)
        {
            await Assert.That(callbacks.SequenceEqual(new uint[] { 12 })).IsTrue();
            var packet = doodad.Packets.Single();
            await Assert.That(packet).IsTypeOf<SCDoodadPhaseChangedPacket>();
            var body = new PacketStream(packet.Write(new PacketStream()).GetBytes());
            await Assert.That(body.ReadBc()).IsEqualTo(doodad.ObjId);
            await Assert.That(body.ReadUInt32()).IsEqualTo(12u);
        }
        else
        {
            await Assert.That(callbacks).IsEmpty();
            await Assert.That(doodad.Packets).IsEmpty();
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task RatioRespawn_ReplacesTheSelectedDoodadOnlyAfterLaborCommit(bool succeeds)
    {
        var doodad = CreateRatioLaborDoodad(
            new DoodadFuncRatioRespawn { Id = 1, Ratio = 4000, SpawnDoodadId = 3084 },
            new DoodadFuncRatioRespawn { Id = 2, Ratio = 6000, SpawnDoodadId = 3085 });
        var spawner = new RatioLaborSpawner { Id = 10, UnitId = doodad.TemplateId };
        doodad.Spawner = spawner;
        var callbacks = new List<uint>();
        doodad.ParentWorld.DoodadPhaseChanged += (_, phase) => callbacks.Add(phase);
        (int Calls, uint Replacement, int Roll, int Cumulative) prepared = default;
        var skill = NewSkill();
        skill.CommitLaborBatch = (_, _) =>
        {
            prepared = (spawner.Calls.Count, spawner.RespawnDoodadTemplateId,
                doodad.PhaseRatio, doodad.CumulativePhaseRatio);
            return succeeds;
        };

        var result = SkillLaborBatch.Run(_owner, skill, true, () => doodad.DoChangePhase(_owner, 10));

        await Assert.That(result).IsEqualTo(succeeds);
        await Assert.That(prepared).IsEqualTo((0, 0u, 6000, 10000));
        await Assert.That(doodad.RollCalls).IsEqualTo(1);
        await Assert.That(doodad.FuncGroupId).IsEqualTo(succeeds ? 10u : 1u);
        await Assert.That(doodad.PhaseRatio).IsEqualTo(succeeds ? 6000 : 123);
        await Assert.That(doodad.CumulativePhaseRatio).IsEqualTo(succeeds ? 10000 : 456);
        await Assert.That(_owner.LaborPower).IsEqualTo(succeeds ? (short)10 : (short)20);
        await Assert.That(doodad.Packets).IsEmpty();
        await Assert.That(callbacks).IsEmpty();
        if (succeeds)
        {
            await Assert.That(spawner.Despawned).IsSameReferenceAs(doodad);
            await Assert.That(spawner.Calls.SequenceEqual(new[] { "despawn", "spawn:3085" })).IsTrue();
        }
        else
        {
            await Assert.That(spawner.Despawned).IsNull();
            await Assert.That(spawner.Calls).IsEmpty();
            await Assert.That(spawner.RespawnDoodadTemplateId).IsEqualTo(0u);
            // A failed transaction must also clear its temporary deletion marker.
            // Otherwise this normal phase notification is silently suppressed.
            doodad.DoChangePhase(_owner, 1);
            await Assert.That(doodad.Packets.Count).IsEqualTo(1);
            await Assert.That(callbacks.SequenceEqual(new uint[] { 1 })).IsTrue();
        }
    }

    private RatioLaborDoodad CreateRatioLaborDoodad(params DoodadPhaseFuncTemplate[] functions)
    {
        var manager = new DoodadManager(Mock.Of<IObjectIdManager>().Object, Mock.Of<IDoodadIdManager>().Object,
            ItemManager.Instance, new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ISusManager>().Object);
        SetInstance(manager);
        SetField(manager, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>>());
        SetField(manager, "_templates", new Dictionary<uint, DoodadTemplate>
        {
            [3084] = new() { Id = 3084 },
            [3085] = new() { Id = 3085 }
        });
        SetField(manager, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>
        {
            [10] = functions.Select(function => new DoodadPhaseFunc
            {
                GroupId = 10, FuncId = function.Id, FuncType = function.GetType().Name
            }).ToList()
        });
        SetField(manager, "_phaseFuncTemplates", functions.GroupBy(function => function.GetType().Name)
            .ToDictionary(group => group.Key, group => group.ToDictionary(function => function.Id)));
        var doodad = new RatioLaborDoodad
        {
            ObjId = 1234, TemplateId = 2768, Template = new DoodadTemplate { Id = 2768 },
            FuncGroupId = 1, CumulativePhaseRatio = 456
        };
        typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(doodad, new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1));
        typeof(Doodad).GetProperty(nameof(Doodad.PhaseRatio))!.SetValue(doodad, 123);
        return doodad;
    }

    private sealed class RatioLaborDoodad : Doodad
    {
        public List<GamePacket> Packets { get; } = [];
        public int RollCalls { get; private set; }

        protected override int RollPhaseRatio()
        {
            RollCalls++;
            return 6000;
        }

        public override void BroadcastPacket(GamePacket packet, bool self) => Packets.Add(packet);
    }

    private sealed class RatioLaborSpawner : DoodadSpawner
    {
        public List<string> Calls { get; } = [];
        public Doodad Despawned { get; private set; }

        public override void Despawn(Doodad doodad)
        {
            Calls.Add("despawn");
            Despawned = doodad;
        }

        public override Doodad Spawn(uint objId)
        {
            Calls.Add($"spawn:{RespawnDoodadTemplateId}");
            return new Doodad { TemplateId = RespawnDoodadTemplateId };
        }
    }
}
