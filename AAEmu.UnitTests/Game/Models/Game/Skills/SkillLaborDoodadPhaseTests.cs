using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Game.Models.Game.Char;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    [Test]
    [Arguments(0, 27301u, true)]
    [Arguments(9, 27301u, true)]
    [Arguments(9, 27301u, false)]
    [Arguments(9, 17684u, true)]
    [Arguments(9, 0u, true)]
    public async Task NuiStatue_ContributesTheWornPackAndLaborTogether(int previous, uint wornItem, bool commitSucceeds)
    {
        const uint skillId = 23780;
        const uint packId = 27301;
        var skill = new Skill(new SkillTemplate
        {
            Id = skillId, TargetType = SkillTargetType.Doodad, ConsumeLaborPower = 25,
            Effects = [new SkillEffect { ApplicationMethod = SkillEffectApplicationMethod.Target, Chance = 100,
                EndLevel = 99, Template = new InteractionEffect { WorldInteraction = WorldInteractionType.Use } }]
        });
        _owner.InitializeLaborCache(50, DateTime.UtcNow);
        var spare = AddConstructionPack(2, packId, _owner.Inventory.Bag, 1);
        var worn = wornItem == 0 ? null : AddConstructionPack(3, wornItem, _owner.Inventory.Equipment, (int)EquipmentItemSlot.Backpack);
        var manager = new DoodadManager(Mock.Of<IObjectIdManager>().Object, Mock.Of<IDoodadIdManager>().Object,
            ItemManager.Instance, new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object), Mock.Of<ISusManager>().Object);
        SetInstance(manager);
        SetField(SkillManager.Instance, "_skills", new Dictionary<uint, SkillTemplate> { [skillId] = skill.Template });
        SetField(manager, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>>
        {
            [19827] = [new() { GroupId = 19827, FuncId = 57, FuncType = nameof(DoodadFuncInsertCounter), SkillId = skillId, NextPhase = 19888 }]
        });
        SetField(manager, "_funcTemplates", new Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>
        {
            [nameof(DoodadFuncInsertCounter)] = new() { [57] = new DoodadFuncInsertCounter { Id = 57, Count = 10, ItemId = packId, ItemCount = 1 } }
        });
        SetField(manager, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>());
        var statue = new PhaseRecordingDoodad
        {
            ObjId = 301, OwnerId = _owner.Id, TemplateId = 7234, FuncGroupId = 19827,
            Template = new DoodadTemplate { Id = 7234, InsertCounterSkills = [skillId] },
            DbId = 301, IsPersistent = true
        };
        statue.SetData(previous);
        var before = statue.PhaseTime;
        var commitCount = 0;
        var publishedBeforeCommit = false;
        skill.CommitLaborBatch = (_, _) =>
        {
            commitCount++;
            publishedBeforeCommit = statue.DataUpdates.Count != 0 || statue.Phases.Count != 0;
            return commitSucceeds;
        };

        skill.ApplyEffects(_owner, new SkillCasterUnit(_owner.ObjId), statue, new SkillCastDoodadTarget { ObjId = statue.ObjId }, null);
        var accepted = wornItem == packId && commitSucceeds;
        var advanced = accepted && previous == 9;
        var expectedCount = advanced ? 0 : previous + (accepted ? 1 : 0);
        await Assert.That(skill.Cancelled).IsEqualTo(!accepted);
        await Assert.That(commitCount).IsEqualTo(wornItem == packId ? 1 : 0);
        await Assert.That(statue.Data).IsEqualTo(expectedCount);
        await Assert.That(statue.FuncGroupId).IsEqualTo(advanced ? 19888u : 19827u);
        await Assert.That(statue.Phases.Count).IsEqualTo(advanced ? 1 : 0);
        await Assert.That(publishedBeforeCommit).IsFalse();
        await Assert.That(statue.DataUpdates).IsEquivalentTo(accepted ? new[] { expectedCount } : Array.Empty<int>());
        if (!advanced)
            await Assert.That(statue.PhaseTime).IsEqualTo(before);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)(accepted ? 25 : 50));
        await Assert.That(_owner.Inventory.GetEquippedBySlot(EquipmentItemSlot.Backpack)).IsSameReferenceAs(accepted ? null : worn);
        await Assert.That(_owner.Inventory.Bag.GetItemBySlot(spare.Slot)).IsSameReferenceAs(spare);
        await Assert.That(DoodadManager.ChangeDoodadData(_owner, statue, 5)).IsFalse();
        await Assert.That(statue.Data).IsEqualTo(expectedCount);

        if (advanced)
        {
            var lateCast = new Skill(skill.Template);
            var extraPack = AddConstructionPack(4, packId, _owner.Inventory.Equipment, (int)EquipmentItemSlot.Backpack);
            var lateResult = SkillLaborBatch.Run(_owner, lateCast, true, () => statue.Use(_owner, skillId));
            await Assert.That(lateResult).IsFalse();
            await Assert.That(_owner.LaborPower).IsEqualTo((short)25);
            await Assert.That(_owner.Inventory.GetEquippedBySlot(EquipmentItemSlot.Backpack)).IsSameReferenceAs(extraPack);
            await Assert.That(statue.Phases.Count).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments(true, true)]
    [Arguments(false, true)]
    [Arguments(false, false)]
    public async Task MiningCompletion_PreservesTheBrokenVeinPhaseOnlyAfterCommit(bool succeeds, bool hasSpace)
    {
        var doodad = CreateMiningDoodad();
        if (!hasSpace)
        {
            _owner.Inventory.Bag.ContainerSize = 1;
            _owner.Inventory.Bag.UpdateFreeSlotCount();
        }
        var callbacks = new List<uint>();
        doodad.ParentWorld.DoodadPhaseChanged += (current, phase) =>
        {
            // The event identifies each completed transition; the doodad retains its committed state.
            callbacks.Add(phase);
            callbacks.Add(current.FuncGroupId);
        };
        var emittedBeforeCommit = -1;
        var skill = NewSkill();
        skill.CommitLaborBatch = (_, _) =>
        {
            emittedBeforeCommit = doodad.Phases.Count + callbacks.Count;
            return succeeds || !hasSpace;
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
        await Assert.That(emittedBeforeCommit).IsEqualTo(hasSpace ? 0 : -1);
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
            await Assert.That(callbacks.SequenceEqual(new uint[] { 3150, 17070, 17070, 17070 })).IsTrue();
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
    [Arguments(true)]
    [Arguments(false)]
    public async Task PaidShearing_SavesCreditForTheGrantedWool(bool commit)
    {
        const uint woolId = 8053, recordId = 1227, sheepFollower = 717;
        var templates = (Dictionary<uint, ItemTemplate>)typeof(ItemManager)
            .GetField("_templates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ItemManager.Instance)!;
        templates[woolId] = new ItemTemplate { Id = woolId, MaxCount = 1000 };
        using var data = new CharacterAchievementsTests.AchievementDataBuilder();
        data.AddRecord(recordId, CharRecordKind.GetLootitem, woolId);
        data.AddAchievement(sheepFollower, 100, false);
        data.AddObjective(1, sheepFollower, recordId);
        var achievements = new CharacterAchievements(_owner, data.Build());
        typeof(Character).GetField("<Achievements>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_owner, achievements);
        var harvest = new DoodadFuncLootItem { Id = 298, ItemId = woolId, CountMin = 1, CountMax = 3, Percent = 10000 };
        var sheep = new Doodad { TemplateId = 518 };
        uint creditAtSave = 0;
        int woolAtSave = 0;
        int WoolInBag() => _owner.Inventory.Bag.Items.Where(i => i.TemplateId == woolId).Sum(i => i.Count);
        var skill = new Skill(new SkillTemplate { Id = 13802, ConsumeLaborPower = 5 })
        {
            CommitLaborBatch = (_, _) =>
            {
                creditAtSave = achievements.GetAmount(sheepFollower);
                woolAtSave = WoolInBag();
                return commit;
            }
        };

        var result = SkillLaborBatch.Run(_owner, skill, true, () => harvest.Use(_owner, sheep, skill.Id));

        await Assert.That(woolAtSave).IsBetween(1, 3);
        await Assert.That(creditAtSave).IsEqualTo((uint)woolAtSave);
        await Assert.That(result).IsEqualTo(commit);
        await Assert.That(WoolInBag()).IsEqualTo(commit ? woolAtSave : 0);
        await Assert.That(achievements.GetAmount(sheepFollower)).IsEqualTo(commit ? creditAtSave : 0u);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)(commit ? 15 : 20));
    }

    [Test]
    [Arguments(996, false, 0, true, true)]  // The ore fits in the original stack.
    [Arguments(998, false, 0, false, true)] // Even the smallest ore roll exceeds the available space.
    [Arguments(0, false, 0, false, true)]   // The full Bag contains only an unrelated item.
    [Arguments(996, true, 1, true, true)]   // One slot suffices for both rolled items.
    [Arguments(996, true, 0, false, true)]  // Stone has no space after the first grant.
    [Arguments(996, true, 1, true, false)] // Both items fit, but the save fails.
    public async Task IronVein_UsesStackCapacityForTheEntirePaidHarvest(int initialOre, bool rollStone, int freeSlots, bool fits, bool commit)
    {
        const uint oreId = 8022, stoneId = 8008;
        // Select reproducible stock outcomes: ordinary iron ore, with or without the optional raw stone group.
        var rewards = new List<Loot> { new() { Group = 1, ItemId = oreId, MinAmount = 3, MaxAmount = 4, DropRate = 10_000_000 } };
        if (rollStone)
            rewards.Add(new Loot { Group = 3, ItemId = stoneId, MinAmount = 2, MaxAmount = 4, DropRate = 10_000_000 });
        var vein = CreateMiningDoodad(rewards.ToArray());
        var templates = (Dictionary<uint, ItemTemplate>)typeof(ItemManager).GetField("_templates", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(ItemManager.Instance)!;
        foreach (var reward in rewards)
            templates[reward.ItemId] = new ItemTemplate { Id = reward.ItemId, MaxCount = 1000 };
        var bag = _owner.Inventory.Bag;
        if (initialOre > 0)
        {
            _material.TemplateId = oreId;
            _material.Template = templates[oreId];
            _material.Count = initialOre;
        }
        bag.ContainerSize = bag.Items.Count + freeSlots;
        await Assert.That(bag.FreeSlotCount).IsEqualTo(freeSlots);
        using var data = new CharacterAchievementsTests.AchievementDataBuilder();
        data.AddRecord(1, CharRecordKind.GetLootitem, oreId);
        data.AddRecord(2, CharRecordKind.GetLootitem, stoneId);
        data.AddRecord(3, CharRecordKind.GetLootpack, 6359);
        for (uint id = 1; id <= 3; id++)
        {
            data.AddAchievement(id, 1000, false);
            data.AddObjective(id, id, id);
        }
        typeof(Character).GetField("<Achievements>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_owner, new CharacterAchievements(_owner, data.Build()));
        (int ore, int stone, uint oreCredit, uint stoneCredit, uint packCredit)? atSave = null;
        int Count(uint item) => bag.Items.Where(row => row.TemplateId == item).Sum(row => row.Count);
        var skill = new Skill(new SkillTemplate { Id = 13986, ConsumeLaborPower = 10, ActabilityGroupId = 13 })
        {
            CommitLaborBatch = (_, _) =>
            {
                atSave = (Count(oreId) - initialOre, Count(stoneId), _owner.Achievements.GetAmount(1),
                    _owner.Achievements.GetAmount(2), _owner.Achievements.GetAmount(3));
                return commit;
            }
        };
        _owner.Actability = new CharacterActability(_owner);
        var session = new RankFeedbackSession();
        _owner.Connection = new GameConnection(session) { ActiveChar = _owner };
        var priorWorld = AppConfiguration.Instance.World;
        bool result;
        try
        {
            AppConfiguration.Instance.World = new WorldConfig { LootRate = 1 };
            result = SkillLaborBatch.Run(_owner, skill, true, () => vein.Use(_owner, skill.Id));
        }
        finally
        {
            AppConfiguration.Instance.World = priorWorld;
        }

        var saved = fits && commit;
        await Assert.That(atSave.HasValue).IsEqualTo(fits);
        if (atSave is { } staged)
        {
            await Assert.That(staged.oreCredit).IsEqualTo((uint)staged.ore);
            await Assert.That(staged.stoneCredit).IsEqualTo((uint)staged.stone);
            await Assert.That(staged.packCredit).IsEqualTo(1u);
        }
        await Assert.That(result).IsEqualTo(saved);
        var bagErrors = session.Packets.Count(packet => BitConverter.ToUInt16(packet, 6) == SCOffsets.SCErrorMsgPacket &&
            BitConverter.ToInt16(packet, 8) == (short)ErrorMessageType.BagFull);
        await Assert.That(bagErrors).IsEqualTo(fits ? 0 : 1);
        await Assert.That(vein.FuncGroupId).IsEqualTo(saved ? 17070u : 3054u);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)(saved ? 10 : 20));
        await Assert.That(bag.GetItemBySlot(0)).IsSameReferenceAs(_material);
        var oreGained = Count(oreId) - initialOre;
        var stoneGained = Count(stoneId);
        await Assert.That(oreGained).IsBetween(saved ? 3 : 0, saved ? 4 : 0);
        await Assert.That(stoneGained).IsBetween(saved && rollStone ? 2 : 0, saved && rollStone ? 4 : 0);
        await Assert.That(_owner.Achievements.GetAmount(1)).IsEqualTo((uint)oreGained);
        await Assert.That(_owner.Achievements.GetAmount(2)).IsEqualTo((uint)stoneGained);
        await Assert.That(_owner.Achievements.GetAmount(3)).IsEqualTo(saved ? 1u : 0u);
    }

    [Test]
    public async Task PageLoot_FullBagAndOwnedPageReportsOnlyThePickupLimit()
    {
        const uint page = 23216;
        var manager = new DoodadManager(null, null, null, null, null);
        SetInstance(manager);
        SetField(SkillManager.Instance, "_skills", new Dictionary<uint, SkillTemplate>());
        SetField(manager, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>());
        var function = new DoodadFunc { FuncId = 217, GroupId = 11107, FuncType = nameof(DoodadFuncLootPack), NextPhase = 11108 };
        SetField(manager, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>> { [11107] = [function] });
        SetField(manager, "_funcTemplates", new Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>
        {
            [function.FuncType] = new() { [217] = new DoodadFuncLootPack { Id = 217, LootPackId = 6660 } }
        });
        // Select one possible reward from this stock page pack.
        SetHarvestPack(6660, [new Loot { ItemId = page, MinAmount = 1, MaxAmount = 1, AlwaysDrop = true }]);
        var pages = new Doodad { TemplateId = 4531, Template = new DoodadTemplate { Id = 4531 }, FuncGroupId = 11107 };
        var item = new ItemTemplate { Id = page, MaxCount = 1, PickupLimit = 1 };
        SetField(ItemManager.Instance, "_templates", new Dictionary<uint, ItemTemplate> { [page] = item });
        _material.TemplateId = page;
        _material.Template = item;
        _material.Count = 1;
        _owner.Inventory.Bag.ContainerSize = 1;
        var session = new RankFeedbackSession();
        _owner.Connection = new GameConnection(session) { ActiveChar = _owner };
        var priorWorld = AppConfiguration.Instance.World;
        try
        {
            AppConfiguration.Instance.World = new WorldConfig { LootRate = 1 };
            pages.Use(_owner);
        }
        finally
        {
            AppConfiguration.Instance.World = priorWorld;
        }
        var errors = session.Packets.Where(packet => BitConverter.ToUInt16(packet, 6) == SCOffsets.SCErrorMsgPacket)
            .Select(packet => (ErrorMessageType)BitConverter.ToInt16(packet, 8)).ToArray();
        await Assert.That(errors.SequenceEqual(new[] { ErrorMessageType.ItemPickupLimit })).IsTrue();
        await Assert.That(pages.FuncGroupId).IsEqualTo(11107u);
        await Assert.That(_owner.Inventory.Bag.Items.Single()).IsSameReferenceAs(_material);
        await Assert.That(_material.Count).IsEqualTo(1);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)20);
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

    private PhaseRecordingDoodad CreateMiningDoodad(params Loot[] rewards)
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
        if (rewards.Length == 0)
            rewards = [new Loot { ItemId = 200, MinAmount = 1, MaxAmount = 1, AlwaysDrop = true }];
        SetHarvestPack(6359, rewards);
        var world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1);
        var doodad = new PhaseRecordingDoodad
        {
            ObjId = 1234, TemplateId = 1671, Template = new DoodadTemplate { Id = 1671 }, FuncGroupId = 3054
        };
        typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(doodad, world);
        return doodad;
    }

    private void SetHarvestPack(uint packId, Loot[] rewards)
    {
        var lootData = new LootGameData();
        SetField(lootData, "_lootPacks", new Dictionary<uint, LootPack>
        {
            [packId] = new()
            {
                Id = packId, Loots = [.. rewards], Groups = [], ActabilityGroups = [],
                LootsByGroupNo = rewards.GroupBy(row => row.Group).ToDictionary(group => group.Key, group => group.ToList())
            }
        });
        SetInstance(lootData);
    }

    private sealed class PhaseRecordingDoodad : Doodad
    {
        public List<(uint GroupId, uint TimeLeft)> Phases { get; } = [];
        public List<int> DataUpdates { get; } = [];

        public override void BroadcastPacket(GamePacket packet, bool self)
        {
            if (packet is not (SCDoodadPhaseChangedPacket or SCDoodadChangedPacket))
                return;
            var stream = packet.Write(new PacketStream());
            stream.Rollback();
            stream.ReadBc();
            if (packet is SCDoodadChangedPacket)
                DataUpdates.Add(stream.ReadInt32());
            else
                Phases.Add((stream.ReadUInt32(), stream.ReadUInt32()));
        }
    }
}
