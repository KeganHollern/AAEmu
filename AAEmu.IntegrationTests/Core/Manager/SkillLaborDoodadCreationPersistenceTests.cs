using System.Reflection;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Fact]
    public void SkillLabor_SpawnEffectDoesNotPublishItsPositionBeforeTheCheckpoint()
    {
        using var graph = new SendGraph();
        using var services = new LaborBuffServices();
        var idField = typeof(ObjectIdManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldIds = idField.GetValue(null);
        var ids = new ObjectIdManager();
        Assert.True(ids.Initialize());
        idField.SetValue(null, ids);
        var oldWorlds = SwapSingleton<WorldManager>(null);
        var oldZones = SwapSingleton(new ZoneManager(null, null));
        var doodads = new DoodadManager(ids, null, graph.Items, null, null);
        SetField(doodads, "_templates", new Dictionary<uint, DoodadTemplate> { [1] = new() { Id = 1 } });
        var oldDoodads = SwapSingleton(doodads);
        try
        {
            var player = graph.Sender;
            player.InitializeLaborCache(20, DateTime.UtcNow);
            Execute($"INSERT INTO accounts(account_id,labor) VALUES({player.AccountId},20) ON DUPLICATE KEY UPDATE labor=20");
            // Match a new detached transform's instance until the object is published.
            var world = new WorldInstance(new WorldTemplate { Id = 0, Name = "unpublished" }, 0, true, uint.MaxValue);
            typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(player, world);
            var checkpointReached = false;
            var skill = new Skill(new SkillTemplate { Id = 50, ConsumeLaborPower = 10 })
            {
                CommitLaborBatch = (_, _) =>
                {
                    checkpointReached = true;
                    return false;
                }
            };
            var availableId = ids.GetNextId();
            ids.ReleaseId(availableId);

            // WorldManager is absent until publication. Preparation must not register a visible position.
            var result = SkillLaborBatch.Run(player, skill, true, () =>
                new AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects.SpawnDoodad().Execute(
                    player, null, player, null, null, skill, null, DateTime.UtcNow, 1, 0, 0, 0));

            Assert.False(result);
            Assert.True(checkpointReached);
            Assert.Equal(20, player.LaborPower);
            Assert.Equal(20, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
            Assert.Equal(availableId, ids.GetNextId());
        }
        finally
        {
            SwapSingleton(oldDoodads);
            SwapSingleton(oldZones);
            SwapSingleton(oldWorlds);
            idField.SetValue(null, oldIds);
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("checkpoint")]
    [InlineData("labor")]
    public void SkillLabor_TemporaryDoodadPublishesOnlyWithPaidMaterials(string outcome)
    {
        using var graph = new SendGraph();
        using var services = new LaborBuffServices();
        var idField = typeof(ObjectIdManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldIds = idField.GetValue(null);
        var ids = new ObjectIdManager();
        Assert.True(ids.Initialize());
        idField.SetValue(null, ids);
        var oldWorld = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig { GrowthRate = 1, ExpRate = 1 };
        try
        {
            var player = graph.Sender;
            var labor = outcome == "labor" ? 0 : 20;
            player.InitializeLaborCache((short)labor, DateTime.UtcNow);
            Execute($"INSERT INTO accounts(account_id,labor) VALUES({player.AccountId},{labor}) ON DUPLICATE KEY UPDATE labor={labor}");
            var material = graph.AddItem(0);
            material.Count = 1;
            Assert.True(graph.Save.TryCommitEconomy([player]));
            var availableId = ids.GetNextId();
            ids.ReleaseId(availableId);
            var world = new WorldInstance(new WorldTemplate { Id = 1, Name = "paid_doodad" }, 0, true, 1);
            var planted = new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);
            TemporarySpawnProbe doodad = null;
            var skill = new Skill(new SkillTemplate { Id = 50, ConsumeLaborPower = 10 });
            if (outcome == "checkpoint")
                skill.CommitLaborBatch = (_, write) => graph.Save.TryCommitEconomy(SkillLaborBatch.Current.Participants, context =>
                {
                    write(context);
                    throw new InvalidOperationException("Forced temporary doodad checkpoint failure");
                });
            var success = SkillLaborBatch.Run(player, skill, true, () =>
            {
                Assert.Equal(1, player.Inventory.Bag.ConsumeItem(ItemTaskType.SkillReagents, material.TemplateId, 1, material));
                doodad = new TemporarySpawnProbe
                {
                    ObjId = ids.GetNextId(), TemplateId = 1, OwnerId = player.Id,
                    Template = new DoodadTemplate { Id = 1, TotalDoodadGrowthTime = 0 },
                    PlantTime = planted, IsPersistent = false
                };
                typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(doodad, world);
                DoodadCreation.InitializeAndSpawnTemporary(doodad);
                Assert.False(doodad.Spawned);
                Assert.Equal(DateTime.MinValue, doodad.GrowthTime);
            });
            Assert.Equal(outcome == "success", success);
            Assert.Equal(outcome == "success" ? 10 : labor, player.LaborPower);
            Assert.Equal(outcome == "success" ? 10 : labor, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
            Assert.Equal(outcome == "success" ? 0 : 1, Scalar($"SELECT COUNT(*) FROM items WHERE id={material.Id}"));
            Assert.Equal(0, Scalar("SELECT COUNT(*) FROM doodads WHERE owner_id=" + player.Id));
            Assert.Equal(outcome != "success", graph.ReloadLifecycle().Items.GetItemByItemId(material.Id) != null);
            if (outcome == "labor")
                Assert.Null(doodad);
            else
            {
                Assert.Equal(outcome == "success", doodad.Spawned);
                Assert.False(doodad.IsPersistent);
                Assert.Equal(outcome == "success" ? planted : DateTime.MinValue, doodad.GrowthTime);
            }
            Assert.Equal(outcome == "success" ? availableId + 1 : availableId, ids.GetNextId());
        }
        finally
        {
            idField.SetValue(null, oldIds);
            AppConfiguration.Instance.World = oldWorld;
        }
    }

    private sealed class TemporarySpawnProbe : Doodad
    {
        public bool Spawned { get; private set; }
        public override void Spawn() => Spawned = true;
    }
}
