using System.Buffers.Binary;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Fact]
    public void CharacterSlotScroll_AtOriginsLimitRefusesUseAndKeepsSavedBagItem()
    {
        using var graph = new SendGraph();
        using var services = new LaborBuffServices();
        var models = new ModelManager();
        SetField(models, "_modelTypes", new Dictionary<uint, ModelType>());
        var zones = new ZoneManager(null, null);
        SetField(zones, "_zones", new Dictionary<uint, Zone> { [10] = new() { ZoneKey = 10, GroupId = 50 } });
        SetField(zones, "_groups", new Dictionary<uint, ZoneGroup>());
        var oldZones = SwapSingleton(zones);
        var oldModels = SwapSingleton(models);
        var oldRules = SwapSingleton(new SkillRequirementsGameData());
        var oldPermissions = SwapSingleton(new PermissionManager(Mock.Of<IAccountManager>()));
        var oldWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig { GrowthRate = 1, ExpRate = 1 };
        try
        {
            var owner = graph.Sender;
            var world = HousingPlacementWorld();
            world.Regions = new Region[WorldManager.SECTORS_PER_CELL, WorldManager.SECTORS_PER_CELL];
            owner.ParentWorld = world;
            owner.ObjId = owner.Id;
            owner.Hp = owner.Mp = 100;
            owner.Level = 1;
            world.AddObject(owner);
            owner.Skills = new CharacterSkills(owner);
            owner.Abilities = new CharacterAbilities(owner);
            owner.InitializeLaborCache(20, DateTime.UtcNow);
            Execute($"INSERT INTO accounts(account_id,labor) VALUES({owner.AccountId},20) ON DUPLICATE KEY UPDATE labor=20");
            var itemUses = new List<uint>();
            owner.Events.OnItemUse += (_, args) => itemUses.Add(args.ItemId);
            var scroll = graph.AddItem(0, 8000002);
            scroll.Template.UseSkillId = 8000002;
            scroll.Template.UseSkillAsReagent = true;
            var laborPotion = graph.AddItem(1, 8000012);
            laborPotion.Template.UseSkillId = 8000005;
            laborPotion.Template.UseSkillAsReagent = true;
            Assert.True(graph.Save.TryCommitEconomy([owner]));
            Assert.Equal(1, Scalar($"SELECT count FROM items WHERE id={scroll.Id} AND template_id=8000002"));

            SetField(SkillManager.Instance, "_comboFollowupSkills", new HashSet<uint>());
            services.AddSkill(new SkillTemplate
            {
                Id = 8000002, TargetType = SkillTargetType.Self, TargetSelection = SkillTargetSelection.Source,
                Category = SkillCategory.Ability, DefaultGcd = true, TargetAlive = true, SourceAlive = true, MaxRange = 4,
                Effects = [ItemSpecial(8000003, SpecialType.AddCharacterSlot, SkillEffectApplicationMethod.Target)]
            });
            services.AddSkill(new SkillTemplate
            {
                Id = 8000005, TargetType = SkillTargetType.Self, TargetSelection = SkillTargetSelection.Source,
                Category = SkillCategory.Item, TargetAlive = true, SourceAlive = true, CooldownTime = 14400000,
                IgnoreGlobalCooldown = true,
                Effects = [ItemSpecial(8000006, SpecialType.AddLaborPower, SkillEffectApplicationMethod.Source, 500)]
            });
            var session = new Mock<ISession>();
            owner.Connection = new GameConnection(session.Object) { ActiveChar = owner };
            var packets = new List<byte[]>();
            session.Setup(x => x.SendPacket(It.IsAny<byte[]>())).Callback<byte[]>(packets.Add);
            void Use(Item item)
            {
                var request = new PacketStream().Write(item.Template.UseSkillId);
                new SkillItem(owner.ObjId, item.Id, item.TemplateId).Write(request);
                request.Write((byte)SkillCastTargetType.Unit).WriteBc(owner.ObjId).Write((byte)0);
                new CSStartSkillPacket { Connection = owner.Connection }.Read(request);
            }
            static ushort Opcode(byte[] packet) => BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6));

            var before = (owner.Mp, owner.LaborPower, owner.Money, owner.SkillLastUsed, owner.GlobalCooldown);
            Use(scroll);
            var error = Assert.Single(packets, p => Opcode(p) == SCOffsets.SCErrorMsgPacket);
            Assert.Equal((ushort)ErrorMessageType.AddCharacterSlotFail, BinaryPrimitives.ReadUInt16LittleEndian(error.AsSpan(8)));
            Assert.Equal(1, error[^1]); // The stock error is a player notification.
            var started = Assert.Single(packets, p => Opcode(p) == SCOffsets.SCSkillStartedPacket);
            Assert.Equal((byte)SkillResult.Failure, started[^1]);
            Assert.DoesNotContain(packets, p => Opcode(p) is SCOffsets.SCSkillFiredPacket or SCOffsets.SCItemTaskSuccessPacket or SCOffsets.SCGetSlotCountPacket);
            Assert.Empty(itemUses);
            Assert.Equal(before, (owner.Mp, owner.LaborPower, owner.Money, owner.SkillLastUsed, owner.GlobalCooldown));
            Assert.Same(scroll, owner.Inventory.Bag.GetItemByItemId(scroll.Id));
            Assert.Equal(1, scroll.Count);

            packets.Clear();
            Use(laborPotion);
            Assert.Contains(packets, p => Opcode(p) == SCOffsets.SCSkillFiredPacket);
            Assert.DoesNotContain(packets, p => Opcode(p) == SCOffsets.SCErrorMsgPacket);
            Assert.Equal(520, owner.LaborPower);
            Assert.Equal(new[] { laborPotion.TemplateId }, itemUses);
            Assert.Null(owner.Inventory.Bag.GetItemByItemId(laborPotion.Id));
            Assert.True(graph.Save.DoSave());
            var previousItems = SwapSingleton(graph.ReloadLifecycle().Items);
            try
            {
                owner.Inventory = new Inventory(owner);
                var restored = owner.Inventory.Bag.GetItemByItemId(scroll.Id);
                Assert.NotNull(restored);
                Assert.NotSame(scroll, restored);
                Assert.Equal((scroll.Id, scroll.TemplateId, owner.Id, scroll.Slot, 1),
                    (restored.Id, restored.TemplateId, restored.OwnerId, restored.Slot, restored.Count));
                Assert.Null(owner.Inventory.Bag.GetItemByItemId(laborPotion.Id));
            }
            finally
            {
                SwapSingleton(previousItems);
            }
        }
        finally
        {
            AppConfiguration.Instance.World = oldWorldConfig;
            SwapSingleton(oldPermissions);
            SwapSingleton(oldRules);
            SwapSingleton(oldModels);
            SwapSingleton(oldZones);
        }
    }

    private static SkillEffect ItemSpecial(uint id, SpecialType kind, SkillEffectApplicationMethod method, int value = 0) => new()
    {
        ApplicationMethod = method, StartLevel = 1, EndLevel = 99, Friendly = true, NonFriendly = true,
        Front = true, Back = true, Chance = 100, ConsumeItemCount = 1,
        Template = new SpecialEffect { Id = id, SpecialEffectTypeId = kind, Value1 = value }
    };
}
