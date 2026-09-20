using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task MiningCompletion_PreservesTheBrokenVeinPhaseOnlyAfterCommit(bool succeeds)
    {
        var doodad = CreateMiningDoodad();
        var callbacks = new List<uint>();
        doodad.ParentWorld.DoodadPhaseChanged += (current, phase) =>
        {
            // World subscribers still observe the final committed state, not a replayed historical state.
            callbacks.Add(phase);
            callbacks.Add(current.FuncGroupId);
        };
        var emittedBeforeCommit = -1;
        var skill = NewSkill();
        skill.CommitLaborBatch = (_, _) =>
        {
            emittedBeforeCommit = doodad.Phases.Count + callbacks.Count;
            return succeeds;
        };

        var previousWorldConfig = AppConfiguration.Instance.World;
        bool result;
        try
        {
            AppConfiguration.Instance.World = new WorldConfig { LootRate = 1 };
            result = SkillLaborBatch.Run(_owner, skill, true, () => doodad.Use(_owner, 13986));
        }
        finally
        {
            AppConfiguration.Instance.World = previousWorldConfig;
        }

        await Assert.That(result).IsEqualTo(succeeds);
        await Assert.That(emittedBeforeCommit).IsEqualTo(0);
        await Assert.That(doodad.FuncGroupId).IsEqualTo(succeeds ? 17070u : 3054u);
        await Assert.That(_owner.LaborPower).IsEqualTo(succeeds ? (short)10 : (short)20);
        await Assert.That(_owner.Inventory.Bag.Items.Any(item => item.TemplateId == 200)).IsEqualTo(succeeds);
        if (succeeds)
        {
            await Assert.That(doodad.Phases.Select(phase => phase.GroupId)).IsEquivalentTo(new uint[] { 3150, 17070 });
            await Assert.That(doodad.Phases[0].GroupId).IsEqualTo(3150u);
            await Assert.That(doodad.Phases[1].GroupId).IsEqualTo(17070u);
            await Assert.That(doodad.Phases[0].TimeLeft).IsGreaterThan(170000u).And.IsLessThanOrEqualTo(180000u);
            await Assert.That(doodad.Phases[1].TimeLeft).IsGreaterThan(0u).And.IsLessThanOrEqualTo(3000u);
            await Assert.That(callbacks).IsEquivalentTo(new uint[] { 17070, 17070, 17070, 17070 });
            await Assert.That(TaskManager.Instance.GetQueueCount()).IsEqualTo(1);
        }
        else
        {
            await Assert.That(doodad.Phases).IsEmpty();
            await Assert.That(callbacks).IsEmpty();
            await Assert.That(TaskManager.Instance.GetQueueCount()).IsEqualTo(0);
        }
    }

    [Test]
    public async Task CommittedPhase_PrecedesALaterDeletionWithoutReplayingWorldState()
    {
        var doodad = CreateMiningDoodad();
        var phasesBeforeDeletion = -1;
        var result = SkillLaborBatch.Run(_owner, NewSkill(), true, () =>
        {
            doodad.DoChangePhase(_owner, 3150);
            SkillLaborBatch.Current.DeleteDoodad(doodad, () => phasesBeforeDeletion = doodad.Phases.Count);
        });

        await Assert.That(result).IsTrue();
        await Assert.That(phasesBeforeDeletion).IsEqualTo(1);
        await Assert.That(doodad.Phases.Single().GroupId).IsEqualTo(3150u);
    }

    private PhaseRecordingDoodad CreateMiningDoodad()
    {
        SetInstance(new TaskManager(null));
        var subZones = Mock.Of<ISubZoneManager>();
        subZones.GetSubZoneByPosition(Any<WorldTemplate>(), Any<Vector3>()).Returns([]);
        SetInstance(new PublicFarmManager(null, null, subZones.Object));
        SetField(SkillManager.Instance, "_skills", new Dictionary<uint, SkillTemplate>
        {
            [13986] = new() { Id = 13986 }
        });
        var manager = new DoodadManager(Mock.Of<IObjectIdManager>().Object, Mock.Of<IDoodadIdManager>().Object,
            ItemManager.Instance, new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object), Mock.Of<ISusManager>().Object);
        SetInstance(manager);
        SetField(manager, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>>
        {
            [3054] = [new() { GroupId = 3054, FuncId = 1013, FuncType = nameof(DoodadFuncUse), SkillId = 13986, NextPhase = 3150 }],
            [3150] = [new() { GroupId = 3150, FuncId = 49, FuncType = nameof(DoodadFuncLootPack), NextPhase = 17069 }]
        });
        SetField(manager, "_funcTemplates", new Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>
        {
            [nameof(DoodadFuncUse)] = new() { [1013] = new DoodadFuncUse { Id = 1013 } },
            [nameof(DoodadFuncLootPack)] = new() { [49] = new DoodadFuncLootPack { Id = 49, LootPackId = 6359 } }
        });
        SetField(manager, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>
        {
            [3150] = [new() { GroupId = 3150, FuncId = 5209, FuncType = nameof(DoodadFuncTimer) }],
            [17069] = [new() { GroupId = 17069, FuncId = 1035, FuncType = nameof(DoodadFuncRatioChange) }],
            [17070] = [new() { GroupId = 17070, FuncId = 3223, FuncType = nameof(DoodadFuncFinal) }]
        });
        SetField(manager, "_phaseFuncTemplates", new Dictionary<string, Dictionary<uint, DoodadPhaseFuncTemplate>>
        {
            [nameof(DoodadFuncTimer)] = new() { [5209] = new DoodadFuncTimer { Id = 5209, Delay = 180000, NextPhase = 17069 } },
            // Select the common end branch deterministically. Production also has a rare-vein branch.
            [nameof(DoodadFuncRatioChange)] = new() { [1035] = new DoodadFuncRatioChange { Id = 1035, Ratio = 10000, NextPhase = 17070 } },
            [nameof(DoodadFuncFinal)] = new() { [3223] = new DoodadFuncFinal { Id = 3223, After = 3000, Respawn = true, MinTime = 270000, MaxTime = 390000 } }
        });
        var loot = new Loot { ItemId = 200, MinAmount = 1, MaxAmount = 1, AlwaysDrop = true };
        var lootData = new LootGameData();
        SetField(lootData, "_lootPacks", new Dictionary<uint, LootPack>
        {
            [6359] = new() { Id = 6359, Loots = [loot], LootsByGroupNo = new() { [0] = [loot] }, Groups = [], ActabilityGroups = [] }
        });
        SetInstance(lootData);
        var world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1);
        var doodad = new PhaseRecordingDoodad
        {
            ObjId = 1234, TemplateId = 1671, Template = new DoodadTemplate { Id = 1671 }, FuncGroupId = 3054
        };
        typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(doodad, world);
        return doodad;
    }

    private sealed class PhaseRecordingDoodad : Doodad
    {
        public List<(uint GroupId, uint TimeLeft)> Phases { get; } = [];

        public override void BroadcastPacket(GamePacket packet, bool self)
        {
            if (packet is not SCDoodadPhaseChangedPacket)
                return;
            var stream = packet.Write(new PacketStream());
            stream.Rollback();
            stream.ReadBc();
            Phases.Add((stream.ReadUInt32(), stream.ReadUInt32()));
        }
    }
}
