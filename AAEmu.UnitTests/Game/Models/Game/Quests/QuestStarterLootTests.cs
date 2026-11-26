using System.Reflection;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Schedules;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Models.Game.Quests;

public sealed partial class QuestRewardDeliveryTests
{
    private const uint Slipper = 5052;
    private const uint SlipperQuest = 336;

    [Test]
    public async Task QuestStarterLoot_SpiderDropsOneSlipper_OwnerCanTakeItOnceBeforeAccept()
    {
        using var models = new QuestInteractionTestModels();
        var owner = CreateOwner(10);
        owner.Level = 22;
        owner.Faction = new SystemFaction { Id = (FactionsEnum)101, MotherId = (FactionsEnum)148 };
        var pack = ConfigureSlipperLoot();
        var loot = new LootGameData();
        SetField(loot, "_lootPacks", new Dictionary<uint, LootPack> { [6643] = pack });
        UseLootSingleton(loot);
        UseLootSingleton(new TeamManager(null, null, null));
        SetField(_items, "_lootPackDroppingNpc", new Dictionary<uint, List<LootPackDroppingNpc>>
        {
            [5152] = [new() { Id = 38790, NpcId = 5152, LootPackId = 6643, DefaultPack = true }]
        });
        var world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1);
        var spider = new Npc { TemplateId = 5152, ObjId = 77, Template = new NpcTemplate() };
        typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(spider, world);
        void Join(Character player)
        {
            typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(player, world);
            typeof(Character).GetField("<IsOnline>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(player, true);
            player.Connection = new GameConnection(Mock.Of<ISession>().Object) { ActiveChar = player };
        }
        Join(owner);
        var other = new CharacterMock { Id = 8 };
        Join(other);
        var corpse = new LootingContainer(spider, Random.Shared, (_, _) => { }, id => id == owner.Id ? owner : other);
        var previous = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig();
        try
        {
            corpse.GenerateLoot(owner);
            var drop = corpse.Items.Values.Single();
            await Assert.That(drop.Item.TemplateId).IsEqualTo(Slipper);
            await Assert.That(drop.Item.Count).IsEqualTo(1);
            await Assert.That(corpse.TryTakeLoot(other, drop.ItemIndex, null, false)).IsFalse();

            owner.Level = 21;
            await Assert.That(corpse.TryTakeLoot(owner, drop.ItemIndex, null, false)).IsFalse();
            owner.Level = 22;
            ItemIdManager.Instance.Initialize();
            await Assert.That(corpse.TryTakeLoot(owner, drop.ItemIndex, null, false)).IsTrue();
            corpse.GenerateLoot(owner);
            await Assert.That(corpse.Items).IsEmpty();
            await Assert.That(corpse.TryTakeLoot(owner, drop.ItemIndex, null, false)).IsFalse();
            await Assert.That(owner.Inventory.GetItemsCount(Slipper)).IsEqualTo(1);
            await Assert.That(owner.Quests.HasQuest(SlipperQuest)).IsFalse();
            await Assert.That(owner.Quests.HasQuestCompleted(SlipperQuest)).IsFalse();
        }
        finally
        {
            AppConfiguration.Instance.World = previous;
        }
    }

    [Test]
    [Arguments(21, 148, false, false)]
    [Arguments(22, 149, false, false)]
    [Arguments(22, 148, true, false)]
    [Arguments(22, 148, false, true)]
    public async Task QuestStarterLoot_SpiderUsesStockLevelFactionAndCompletion(int level, int mother, bool complete, bool expected)
    {
        var owner = CreateOwner(10);
        owner.Level = (byte)level;
        owner.Faction = new SystemFaction { MotherId = (FactionsEnum)mother };
        if (complete)
        {
            var block = new CompletedQuest((ushort)(SlipperQuest / 64));
            block.Body.Set((int)(SlipperQuest % 64), true);
            var saved = (Dictionary<ushort, CompletedQuest>)typeof(CharacterQuests).GetProperty("CompletedQuests",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner.Quests)!;
            saved.Add(block.Id, block);
        }
        var pack = ConfigureSlipperLoot();
        var drop = RollLoot(pack, owner);
        await Assert.That(drop.Count).IsEqualTo(expected ? 1 : 0);
        await Assert.That(owner.Quests.HasQuest(SlipperQuest)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task QuestStarterLoot_OrdinaryObjectiveStillNeedsTheActiveQuest(bool active)
    {
        // Pack 7093, row 67306. This objective item has no Start component.
        const uint objective = 17831;
        const uint questId = 2695;
        var owner = CreateOwner(10);
        _templates.Add(objective, new ItemTemplate { Id = objective, LootQuestId = questId });
        if (active)
        {
            var quest = new Quest(new QuestTemplate { Id = questId }, owner, _quests, Mock.Of<ITaskManager>().Object,
                Mock.Of<ISkillManager>().Object, Mock.Of<IExpressTextManager>().Object, Mock.Of<IWorldManager>().Object);
            owner.Quests.ActiveQuests.Add(questId, quest);
        }
        var drop = new Loot { Id = 67306, LootPackId = 7093, ItemId = objective, MinAmount = 1,
            MaxAmount = 1, AlwaysDrop = true, DropRate = 10_000_000 };
        var pack = new LootPack { Id = 7093, Groups = [], ActabilityGroups = [], Loots = [drop],
            LootsByGroupNo = new() { [0] = [drop] } };
        await Assert.That(RollLoot(pack, owner).Count).IsEqualTo(active ? 1 : 0);
    }

    [Test]
    public async Task QuestStarterLoot_WrongItemOrAZeroChanceCannotAwardTheStarter()
    {
        var owner = CreateOwner(10);
        owner.Level = 22;
        owner.Faction = new SystemFaction { MotherId = (FactionsEnum)148 };
        var pack = ConfigureSlipperLoot();
        var drop = pack.Loots.Single();
        // Keep the exact authored starter properties, except turn off the roll.
        drop.DropRate = 0;
        drop.AlwaysDrop = false;
        await Assert.That(RollLoot(pack, owner)).IsEmpty();

        // An AcceptQuest item with the same loot quest still needs the exact Start act.
        const uint differentItem = 50_003;
        _templates.Add(differentItem, new ItemTemplate { Id = differentItem, ImplId = ItemImplEnum.AcceptQuest,
            LootQuestId = SlipperQuest, UseSkillId = 11141 });
        drop.ItemId = differentItem;
        drop.AlwaysDrop = true;
        await Assert.That(RollLoot(pack, owner)).IsEmpty();
        await Assert.That(owner.Quests.HasQuest(SlipperQuest)).IsFalse();
    }

    [Test]
    public async Task QuestStarterLoot_ClosedScheduleAndWrongItemTypeRefuseAnOtherwiseEligibleDrop()
    {
        var owner = CreateOwner(10);
        owner.Level = 22;
        owner.Faction = new SystemFaction { MotherId = (FactionsEnum)148 };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 26, 9, 0, 0, TimeSpan.Zero));
        var schedules = new GameScheduleManager(null, clock);
        schedules.LoadGameSchedules(new Dictionary<int, GameSchedules>
        {
            [1] = new() { Id = 1, StYear = 2026, StMonth = 9, StDay = 26, StHour = 10,
                EdYear = 2026, EdMonth = 9, EdDay = 26, EdHour = 11 }
        });
        schedules.LoadGameScheduleQuests(new Dictionary<int, GameScheduleQuests>
        {
            [1] = new() { Id = 1, QuestId = (int)SlipperQuest, GameScheduleId = 1 }
        });
        owner.Quests = new CharacterQuests(owner, schedules);
        var pack = ConfigureSlipperLoot();
        await Assert.That(RollLoot(pack, owner)).IsEmpty();
        clock.Advance(TimeSpan.FromHours(1));
        await Assert.That(RollLoot(pack, owner).Single().itemId).IsEqualTo(Slipper);
        _templates[Slipper].ImplId = ItemImplEnum.Misc;
        await Assert.That(RollLoot(pack, owner)).IsEmpty();
    }

    private static List<(uint itemId, int count, byte grade, uint lootGroupOrigin)> RollLoot(LootPack pack, Character owner)
    {
        var previous = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig();
        try
        {
            return pack.GeneratePackNewV2(1, 1, owner, ActabilityType.None);
        }
        finally
        {
            AppConfiguration.Instance.World = previous;
        }
    }

    private LootPack ConfigureSlipperLoot()
    {
        _templates.Add(Slipper, new ItemTemplate { Id = Slipper, MaxCount = 1, ImplId = ItemImplEnum.AcceptQuest,
            LootQuestId = SlipperQuest, LevelRequirement = 22, UseSkillId = 11141 });
        var template = new QuestTemplate { Id = SlipperQuest };
        var start = new QuestComponentTemplate(template) { Id = 2492, KindId = QuestComponentKind.Start };
        start.ActTemplates.Add(new QuestActConAcceptItem(start) { ActId = 3819, ItemId = Slipper });
        template.Components.Add(start.Id, start);
        SetField(_quests, "_questTemplates", new Dictionary<uint, QuestTemplate> { [SlipperQuest] = template });
        var requirements = new UnitRequirementsGameData();
        var rows = new Dictionary<string, List<UnitReqs>>
        {
            ["QuestComponent"] = [
                new() { Id = 32766, OwnerId = 2492, OwnerType = "QuestComponent", KindType = UnitReqsKindType.Level, Value1 = 22 },
                new() { Id = 32767, OwnerId = 2492, OwnerType = "QuestComponent", KindType = UnitReqsKindType.MotherFaction, Value1 = 148 }
            ]
        };
        typeof(UnitRequirementsGameData).GetProperty("_unitReqsByOwnerType", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(requirements, rows);
        UseLootSingleton(requirements);
        List<Loot> drops = [new() { Id = 66110, LootPackId = 6643, ItemId = Slipper, MinAmount = 1, MaxAmount = 1,
            DropRate = 10_000_000, AlwaysDrop = true }];
        return new LootPack { Id = 6643, Groups = [], ActabilityGroups = [], Loots = drops, LootsByGroupNo = new() { [0] = drops } };
    }

    private void UseLootSingleton<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousSingletons.Add(field, field.GetValue(null));
        field.SetValue(null, value);
    }
}
