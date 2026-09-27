using System.Reflection;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(4u, 1000, 1, false)]
    [InlineData(4u, 1000, 5, true)]
    [InlineData(2u, 33000, 5, false)]
    [InlineData(2u, 33000, 1, true)]
    public void SkillLabor_VehicleRepairCommitsKitDetailsAndAuthoredPointsTogether(
        uint effectId, int health, int kitCount, bool failBeforeCommit)
    {
        using var repair = new VehicleRepairGraph(effectId, health, kitCount);
        var graph = repair.Graph;
        var before = DateTime.UtcNow.AddSeconds(-1);
        var calls = 0;
        repair.Skill.CommitLaborBatch = (_, write) =>
        {
            calls++;
            return graph.Save.TryCommitEconomy(SkillLaborBatch.Current.Participants, context =>
            {
                write(context);
                if (failBeforeCommit)
                    throw new InvalidOperationException("Forced failure after vehicle repair writes");
            });
        };

        repair.Apply();

        Assert.Equal(1, calls);
        Assert.Equal(failBeforeCommit, repair.Skill.Cancelled);
        Assert.Equal(failBeforeCommit ? kitCount : kitCount - 1, repair.Kit.Count);
        Assert.Equal(failBeforeCommit ? kitCount : kitCount - 1,
            Scalar($"SELECT COALESCE(SUM(count),0) FROM items WHERE id={repair.Kit.Id}"));
        Assert.Equal(failBeforeCommit ? 0 : health,
            Scalar($"SELECT hp FROM slaves WHERE id={repair.Vehicle.SlaveDbId}"));
        Assert.Equal(failBeforeCommit ? 7 : 1,
            Scalar($"SELECT mp FROM slaves WHERE id={repair.Vehicle.SlaveDbId}"));
        Assert.Equal(20, graph.Sender.LaborPower);
        Assert.Equal(20, Scalar($"SELECT labor FROM accounts WHERE account_id={graph.Sender.AccountId}"));
        var restored = Assert.IsType<SummonSlave>(graph.ReloadLifecycle().Items.GetItemByItemId(repair.Vehicle.Id));
        Assert.Equal(repair.Vehicle.SlaveDbId, restored.SlaveDbId);
        Assert.Equal((byte)2, restored.SlaveType);
        Assert.Equal(failBeforeCommit ? (byte)1 : (byte)0, restored.IsDestroyed);
        Assert.Equal(restored.IsDestroyed, repair.Vehicle.IsDestroyed);
        Assert.Equal(29, Scalar($"SELECT OCTET_LENGTH(details) FROM items WHERE id={repair.Vehicle.Id}"));
        if (failBeforeCommit)
        {
            Assert.Equal(DateTime.MinValue, restored.RepairStartTime);
            Assert.Equal(DateTime.MinValue, repair.Vehicle.RepairStartTime);
            Assert.False(repair.Vehicle.IsDirty);
            Assert.Same(repair.Kit, graph.Sender.Inventory.GetItemById(repair.Kit.Id));
        }
        else
        {
            Assert.InRange(restored.RepairStartTime, before, DateTime.UtcNow);
            Assert.Equal(repair.Vehicle.RepairStartTime.Ticks / TimeSpan.TicksPerSecond,
                restored.RepairStartTime.Ticks / TimeSpan.TicksPerSecond);
        }
    }

    [Theory]
    [InlineData("healthy")]
    [InlineData("recovering")]
    [InlineData("wrong-kit")]
    [InlineData("foreign-item")]
    [InlineData("wrong-item-row")]
    [InlineData("parent-owned-row")]
    [InlineData("wrong-template-row")]
    [InlineData("missing-row")]
    public void SkillLabor_VehicleRepairRejectsInvalidTargetWithoutConsumingKit(string reason)
    {
        using var repair = new VehicleRepairGraph(4, 1000, 5);
        var graph = repair.Graph;
        switch (reason)
        {
            case "healthy":
                repair.Vehicle.IsDestroyed = 0;
                break;
            case "recovering":
                repair.Vehicle.RepairStartTime = DateTime.UtcNow;
                repair.Vehicle.IsDestroyed = 0;
                break;
            case "wrong-kit":
                repair.SetAllowedEffect(3);
                break;
            case "foreign-item":
                graph.Sender.Inventory.Bag.Items.Remove(repair.Vehicle);
                graph.Receiver.Inventory.Bag.Items.Add(repair.Vehicle);
                repair.Vehicle._holdingContainer = graph.Receiver.Inventory.Bag;
                repair.Vehicle.OwnerId = graph.Receiver.Id;
                break;
            case "wrong-item-row":
                Execute($"UPDATE slaves SET item_id={repair.Vehicle.Id + 1} WHERE id={repair.Vehicle.SlaveDbId}");
                break;
            case "parent-owned-row":
                Execute($"UPDATE slaves SET owner_type=2 WHERE id={repair.Vehicle.SlaveDbId}");
                break;
            case "wrong-template-row":
                Execute($"UPDATE slaves SET template_id=999999 WHERE id={repair.Vehicle.SlaveDbId}");
                break;
            case "missing-row":
                Execute($"DELETE FROM slaves WHERE id={repair.Vehicle.SlaveDbId}");
                break;
        }
        repair.Vehicle.IsDirty = true;
        Assert.True(graph.Save.TryCommitEconomy([graph.Sender, graph.Receiver]));
        var destroyed = repair.Vehicle.IsDestroyed;
        var time = repair.Vehicle.RepairStartTime;

        repair.Apply();

        Assert.True(repair.Skill.Cancelled);
        Assert.Equal(5, repair.Kit.Count);
        Assert.Equal(5, Scalar($"SELECT count FROM items WHERE id={repair.Kit.Id}"));
        Assert.Equal(20, graph.Sender.LaborPower);
        Assert.Equal(destroyed, repair.Vehicle.IsDestroyed);
        Assert.Equal(time, repair.Vehicle.RepairStartTime);
        Assert.False(repair.Vehicle.IsDirty);
        Assert.Equal(reason == "missing-row" ? 0 : 1,
            Scalar($"SELECT COUNT(*) FROM slaves WHERE id={repair.Vehicle.SlaveDbId} AND hp=0 AND mp=7"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkillLabor_VehicleRepairTransfersTradedScrollToCurrentOwnerOnlyAtCommit(bool failBeforeCommit)
    {
        using var repair = new VehicleRepairGraph(3, 1000, 5);
        var graph = repair.Graph;
        repair.Vehicle.Template.BindType = ItemBindType.Normal;
        Execute($"UPDATE slaves SET owner_id={graph.Receiver.Id},summoner={graph.Receiver.Id} WHERE id={repair.Vehicle.SlaveDbId}");
        if (failBeforeCommit)
            repair.Skill.CommitLaborBatch = (_, write) => graph.Save.TryCommitEconomy(
                SkillLaborBatch.Current.Participants, context =>
                {
                    write(context);
                    throw new InvalidOperationException("Forced failure after vehicle ownership transfer");
                });

        repair.Apply();

        Assert.Equal(failBeforeCommit, repair.Skill.Cancelled);
        Assert.Equal(failBeforeCommit ? 5 : 4, repair.Kit.Count);
        var expectedOwner = failBeforeCommit ? graph.Receiver.Id : graph.Sender.Id;
        Assert.Equal(expectedOwner, Scalar($"SELECT owner_id FROM slaves WHERE id={repair.Vehicle.SlaveDbId}"));
        Assert.Equal(expectedOwner, Scalar($"SELECT summoner FROM slaves WHERE id={repair.Vehicle.SlaveDbId}"));
        Assert.Equal(failBeforeCommit ? 0 : 1000, Scalar($"SELECT hp FROM slaves WHERE id={repair.Vehicle.SlaveDbId}"));
        var restored = Assert.IsType<SummonSlave>(graph.ReloadLifecycle().Items.GetItemByItemId(repair.Vehicle.Id));
        Assert.Equal(graph.Sender.Id, restored.OwnerId);
        Assert.Equal(failBeforeCommit ? (byte)1 : (byte)0, restored.IsDestroyed);
    }

    [Fact]
    public void SkillLabor_VehicleRepairRejectsSecondKitAndSurvivesCorpseSave()
    {
        using var repair = new VehicleRepairGraph(2, 33000, 5);
        var player = repair.Graph.Sender;
        var corpse = new Slave
        {
            Id = repair.Vehicle.SlaveDbId,
            TemplateId = repair.VehicleTemplate.Id,
            Template = repair.VehicleTemplate,
            OwnerType = BaseUnitType.Character,
            OwnerId = player.Id,
            Summoner = player,
            SummoningItem = repair.Vehicle,
            Hp = 0,
            Mp = 7
        };

        repair.Apply();
        Assert.False(repair.Skill.Cancelled);
        var repairedAt = repair.Vehicle.RepairStartTime;
        var second = repair.NewSkill();
        repair.Apply(second);

        Assert.True(second.Cancelled);
        Assert.Equal(4, repair.Kit.Count);
        Assert.Equal(repairedAt, repair.Vehicle.RepairStartTime);
        using (var connection = MySQL.CreateConnection())
            Assert.True(corpse.Save(connection, null));
        Assert.Equal(0, corpse.Hp);
        Assert.Equal(33000, Scalar($"SELECT hp FROM slaves WHERE id={repair.Vehicle.SlaveDbId}"));
        Assert.Equal(1, Scalar($"SELECT mp FROM slaves WHERE id={repair.Vehicle.SlaveDbId}"));
        Assert.Equal(4, Scalar($"SELECT count FROM items WHERE id={repair.Kit.Id}"));
        var restored = Assert.IsType<SummonSlave>(repair.Graph.ReloadLifecycle().Items.GetItemByItemId(repair.Vehicle.Id));
        Assert.Equal(repairedAt.Ticks / TimeSpan.TicksPerSecond, restored.RepairStartTime.Ticks / TimeSpan.TicksPerSecond);
    }

    private sealed class VehicleRepairGraph : IDisposable
    {
        public SendGraph Graph { get; } = new();
        private readonly LaborBuffServices _services = new();
        private readonly SlaveGameData _data = new();
        private readonly SlaveGameData _oldData;
        private readonly ZoneManager _oldZones;
        private readonly uint _effectId;
        private readonly int _health;
        public Item Kit { get; }
        public SummonSlave Vehicle { get; }
        public SlaveTemplate VehicleTemplate { get; }
        public Skill Skill { get; }

        public VehicleRepairGraph(uint effectId, int health, int kitCount)
        {
            _effectId = effectId;
            _health = health;
            _oldData = SwapSingleton(_data);
            _oldZones = SwapSingleton(new ZoneManager(null, null));
            var owner = Graph.Sender;
            owner.ObjId = owner.Id;
            owner.InitializeLaborCache(20, DateTime.UtcNow);
            Execute($"INSERT INTO accounts(account_id,labor) VALUES({owner.AccountId},20) ON DUPLICATE KEY UPDATE labor=20");
            Kit = Graph.AddItem(0);
            Kit.Count = kitCount;
            Kit.Template.UseSkillId = effectId switch { 2 => 16561u, 3 => 17595u, _ => 17596u };
            Kit.Template.UseSkillAsReagent = true;
            VehicleTemplate = new SlaveTemplate { Id = effectId switch { 2 => 21u, 3 => 94u, _ => 46u }, Name = "Repair fixture" };
            Vehicle = new SummonSlave(owner.Id + 11UL,
                new SummonSlaveTemplate { Id = owner.Id + 3, SlaveId = VehicleTemplate.Id, MaxCount = 1,
                    BindType = ItemBindType.BindOnPickup, FixedGrade = -1 }, 1)
            {
                SlaveType = 2, SlaveDbId = owner.Id + 80, IsDestroyed = 1,
                RepairStartTime = DateTime.MinValue
            };
            RegisterVehicle();
            SetAllowedEffect(effectId);
            ((Dictionary<uint, SlaveTemplate>)typeof(SlaveGameData)
                .GetField("_slaveTemplates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_data)!)[VehicleTemplate.Id] = VehicleTemplate;
            Execute($"INSERT INTO slaves(id,item_id,template_id,owner_type,owner_id,summoner,name,hp,mp) " +
                $"VALUES({Vehicle.SlaveDbId},{Vehicle.Id},{VehicleTemplate.Id},0,{owner.Id},{owner.Id},'Repair fixture',0,7)");
            Skill = NewSkill();
            Assert.True(Graph.Save.TryCommitEconomy([owner]));
        }

        private void RegisterVehicle()
        {
            Vehicle.OwnerId = Graph.Sender.Id;
            Vehicle.SlotType = SlotType.Inventory;
            Vehicle.Slot = 1;
            Vehicle.CreateTime = DateTime.UtcNow;
            Vehicle._holdingContainer = Graph.Sender.Inventory.Bag;
            ((Dictionary<uint, ItemTemplate>)typeof(ItemManager)
                .GetField("_templates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Graph.Items)!)[Vehicle.TemplateId] = Vehicle.Template;
            Assert.True(Graph.Items.AddItem(Vehicle));
            Graph.Sender.Inventory.Bag.Items.Add(Vehicle);
            Graph.Sender.Inventory.Bag.UpdateFreeSlotCount();
        }

        public void SetAllowedEffect(uint effectId) =>
            ((Dictionary<uint, uint>)typeof(SlaveGameData)
                .GetField("_repairableSlaves", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_data)!)[VehicleTemplate.Id] = effectId;

        public Skill NewSkill()
        {
            var template = new SkillTemplate { Id = Kit.Template.UseSkillId, TargetType = SkillTargetType.Item,
                TargetSelection = SkillTargetSelection.Target, ConsumeLaborPower = 0 };
            template.Effects.Add(new SkillEffect
            {
                Template = new RepairSlaveEffect { Id = _effectId, Health = _health, Mana = 1 },
                StartLevel = 0, EndLevel = 99, Friendly = true, NonFriendly = true,
                Front = true, Back = true, Chance = 100, ApplicationMethod = SkillEffectApplicationMethod.Target,
                ConsumeSourceItem = false, ConsumeItemCount = 1
            });
            _services.AddSkill(template);
            return new Skill(template);
        }

        public void Apply(Skill skill = null)
        {
            Graph.Sender.SkillCancelled = false;
            (skill ?? Skill).ApplyEffects(Graph.Sender,
                new SkillItem(Graph.Sender.ObjId, Kit.Id, Kit.TemplateId), Graph.Sender,
                new SkillCastItemTarget { ObjId = Graph.Sender.ObjId, Id = Vehicle.Id }, null);
        }

        public void Dispose()
        {
            SwapSingleton(_oldZones);
            SwapSingleton(_oldData);
            _services.Dispose();
            Graph.Dispose();
        }
    }
}
