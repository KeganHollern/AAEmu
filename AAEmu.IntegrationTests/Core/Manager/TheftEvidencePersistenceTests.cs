using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Duels;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.StaticValues;

using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(FactionsEnum.NuiaAlliance, FactionsEnum.NuiaAlliance, true)]
    [InlineData(FactionsEnum.NuiaAlliance, FactionsEnum.Elf, true)]
    [InlineData(FactionsEnum.Elf, FactionsEnum.NuiaAlliance, true)]
    [InlineData(FactionsEnum.NuiaAlliance, FactionsEnum.HaranyaAlliance, false)]
    [InlineData(FactionsEnum.HaranyaAlliance, FactionsEnum.NuiaAlliance, false)]
    [InlineData(FactionsEnum.NuiaAlliance, FactionsEnum.Pirate, false)]
    [InlineData(FactionsEnum.Pirate, FactionsEnum.NuiaAlliance, false)]
    [InlineData(FactionsEnum.Pirate, FactionsEnum.Pirate, true)]
    [InlineData(FactionsEnum.Neutral, FactionsEnum.NuiaAlliance, false)]
    [InlineData(FactionsEnum.Invalid, FactionsEnum.NuiaAlliance, false)]
    [InlineData(FactionsEnum.NuiaAlliance, FactionsEnum.Neutral, false)]
    [InlineData(FactionsEnum.NuiaAlliance, FactionsEnum.Invalid, false)]
    [InlineData(FactionsEnum.NuiaAlliance, (FactionsEnum)999999, false)]
    public void Theft_OfflineOwner_UsesStoredFactionRelations(FactionsEnum actorFaction, FactionsEnum ownerFaction, bool eligible)
    {
        using var graph = new SendGraph();
        using var services = new TheftServices();
        graph.Sender.Faction = services.Factions.GetFaction(actorFaction);
        Execute($"UPDATE characters SET faction_id={(uint)ownerFaction} WHERE id={graph.Receiver.Id}");
        var property = TheftProperty(graph.Receiver.Id);

        Assert.Equal(eligible, services.Crime.CanGenerateTheftEvidence(graph.Sender, property));
        Assert.Null(WorldManager.Instance.GetCharacterById(graph.Receiver.Id));
    }

    [Fact]
    public void Theft_OwnerFactionChanges_DoNotUseCachedOrUnsavedDatabaseValues()
    {
        using var graph = new SendGraph();
        using var services = new TheftServices();
        graph.Sender.Faction = services.Factions.GetFaction(FactionsEnum.NuiaAlliance);
        var property = TheftProperty(graph.Receiver.Id);
        Execute($"UPDATE characters SET faction_id=148 WHERE id={graph.Receiver.Id}");
        Assert.True(services.Crime.CanGenerateTheftEvidence(graph.Sender, property));

        Execute($"UPDATE characters SET faction_id=161 WHERE id={graph.Receiver.Id}");
        Assert.False(services.Crime.CanGenerateTheftEvidence(graph.Sender, property));
        graph.Receiver.ObjId = graph.Receiver.Id;
        graph.Receiver.Faction = services.Factions.GetFaction(FactionsEnum.NuiaAlliance);
        Assert.True(WorldManager.Instance.TryAddCharacter(graph.Receiver));
        Assert.True(services.Crime.CanGenerateTheftEvidence(graph.Sender, property));

        graph.Receiver.Faction = services.Factions.GetFaction(FactionsEnum.Pirate);
        Execute($"UPDATE characters SET faction_id=148 WHERE id={graph.Receiver.Id}");
        Assert.False(services.Crime.CanGenerateTheftEvidence(graph.Sender, property));
        graph.Receiver.Faction = null;
        Assert.False(services.Crime.CanGenerateTheftEvidence(graph.Sender, property));
        Assert.True(WorldManager.Instance.TryRemoveCharacter(graph.Receiver.ObjId));
        Assert.True(services.Crime.CanGenerateTheftEvidence(graph.Sender, property));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Theft_MissingOrDeletedOwner_DoesNotUseAStaleWorldObject(bool activeOwner)
    {
        using var graph = new SendGraph();
        using var services = new TheftServices();
        graph.Sender.Faction = graph.Receiver.Faction = services.Factions.GetFaction(FactionsEnum.NuiaAlliance);
        graph.Receiver.ObjId = graph.Receiver.Id;
        if (activeOwner)
            Assert.True(WorldManager.Instance.TryAddCharacter(graph.Receiver));
        Execute($"UPDATE characters SET faction_id=148, deleted=1 WHERE id={graph.Receiver.Id}");
        var property = TheftProperty(graph.Receiver.Id);
        Assert.False(services.Crime.CanGenerateTheftEvidence(graph.Sender, property));
        Assert.Null(services.Crime.GenerateEvidenceFromTheft(graph.Sender, property));

        Execute($"DELETE FROM characters WHERE id={graph.Receiver.Id}");
        Assert.False(services.Crime.CanGenerateTheftEvidence(graph.Sender, property));
        Assert.Null(services.Crime.GenerateEvidenceFromTheft(graph.Sender, property));
    }

    [Theory]
    [InlineData(FactionsEnum.NuiaAlliance)]
    [InlineData(FactionsEnum.Elf)]
    public void Theft_ActiveOwnerWithInvalidFaction_DoesNotUseTheFriendlyMotherFactionFallback(FactionsEnum actorFaction)
    {
        using var graph = new SendGraph();
        using var services = new TheftServices();
        graph.Sender.Faction = services.Factions.GetFaction(actorFaction);
        graph.Receiver.Faction = new SystemFaction { Id = FactionsEnum.Invalid };
        graph.Receiver.ObjId = graph.Receiver.Id;
        Execute($"UPDATE characters SET faction_id=148 WHERE id={graph.Receiver.Id}");
        Assert.True(WorldManager.Instance.TryAddCharacter(graph.Receiver));

        var property = TheftProperty(graph.Receiver.Id);
        Assert.False(services.Crime.CanGenerateTheftEvidence(graph.Sender, property));
        Assert.Null(services.Crime.GenerateEvidenceFromTheft(graph.Sender, property));
    }

    [Fact]
    public void Theft_CurrentFactionRelationChanges_ApplyWithoutAnOwnerCache()
    {
        using var graph = new SendGraph();
        using var services = new TheftServices();
        graph.Sender.Faction = services.Factions.GetFaction(FactionsEnum.Elf);
        Execute($"UPDATE characters SET faction_id=149 WHERE id={graph.Receiver.Id}");
        var property = TheftProperty(graph.Receiver.Id);
        var relation = services.Factions.GetFaction(FactionsEnum.NuiaAlliance).Relations[FactionsEnum.HaranyaAlliance];
        Assert.False(services.Crime.CanGenerateTheftEvidence(graph.Sender, property));

        relation.State = RelationState.Friendly;
        Assert.True(services.Crime.CanGenerateTheftEvidence(graph.Sender, property));
        relation.State = RelationState.Neutral;
        Assert.False(services.Crime.CanGenerateTheftEvidence(graph.Sender, property));
        relation.State = RelationState.Hostile;
        Assert.False(services.Crime.CanGenerateTheftEvidence(graph.Sender, property));
    }

    [Fact]
    public void Theft_ActiveDuel_DoesNotChangeFriendlyPropertyEligibility()
    {
        using var graph = new SendGraph();
        using var services = new TheftServices();
        graph.Sender.Faction = graph.Receiver.Faction = services.Factions.GetFaction(FactionsEnum.NuiaAlliance);
        Execute($"UPDATE characters SET faction_id=148 WHERE id={graph.Receiver.Id}");
        graph.Receiver.ObjId = graph.Receiver.Id;
        Assert.True(WorldManager.Instance.TryAddCharacter(graph.Receiver));
        var duels = new DuelManager();
        var oldDuels = SwapSingleton(duels);
        try
        {
            var duel = new Duel(graph.Sender, graph.Receiver) { Active = true };
            SetField(duels, "_duels", new Dictionary<uint, Duel>
            {
                [graph.Sender.Id] = duel,
                [graph.Receiver.Id] = duel
            });
            Assert.Equal(RelationState.Hostile, graph.Sender.GetRelationStateTo(graph.Receiver));
            Assert.True(services.Crime.CanGenerateTheftEvidence(graph.Sender, TheftProperty(graph.Receiver.Id)));
        }
        finally
        {
            SwapSingleton(oldDuels);
        }
    }

    [Theory]
    [InlineData("friendly", false, true)]
    [InlineData("friendly", true, true)]
    [InlineData("hostile", false, false)]
    [InlineData("hostile", true, false)]
    [InlineData("pirate", false, false)]
    [InlineData("neutral", false, false)]
    [InlineData("public", false, false)]
    [InlineData("non_crime_skill", false, false)]
    [InlineData("self", false, false)]
    [InlineData("system", false, false)]
    public void Theft_DoodadUse_PreservesLootAndCreatesOnlyEligibleFootprints(string scenario, bool skillLess, bool evidence)
    {
        using var graph = new SendGraph();
        using var buffs = new LaborBuffServices();
        using var services = new TheftServices();
        graph.Sender.Faction = services.Factions.GetFaction(FactionsEnum.NuiaAlliance);
        var ownerFaction = scenario switch
        {
            "hostile" => FactionsEnum.HaranyaAlliance,
            "pirate" => FactionsEnum.Pirate,
            "neutral" => FactionsEnum.Neutral,
            _ => FactionsEnum.NuiaAlliance
        };
        Execute($"UPDATE characters SET faction_id={(uint)ownerFaction} WHERE id={graph.Receiver.Id}");
        var world = HousingPlacementWorld();
        world.Regions = new Region[WorldManager.SECTORS_PER_CELL, WorldManager.SECTORS_PER_CELL];
        world.SpawnManager = new SpawnManager(world);
        GC.SuppressFinalize(world);
        SetParentWorld(graph.Sender, world);
        graph.Sender.Transform.Local.Position = new Vector3(100, 200, 300);
        var loot = graph.AddItem(0);
        var countBefore = loot.Count;
        var ids = (IItemIdManager)typeof(ItemManager)
            .GetField("<itemIdManager>P", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(graph.Items)!;
        Mock.Get(ids).Setup(manager => manager.GetNextId()).Returns(graph.Sender.Id + 90);
        SetField(SkillManager.Instance, "_skills", new Dictionary<uint, SkillTemplate>
        {
            [50] = new() { Id = 50, CrimePoint = scenario == "non_crime_skill" ? 0 : 1 }
        });
        var objectIds = new Mock<IObjectIdManager>();
        objectIds.Setup(allocator => allocator.GetNextId()).Returns(graph.Sender.Id + 80);
        var manager = new DoodadManager(objectIds.Object, Mock.Of<IDoodadIdManager>(), graph.Items,
            new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>()), null);
        var oldDoodads = SwapSingleton(manager);
        var oldWorld = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig { GrowthRate = 1, LootRate = 1 };
        var doodadIdField = typeof(DoodadIdManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldIds = doodadIdField.GetValue(null);
        var doodadIds = new DoodadIdManager();
        Assert.True(doodadIds.Initialize());
        doodadIdField.SetValue(null, doodadIds);
        try
        {
            SetField(manager, "_templates", new Dictionary<uint, DoodadTemplate>
            {
                [DoodadConstants.FootprintMale] = new() { Id = DoodadConstants.FootprintMale },
                [DoodadConstants.FootprintFemale] = new() { Id = DoodadConstants.FootprintFemale }
            });
            SetField(manager, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>>
            {
                [1] = [new() { GroupId = 1, FuncId = 1, FuncType = nameof(DoodadFuncLootItem), SkillId = skillLess ? 0u : 50u, NextPhase = 2 }]
            });
            SetField(manager, "_funcTemplates", new Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>
            {
                [nameof(DoodadFuncLootItem)] = new() { [1] = new DoodadFuncLootItem
                    { Id = 1, ItemId = loot.TemplateId, CountMin = 1, CountMax = 1, Percent = 10000 } }
            });
            SetField(manager, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>());
            var property = TheftProperty(scenario == "self" ? graph.Sender.Id : graph.Receiver.Id);
            property.Template = new DoodadTemplate { Id = 100 };
            property.TemplateId = 100;
            property.ParentWorld = world;
            property.Transform.Local.Position = new Vector3(105, 203, 301);
            property.FuncGroupId = 1;
            if (scenario == "system") property.OwnerType = DoodadOwnerType.System;
            if (scenario == "public") property.PlantTime = DateTime.UtcNow.AddHours(-25);

            property.Use(graph.Sender, skillLess ? 0u : 50u);

            Assert.Equal(countBefore + 1, graph.Sender.Inventory.Bag.Items.Where(item => item.TemplateId == loot.TemplateId).Sum(item => item.Count));
            Assert.Equal(2u, property.FuncGroupId);
            Assert.Equal(0, graph.Sender.CrimePoint);
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM crime WHERE criminal={graph.Sender.Id}"));
            Assert.Equal(evidence ? 1 : 0, Scalar($"SELECT COUNT(*) FROM doodads WHERE owner_id={graph.Sender.Id} AND template_id={DoodadConstants.FootprintMale}"));
            if (evidence)
            {
                var footprint = world.GetDoodad(graph.Sender.Id + 80);
                Assert.NotNull(footprint);
                Assert.True(footprint.IsVisible);
                Assert.Same(world.GetRegionByPos(property.Transform.World.Position), footprint.Region);
                Assert.Equal(graph.Receiver.Id, Scalar($"SELECT data FROM doodads WHERE owner_id={graph.Sender.Id}"));
                Assert.Equal(100, Scalar($"SELECT item_template_id FROM doodads WHERE owner_id={graph.Sender.Id}"));
                Assert.Equal(105, Scalar($"SELECT x FROM doodads WHERE owner_id={graph.Sender.Id}"));
            }
        }
        finally
        {
            SwapSingleton(oldDoodads);
            AppConfiguration.Instance.World = oldWorld;
            doodadIdField.SetValue(null, oldIds);
        }
    }

    private static Doodad TheftProperty(uint ownerId) => new()
    {
        OwnerId = ownerId, OwnerType = DoodadOwnerType.Character, PlantTime = DateTime.UtcNow
    };

    private sealed class TheftServices : IDisposable
    {
        private readonly FactionManager _oldFactions;
        private readonly CrimeManager _oldCrime;
        public FactionManager Factions { get; } = new(null);
        public CrimeManager Crime { get; } = new();

        public TheftServices()
        {
            var nuia = new SystemFaction { Id = FactionsEnum.NuiaAlliance };
            var haranya = new SystemFaction { Id = FactionsEnum.HaranyaAlliance };
            var outlaw = new SystemFaction { Id = (FactionsEnum)114 };
            var pirate = new SystemFaction { Id = FactionsEnum.Pirate, MotherId = outlaw.Id };
            foreach (var first in new[] { nuia, haranya, outlaw })
                foreach (var second in new[] { nuia, haranya, outlaw }.Where(other => other != first))
                    first.Relations.Add(second.Id, new FactionRelation { Id = first.Id, Id2 = second.Id, State = RelationState.Hostile });
            SetField(Factions, "_systemFactions", new Dictionary<FactionsEnum, SystemFaction>
            {
                [nuia.Id] = nuia,
                [haranya.Id] = haranya,
                [pirate.Id] = pirate,
                [outlaw.Id] = outlaw,
                [FactionsEnum.Elf] = new() { Id = FactionsEnum.Elf, MotherId = nuia.Id },
                [FactionsEnum.Neutral] = new() { Id = FactionsEnum.Neutral }
            });
            _oldFactions = SwapSingleton(Factions);
            _oldCrime = SwapSingleton(Crime);
        }

        public void Dispose()
        {
            SwapSingleton(_oldCrime);
            SwapSingleton(_oldFactions);
        }
    }
}
