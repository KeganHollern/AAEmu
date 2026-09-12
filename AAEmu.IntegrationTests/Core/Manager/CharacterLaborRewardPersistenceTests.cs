using System.Buffers.Binary;

using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Char.Templates;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Trading;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;
using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData("skill", false)]
    [InlineData("skill", true)]
    [InlineData("skill_level", false)]
    [InlineData("skill_level", true)]
    [InlineData("child", false)]
    [InlineData("child", true)]
    [InlineData("placement", false)]
    [InlineData("placement", true)]
    [InlineData("specialty", false)]
    [InlineData("specialty", true)]
    public void LaborRewards_CommitWithTheDebitAndRestoreAfterFailedCheckpoint(string consumer, bool failBeforeCommit)
    {
        using var graph = new SendGraph();
        using var services = new LaborRewardServices(graph.Sender, consumer == "skill_level");
        var player = graph.Sender;
        var material = graph.AddItem(0);
        material.Count = 1;
        player.InitializeLaborCache(100, DateTime.UtcNow);
        player.ConsumedLaborPower = 11;
        Execute($"INSERT INTO accounts(account_id,labor) VALUES({player.AccountId},100) ON DUPLICATE KEY UPDATE labor=100");
        Assert.True(graph.Save.TryCommitEconomy([player]));
        var cost = consumer == "specialty" ? 60 : consumer == "child" ? 12 : 10;
        var hasExperienceEffect = consumer is "skill" or "skill_level" or "child";
        var expectedExperience = 20 + 48 * cost + (hasExperienceEffect ? 24 : 0);
        var expectedAbilityExperience = expectedExperience + 10;
        var expectedLevel = consumer == "skill_level" ? 2 : 1;
        var expectedPoints = consumer == "placement" ? 7 : 7 + 2 * cost;
        var notifications = new List<ushort>();
        var session = new Mock<ISession>();
        player.Connection = new GameConnection(session.Object) { ActiveChar = player };
        session.Setup(value => value.SendPacket(It.IsAny<byte[]>())).Callback<byte[]>(bytes =>
        {
            var typeId = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6));
            notifications.Add(typeId);
            if (typeId is SCOffsets.SCExpChangedPacket or SCOffsets.SCCharacterLaborPowerChangedPacket)
            {
                Assert.False(failBeforeCommit);
                Assert.Equal(expectedExperience, Scalar($"SELECT experience FROM characters WHERE id={player.Id}"));
                Assert.Equal(expectedPoints, Scalar($"SELECT point FROM actabilities WHERE owner={player.Id} AND id={(uint)ActabilityType.Commerce}"));
                Assert.Equal(100 - cost, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
            }
        });
        var demand = SpecialtyDemand.Create(material.TemplateId, 2, DateTime.UtcNow, new SpecialtyConfig())
            .RecordSale(DateTime.UtcNow, new SpecialtyConfig());
        bool Commit(Character character, Action<PersistenceSaveContext> write) => graph.Save.TryCommitEconomy([character], context =>
        {
            Assert.Equal(expectedExperience, player.Experience);
            Assert.Equal(expectedLevel, player.Level);
            Assert.Equal(expectedAbilityExperience, player.Abilities.Abilities[AbilityType.Fight].Exp);
            Assert.Equal(expectedPoints, player.Actability.Actabilities[(uint)ActabilityType.Commerce].Point);
            Assert.Equal(11 + cost, player.ConsumedLaborPower);
            Assert.Empty(notifications);
            write(context);
            if (failBeforeCommit)
                throw new InvalidOperationException("Failure after labor reward and source writes");
        });
        bool succeeded;
        if (consumer == "specialty")
        {
            // Specialty sale uses this common mutation outside SkillLaborBatch.
            lock (SaveManager.PersistenceSyncRoot)
            lock (AccountManager.Instance.GetAccountSyncRoot(player.AccountId))
            {
                using var inventory = new InventoryMutation(ItemTaskType.SellBackpack);
                using var labor = new CharacterLaborMutation(player);
                Assert.True(inventory.TryConsume(player.Inventory.Bag, material, 1));
                Assert.True(labor.TryConsume(60, (uint)ActabilityType.Commerce));
                succeeded = Commit(player, context =>
                {
                    labor.Save(context);
                    SpecialtyDemandStore.Save(context.Connection, context.Transaction, demand);
                });
                if (succeeded)
                {
                    inventory.Complete();
                    labor.Complete();
                    labor.Complete(); // Repeated completion cannot grant or announce rewards twice.
                }
            }
        }
        else
        {
            var skill = new Skill(new SkillTemplate
                { Id = 50, ConsumeLaborPower = 10, ActabilityGroupId = (int)ActabilityType.Commerce })
                { CommitLaborBatch = Commit };
            void Effects()
            {
                Assert.True(SkillLaborBatch.Current.Inventory.TryConsume(player.Inventory.Bag, material, 1));
                if (hasExperienceEffect)
                    player.AddExp(10, true);
                if (consumer == "child")
                {
                    var child = new Skill(new SkillTemplate
                        { Id = 51, ConsumeLaborPower = 2, ActabilityGroupId = (int)ActabilityType.Commerce });
                    Assert.True(SkillLaborBatch.Current.TryConsumeChildLabor(child));
                }
            }
            succeeded = consumer == "placement"
                ? SkillLaborBatch.RunPlacement(player, skill, 10, Effects)
                : SkillLaborBatch.Run(player, skill, true, Effects);
        }
        Assert.Equal(!failBeforeCommit, succeeded);
        Assert.Equal(succeeded ? 100 - cost : 100, player.LaborPower);
        Assert.Equal(succeeded ? expectedExperience : 20, player.Experience);
        Assert.Equal(succeeded ? expectedLevel : 1, player.Level);
        Assert.Equal(succeeded ? expectedAbilityExperience : 30, player.Abilities.Abilities[AbilityType.Fight].Exp);
        Assert.Equal(5, player.Abilities.Abilities[AbilityType.Wild].Exp);
        Assert.Equal(succeeded ? expectedPoints : 7, player.Actability.Actabilities[(uint)ActabilityType.Commerce].Point);
        Assert.Equal(succeeded ? 11 + cost : 11, player.ConsumedLaborPower);
        Assert.Equal(succeeded ? consumer == "child" ? 2 : 1 : 0,
            notifications.Count(type => type == SCOffsets.SCCharacterLaborPowerChangedPacket));
        Assert.Equal(succeeded ? consumer == "child" ? 3 : hasExperienceEffect ? 2 : 1 : 0,
            notifications.Count(type => type == SCOffsets.SCExpChangedPacket));
        Assert.Equal(succeeded && expectedLevel == 2 ? 1 : 0,
            notifications.Count(type => type == SCOffsets.SCLevelChangedPacket));
        Assert.Equal(player.LaborPower, Scalar($"SELECT labor FROM accounts WHERE account_id={player.AccountId}"));
        Assert.Equal(player.Experience, Scalar($"SELECT experience FROM characters WHERE id={player.Id}"));
        Assert.Equal(player.Level, Scalar($"SELECT level FROM characters WHERE id={player.Id}"));
        Assert.Equal(player.ConsumedLaborPower, Scalar($"SELECT consumed_lp FROM characters WHERE id={player.Id}"));
        Assert.Equal(player.Abilities.Abilities[AbilityType.Fight].Exp,
            Scalar($"SELECT exp FROM abilities WHERE owner={player.Id} AND id={(byte)AbilityType.Fight}"));
        Assert.Equal(5, Scalar($"SELECT exp FROM abilities WHERE owner={player.Id} AND id={(byte)AbilityType.Wild}"));
        Assert.Equal(player.Actability.Actabilities[(uint)ActabilityType.Commerce].Point,
            Scalar($"SELECT point FROM actabilities WHERE owner={player.Id} AND id={(uint)ActabilityType.Commerce}"));
        Assert.Equal(succeeded ? 0 : 1, Scalar($"SELECT COUNT(*) FROM items WHERE id={material.Id}"));
        if (consumer == "specialty")
        {
            using var connection = AAEmu.Commons.Utils.DB.MySQL.CreateConnection();
            Assert.Equal(succeeded, SpecialtyDemandStore.Load(connection).Any(value => value.ItemId == material.TemplateId));
        }
    }

    private sealed class LaborRewardServices : IDisposable
    {
        private readonly List<Action> _restore = [];
        public LaborRewardServices(Character player, bool levelUp)
        {
            Replace(new AccountManager(null, null, TimeProvider.System));
            var formulas = new FormulaManager();
            SetField(formulas, "<CalculationEngine>k__BackingField", new Jace.CalculationEngine());
            Replace(formulas);
            SetField(formulas, "_formulas", new Dictionary<uint, Formula>
                { [(uint)FormulaKind.ExpByLaborPower] = new Formula("labor_power * 10") });
            var experience = new ExperienceManager();
            var loader = new Mock<IExperienceLevelTemplateLoader>();
            loader.Setup(value => value.Load()).Returns([
                new ExperienceLevelTemplate { Level = 1, TotalExp = 0 },
                new ExperienceLevelTemplate { Level = 2, TotalExp = levelUp ? 500 : 1000000 },
                new ExperienceLevelTemplate { Level = 3, TotalExp = 2000000 }]);
            experience.Load(loader.Object, 3, 3);
            Replace(experience);
            var characters = new CharacterManager(null, null, null, null, null, null, null, null, null, null, null);
            SetField(characters, "_expertLimits", new Dictionary<int, ExpertLimit> { [0] = new() { UpLimit = 10000 } });
            Replace(characters);
            var world = AppConfiguration.Instance.World;
            _restore.Add(() => AppConfiguration.Instance.World = world);
            AppConfiguration.Instance.World = new WorldConfig { ExpRate = 2, ActabilityRate = 2 };
            player.Level = 1;
            typeof(Character).GetProperty(nameof(Character.Experience))!.SetValue(player, 20);
            player.Ability1 = AbilityType.Fight;
            player.Ability2 = player.Ability3 = AbilityType.None;
            player.Abilities = new CharacterAbilities(player);
            player.Abilities.Abilities[AbilityType.Fight].Exp = 30;
            player.Abilities.Abilities[AbilityType.Wild].Exp = 5;
            player.Actability = new CharacterActability(player);
            player.Actability.Actabilities[(uint)ActabilityType.Commerce] = new Actability(new ActabilityTemplate
                { Id = (uint)ActabilityType.Commerce }) { Point = 7 };
            player.AddBonus(1, new Bonus { Template = new BonusTemplate { Attribute = UnitAttribute.ExpMul, Value = 20 }, Value = 20 });
            player.AddBonus(2, new Bonus { Template = new BonusTemplate { Attribute = UnitAttribute.ExpByLaborPowerMul, Value = 100 }, Value = 100 });
        }

        private void Replace<T>(T manager) where T : class
        {
            var previous = SwapSingleton(manager);
            _restore.Add(() => SwapSingleton(previous));
        }
        public void Dispose()
        {
            foreach (var restore in _restore.AsEnumerable().Reverse())
                restore();
        }
    }
}
