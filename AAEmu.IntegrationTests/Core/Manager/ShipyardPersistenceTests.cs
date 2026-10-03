using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Shipyard;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Taxations;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.StaticValues;

using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shipyard_PlacementCommitsExactDesignMoneyProductsAndConstructionTogether(bool failBeforeCommit)
    {
        using var fixture = new ShipyardGraph();
        var graph = fixture.Graph;
        var alternative = graph.AddItem(0);
        alternative.Count = 1;
        var design = graph.AddItem(1);
        design.Count = 1;
        design.Template.UseSkillId = 50;
        var material = graph.AddEquipment(2);
        fixture.Template.OriginItemId = design.TemplateId;
        fixture.Skills.Setup(manager => manager.GetSkillReagentsBySkillId(50))
            .Returns([new SkillReagent { ItemId = material.TemplateId, Amount = 1 }]);
        fixture.Skills.Setup(manager => manager.GetSkillProductsBySkillId(50))
            .Returns([new SkillProduct { ItemId = fixture.Template.ItemId, Amount = 1 }]);
        var shipyard = fixture.NewShipyard();
        shipyard.SourceDesignItemId = design.Id;
        Assert.True(graph.Save.TryCommitEconomy([graph.Sender]));
        var commits = 0;
        fixture.Manager.CommitPersistence = (participants, write) =>
        {
            commits++;
            return graph.Save.TryCommitEconomy(participants, context =>
            {
                write(context);
                if (failBeforeCommit)
                    throw new InvalidOperationException("Injected failure after shipyard placement writes");
            });
        };

        var result = fixture.Manager.TryInstallPaidShipyard(graph.Sender, shipyard);

        Assert.Equal(!failBeforeCommit, result);
        Assert.Equal(1, commits);
        Assert.Equal(failBeforeCommit ? 10000L : 9750L, graph.Sender.Money);
        Assert.Equal(graph.Sender.Money, Scalar($"SELECT money FROM characters WHERE id={graph.Sender.Id}"));
        Assert.Equal(failBeforeCommit ? 1 : 0, Scalar($"SELECT COUNT(*) FROM items WHERE id={design.Id}"));
        Assert.Equal(failBeforeCommit ? 1 : 0, Scalar($"SELECT COUNT(*) FROM items WHERE id={material.Id}"));
        Assert.Equal(1, Scalar($"SELECT count FROM items WHERE id={alternative.Id}"));
        Assert.Equal(failBeforeCommit ? 0 : 1,
            Scalar($"SELECT COUNT(*) FROM items WHERE owner={graph.Sender.Id} AND template_id={fixture.Template.ItemId}"));
        Assert.Equal(failBeforeCommit ? 0 : 1, Scalar($"SELECT COUNT(*) FROM shipyards WHERE id={shipyard.ShipyardData.Id}"));
        Assert.Equal(!failBeforeCommit, fixture.Manager.IsCurrent(shipyard));
        Assert.Equal(failBeforeCommit, graph.Sender.Inventory.Bag.Items.Contains(design));
        Assert.Equal(failBeforeCommit, graph.Sender.Inventory.Bag.Items.Contains(material));
        Assert.Contains(alternative, graph.Sender.Inventory.Bag.Items);
        var restoredItems = graph.ReloadLifecycle().Items;
        Assert.Equal(failBeforeCommit, restoredItems.GetItemByItemId(design.Id) != null);
        Assert.Equal(failBeforeCommit, restoredItems.GetItemByItemId(material.Id) != null);
        Assert.NotNull(restoredItems.GetItemByItemId(alternative.Id));
        var restored = fixture.Reload();
        if (failBeforeCommit)
            Assert.Empty(restored);
        else
        {
            var loaded = Assert.Single(restored);
            Assert.Equal(graph.Sender.Id, loaded.ShipyardData.Type2);
            Assert.Equal(shipyard.ShipyardData.Id, loaded.ShipyardData.Id);
            Assert.Equal(shipyard.Transform.Local.Position, loaded.Transform.Local.Position);
            Assert.Equal(shipyard.ShipyardData.zRot, loaded.ShipyardData.zRot);
            Assert.Equal(0, loaded.CurrentStep);
            Assert.Equal(0, loaded.NumAction);
        }
        fixture.ObjectIds.Verify(ids => ids.ReleaseId(shipyard.ObjId), failBeforeCommit ? Times.Once() : Times.Never());
        fixture.ShipyardIds.Verify(ids => ids.ReleaseId((uint)shipyard.ShipyardData.Id), failBeforeCommit ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData("success")]
    [InlineData("no_labor")]
    [InlineData("failed_commit")]
    public void Shipyard_ConstructionRestoresProgressHealthLaborAndMaterialsAfterRestart(string outcome)
    {
        using var fixture = new ShipyardGraph();
        var graph = fixture.Graph;
        var player = graph.Sender;
        var initialLabor = outcome == "no_labor" ? (short)0 : (short)20;
        fixture.SetLabor(initialLabor);
        var material = graph.AddItem(0);
        material.Count = 1;
        var shipyard = fixture.NewShipyard();
        shipyard.RestoreConstruction(0, 1, false);
        fixture.Persist(shipyard);
        var skill = fixture.Skill(50, 10, outcome == "failed_commit");

        var result = SkillLaborBatch.Run(player, skill, true, () =>
        {
            Assert.Equal(1, player.Inventory.Bag.ConsumeItem(ItemTaskType.SkillReagents, material.TemplateId, 1, material));
            CraftEffect.AdvanceShipyardConstruction(player, shipyard, 50, skill);
        });

        var success = outcome == "success";
        Assert.Equal(success, result);
        Assert.Equal(success ? 10 : initialLabor, player.LaborPower);
        Assert.Equal(player.LaborPower, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
        Assert.Equal(success ? 0 : 1, Scalar($"SELECT COUNT(*) FROM items WHERE id={material.Id}"));
        Assert.Equal(success ? 1 : 0, shipyard.CurrentStep);
        Assert.Equal(success ? 0 : 1, shipyard.NumAction);
        Assert.False(shipyard.IsDirty);
        var restored = Assert.Single(fixture.Reload());
        Assert.Equal(shipyard.CurrentStep, restored.CurrentStep);
        Assert.Equal(shipyard.NumAction, restored.NumAction);
        Assert.Equal(shipyard.ModelId, restored.ModelId);
        Assert.Equal(shipyard.ShipyardData.Actions, restored.ShipyardData.Actions);
        Assert.Equal(shipyard.ShipyardData.Step, restored.ShipyardData.Step);
        Assert.Equal(75, restored.Hp);
        Assert.Equal(9, restored.Mp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shipyard_CompletionStoresExactRewardOnceAndRetiresAfterOfflineRestart(bool failBeforeCommit)
    {
        using var fixture = new ShipyardGraph();
        var graph = fixture.Graph;
        var shipyard = fixture.NewShipyard();
        shipyard.RestoreConstruction(-1, 0, false);
        fixture.Persist(shipyard);
        var before = DateTime.UtcNow;
        var skill = fixture.Skill(51, 0, failBeforeCommit);

        var result = SkillLaborBatch.Run(graph.Sender, skill, false,
            () => CraftEffect.CompleteShipyardConstruction(graph.Sender, shipyard, skill));

        Assert.Equal(!failBeforeCommit, result);
        var restored = Assert.Single(fixture.Reload());
        var expectedCount = failBeforeCommit ? 0 : 1;
        Assert.Equal(expectedCount, Scalar($"SELECT COUNT(*) FROM items WHERE owner={graph.Sender.Id} AND template_id={fixture.Template.ItemId}"));
        Assert.Equal(shipyard.CompletionItemId, restored.CompletionItemId);
        Assert.Equal(shipyard.ShipyardData.Step, restored.ShipyardData.Step);
        Assert.Equal(shipyard.CeremonyEnd.Ticks / 10, restored.CeremonyEnd.Ticks / 10);
        if (failBeforeCommit)
        {
            Assert.Equal(0UL, restored.CompletionItemId);
            Assert.Equal(DateTime.MinValue, restored.CeremonyEnd);
            Assert.Equal(fixture.Template.ShipyardSteps.Count, restored.ShipyardData.Step);
            Assert.Empty(graph.Sender.Inventory.Bag.Items);
            return;
        }
        Assert.NotEqual(0UL, restored.CompletionItemId);
        Assert.InRange(restored.CeremonyEnd, before.AddMilliseconds(fixture.Template.CeremonyAnimTime),
            DateTime.UtcNow.AddMilliseconds(fixture.Template.CeremonyAnimTime));
        var reward = graph.ReloadLifecycle().Items.GetItemByItemId(restored.CompletionItemId);
        Assert.NotNull(reward);
        Assert.Equal(fixture.Template.ItemId, reward.TemplateId);
        Assert.Equal(graph.Sender.Id, reward.OwnerId);
        var repeatedSkill = fixture.Skill(51, 0);
        Assert.False(SkillLaborBatch.Run(graph.Sender, repeatedSkill, false,
            () => CraftEffect.CompleteShipyardConstruction(graph.Sender, restored, repeatedSkill)));
        restored.CeremonyEnd = DateTime.UtcNow.AddSeconds(-1);
        Assert.True(graph.Save.TryCommitEconomy([], restored.Save));

        // The world lookup has no online owner after restart. The paid item survives retirement.
        fixture.RestoredManager.ShipyardCompleted(restored);
        fixture.RestoredManager.ShipyardCompleted(restored);

        Assert.True(restored.Retired);
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM shipyards WHERE id={restored.ShipyardData.Id}"));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM items WHERE id={reward.Id}"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM doodads WHERE template_id={fixture.DebrisTemplateId}"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Shipyard_DestructionCommitsDebrisAndDeletionTogetherWithoutDuplicates(bool paidSkill, bool failBeforeCommit)
    {
        using var fixture = new ShipyardGraph();
        fixture.EnableSalvage();
        var graph = fixture.Graph;
        var shipyard = fixture.NewShipyard();
        fixture.Persist(shipyard);
        fixture.SetLabor(20);
        var material = graph.AddItem(0);
        material.Count = 1;
        Assert.True(graph.Save.TryCommitEconomy([graph.Sender]));
        var publishedDeaths = 0;
        fixture.Manager.CommitPersistence = (participants, write) => graph.Save.TryCommitEconomy(participants, context =>
        {
            write(context);
            Assert.Equal(0, publishedDeaths);
            Assert.Empty(fixture.World.GetAllDoodads());
            if (failBeforeCommit)
                throw new InvalidOperationException("Injected failure after shipyard salvage and deletion writes");
        });
        if (paidSkill)
        {
            var skill = fixture.Skill(52, 10, failBeforeCommit);
            var result = SkillLaborBatch.Run(graph.Sender, skill, true, () =>
            {
                Assert.Equal(1, graph.Sender.Inventory.Bag.ConsumeItem(ItemTaskType.SkillReagents, material.TemplateId, 1, material));
                fixture.Manager.DestroyShipyard(shipyard, () => publishedDeaths++);
                fixture.Manager.DestroyShipyard(shipyard, () => publishedDeaths++);
                Assert.Equal(0, publishedDeaths);
                Assert.Empty(fixture.World.GetAllDoodads());
            });
            Assert.Equal(!failBeforeCommit, result);
        }
        else
            fixture.Manager.DestroyShipyard(shipyard, () => publishedDeaths++);

        Assert.Equal(failBeforeCommit ? 0 : 1, publishedDeaths);
        Assert.Equal(!failBeforeCommit, shipyard.Retired);
        Assert.Equal(failBeforeCommit ? 1 : 0, Scalar($"SELECT COUNT(*) FROM shipyards WHERE id={shipyard.ShipyardData.Id}"));
        Assert.Equal(failBeforeCommit ? 0 : 3,
            Scalar($"SELECT COUNT(*) FROM doodads WHERE template_id={fixture.DebrisTemplateId} AND owner_id=0 AND owner_type=255 AND z=40"));
        Assert.Equal(failBeforeCommit ? 0 : 3, fixture.World.GetAllDoodads().Count);
        Assert.Equal(paidSkill && !failBeforeCommit ? 10 : 20, graph.Sender.LaborPower);
        Assert.Equal(paidSkill && !failBeforeCommit ? 0 : 1, Scalar($"SELECT COUNT(*) FROM items WHERE id={material.Id}"));
        if (failBeforeCommit)
        {
            Assert.False(shipyard.Retiring);
            Assert.Single(fixture.Reload());
        }
        else
        {
            fixture.Manager.DestroyShipyard(shipyard, () => publishedDeaths++);
            Assert.Equal(1, publishedDeaths);
            Assert.Equal(3, Scalar($"SELECT COUNT(*) FROM doodads WHERE template_id={fixture.DebrisTemplateId}"));
            Assert.Empty(fixture.Reload());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shipyard_FailedFatalDamageRestoresHealthAndAllowsRetry(bool paidSkill)
    {
        using var fixture = new ShipyardGraph();
        fixture.EnableSalvage();
        var shipyard = fixture.NewShipyard();
        fixture.Persist(shipyard);
        fixture.SetLabor(20);
        var deaths = 0;
        shipyard.Events.OnDeath += (_, _) => deaths++;
        fixture.Manager.CommitPersistence = (participants, write) => fixture.Graph.Save.TryCommitEconomy(participants, context =>
        {
            write(context);
            throw new InvalidOperationException("Injected failure after fatal shipyard damage");
        });

        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (paidSkill)
            {
                var skill = fixture.Skill(52, 10, true);
                Assert.False(SkillLaborBatch.Run(fixture.Graph.Sender, skill, true,
                    () => shipyard.ReduceCurrentHp(fixture.Graph.Sender, 75, KillReason.Gm)));
            }
            else
                shipyard.ReduceCurrentHp(fixture.Graph.Sender, 75, KillReason.Gm);

            Assert.Equal(75, shipyard.Hp);
            Assert.False(shipyard.IsDead);
            Assert.False(shipyard.Retiring);
            Assert.False(shipyard.Retired);
            Assert.Equal(0, deaths);
            Assert.Equal(20, fixture.Graph.Sender.LaborPower);
            Assert.Equal(75, Scalar($"SELECT hp FROM shipyards WHERE id={shipyard.ShipyardData.Id}"));
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM doodads WHERE template_id={fixture.DebrisTemplateId}"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shipyard_RetiringOrRetiredConstructionDoesNotConsumeMaterialsOrLabor(bool retired)
    {
        using var fixture = new ShipyardGraph();
        var graph = fixture.Graph;
        fixture.SetLabor(20);
        var material = graph.AddItem(0);
        material.Count = 1;
        var shipyard = fixture.NewShipyard();
        fixture.Persist(shipyard);
        shipyard.Retiring = !retired;
        shipyard.Retired = retired;
        var skill = fixture.Skill(50, 10);

        Assert.False(SkillLaborBatch.Run(graph.Sender, skill, true, () =>
        {
            Assert.Equal(1, graph.Sender.Inventory.Bag.ConsumeItem(ItemTaskType.SkillReagents, material.TemplateId, 1, material));
            CraftEffect.AdvanceShipyardConstruction(graph.Sender, shipyard, 50, skill);
        }));

        Assert.Equal(20, graph.Sender.LaborPower);
        Assert.Equal(1, Scalar($"SELECT count FROM items WHERE id={material.Id}"));
        Assert.Equal(0, Scalar($"SELECT current_action FROM shipyards WHERE id={shipyard.ShipyardData.Id}"));
    }

    private sealed class ShipyardGraph : IDisposable
    {
        public SendGraph Graph { get; } = new();
        private readonly LaborBuffServices _services = new();
        private readonly List<Action> _restore = [];
        public Mock<IObjectIdManager> ObjectIds { get; } = new();
        public Mock<IShipyardIdManager> ShipyardIds { get; } = new();
        public Mock<ISkillManager> Skills { get; } = new();
        public ShipyardManager Manager { get; }
        public ShipyardManager RestoredManager { get; private set; }
        public WorldInstance World { get; }
        public ShipyardsTemplate Template { get; }
        public uint DebrisTemplateId => Graph.Sender.Id + 7;
        private readonly Mock<IWorldManager> _worlds = new();
        private readonly ITaxationsManager _taxations;
        private uint _nextObject;

        public ShipyardGraph()
        {
            _nextObject = Graph.Sender.Id + 200;
            ObjectIds.Setup(ids => ids.GetNextId()).Returns(() => ++_nextObject);
            ShipyardIds.Setup(ids => ids.GetNextId()).Returns(Graph.Sender.Id + 50);
            var taxations = new Mock<ITaxationsManager>();
            taxations.Setup(manager => manager.Taxations).Returns(new Dictionary<uint, Taxation>
                { [1] = new() { Id = 1, Tax = 250 } });
            _taxations = taxations.Object;
            var faction = new FactionManager(null);
            SetField(faction, "_systemFactions", new Dictionary<FactionsEnum, SystemFaction>
                { [Graph.Sender.Faction.Id] = Graph.Sender.Faction });
            Replace(faction);
            Replace(new ZoneManager(null, null));
            World = HousingPlacementWorld(Graph.Sender.Id);
            World.Regions = new Region[WorldManager.SECTORS_PER_CELL, WorldManager.SECTORS_PER_CELL];
            World.Water.OceanLevel = 40;
            World.SpawnManager = new SpawnManager(World);
            Graph.Sender.ParentWorld = World;
            Graph.Sender.ObjId = Graph.Sender.Id;
            Template = new ShipyardsTemplate
            {
                Id = Graph.Sender.Id + 4, ItemId = Graph.Sender.Id + 5, MainModelId = 90,
                CeremonyAnimTime = 60000, TaxationId = 1, TaxDuration = 86400000
            };
            Template.ShipyardSteps[0] = new() { Step = 0, SkillId = 50, ModelId = 10, NumActions = 2, MaxHp = 100 };
            Template.ShipyardSteps[1] = new() { Step = 1, SkillId = 51, ModelId = 20, NumActions = 3, MaxHp = 100 };
            var templates = (Dictionary<uint, ItemTemplate>)typeof(ItemManager)
                .GetField("_templates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Graph.Items)!;
            templates[Template.ItemId] = new ItemTemplate
                { Id = Template.ItemId, MaxCount = 1, FixedGrade = -1, BindType = ItemBindType.Normal };
            var itemIds = (IItemIdManager)typeof(ItemManager)
                .GetField("<itemIdManager>P", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Graph.Items)!;
            var nextItem = Graph.Sender.Id + 90;
            Mock.Get(itemIds).Setup(ids => ids.GetNextId()).Returns(() => ++nextItem);
            Manager = NewManager();
            Replace(Manager);
        }

        private ShipyardManager NewManager()
        {
            var manager = new ShipyardManager(Mock.Of<ITaskManager>(), ObjectIds.Object, ShipyardIds.Object,
                _worlds.Object, _taxations, Skills.Object)
                { CommitPersistence = (participants, write) => Graph.Save.TryCommitEconomy(participants, write) };
            manager._shipyardsTemplate[Template.Id] = Template;
            return manager;
        }

        public Shipyard NewShipyard()
        {
            var shipyard = new Shipyard
            {
                TemplateId = Template.Id, Template = Template, ParentWorld = World, Faction = Graph.Sender.Faction,
                Level = 30, Hp = 75, Mp = 9, Name = Graph.Sender.Name,
                ShipyardData = new ShipyardData
                {
                    TemplateId = Template.Id, Type2 = Graph.Sender.Id, OwnerName = Graph.Sender.Name,
                    Type3 = Graph.Sender.Faction.Id, Spawned = DateTime.UtcNow,
                    X = 100, Y = 200, Z = 40, zRot = 0.5f, Step = 0, Hp = 10000
                }
            };
            shipyard.Transform.Local.SetPosition(100, 200, 40);
            shipyard.Transform.Local.SetRotation(0, 0, 0.5f);
            shipyard.RestoreConstruction(0, 0, false);
            return shipyard;
        }

        public void Persist(Shipyard shipyard)
        {
            shipyard.ShipyardData.Id = Graph.Sender.Id + 50;
            shipyard.Id = (uint)shipyard.ShipyardData.Id;
            shipyard.ShipyardData.ObjId = shipyard.ObjId = ObjectIds.Object.GetNextId();
            SetField(Manager, "_shipyard", new Dictionary<uint, Shipyard> { [(uint)shipyard.ShipyardData.Id] = shipyard });
            Assert.True(Graph.Save.TryCommitEconomy([Graph.Sender], shipyard.Save));
        }

        public IReadOnlyList<Shipyard> Reload()
        {
            RestoredManager = NewManager();
            return RestoredManager.LoadPlayerShipyards(World, false);
        }

        public void SetLabor(short amount)
        {
            Graph.Sender.InitializeLaborCache(amount, DateTime.UtcNow);
            Execute($"INSERT INTO accounts(account_id,labor) VALUES({Graph.Sender.AccountId},{amount}) ON DUPLICATE KEY UPDATE labor={amount}");
        }

        public Skill Skill(uint id, int labor, bool failBeforeCommit = false) =>
            new(new SkillTemplate { Id = id, ConsumeLaborPower = labor })
            {
                CommitLaborBatch = (_, write) => Graph.Save.TryCommitEconomy(SkillLaborBatch.Current.Participants, context =>
                {
                    write(context);
                    if (failBeforeCommit)
                        throw new InvalidOperationException("Injected failure after shipyard skill writes");
                })
            };

        public void EnableSalvage()
        {
            var config = AppConfiguration.Instance.World;
            AppConfiguration.Instance.World = new WorldConfig { GrowthRate = 1, ExpRate = 1 };
            _restore.Add(() => AppConfiguration.Instance.World = config);
            var idsField = typeof(DoodadIdManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
            var previousIds = idsField.GetValue(null);
            var ids = new DoodadIdManager();
            Assert.True(ids.Initialize());
            idsField.SetValue(null, ids);
            _restore.Add(() => idsField.SetValue(null, previousIds));
            var doodads = new DoodadManager(ObjectIds.Object, ids, Graph.Items, null, null);
            var template = new DoodadTemplate { Id = DebrisTemplateId };
            template.FuncGroups.Add(new DoodadFuncGroups { Id = DebrisTemplateId,
                GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start });
            foreach (var field in typeof(DoodadManager).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(field => field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
                field.SetValue(doodads, Activator.CreateInstance(field.FieldType));
            SetField(doodads, "_templates", new Dictionary<uint, DoodadTemplate> { [template.Id] = template });
            Replace(doodads);
            SetField(Manager, "_shipyardRewards", new Dictionary<uint, List<ShipyardReward>>
            {
                [Template.Id] = [new() { Id = 1, DoodadId = template.Id, Count = 3, OnWater = true, Radius = 12 }]
            });
        }

        private void Replace<T>(T manager) where T : class
        {
            var previous = SwapSingleton(manager);
            _restore.Add(() => SwapSingleton(previous));
        }

        public void Dispose()
        {
            // This fixture does not start spawner threads or initialize other world managers.
            World.SpawnManager = null;
            foreach (var restore in _restore.AsEnumerable().Reverse()) restore();
            _services.Dispose();
            Graph.Dispose();
        }
    }
}
