using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Merchant;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Tasks.Skills;
using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class PlayerMailSendPersistenceTests
{
    [Theory]
    [InlineData(31888u, "expired")]
    [InlineData(31889u, "expired")]
    [InlineData(31888u, "interrupted")]
    [InlineData(31889u, "save failure")]
    [InlineData(31888u, "active")]
    [InlineData(31889u, "unset")]
    [InlineData(31888u, "moved")]
    [InlineData(30007u, "wrapped")]
    [InlineData(26390u, "weapon")]
    public async Task CostumeCleaner_PurchasedCleanerAndTimedBagCastPersistTogether(uint costumeId, string condition)
    {
        var ids = new Mock<IItemIdManager>();
        using var graph = new SendGraph(itemIds: ids.Object);
        using var services = new LaborBuffServices();
        var owner = graph.Sender;
        ids.Setup(service => service.GetNextId()).Returns(owner.Id + 50);
        var oldZones = SwapSingleton(new ZoneManager(null, null));
        var oldPermissions = SwapSingleton(new PermissionManager(Mock.Of<IAccountManager>()));
        var oldRequirements = SwapSingleton(new SkillRequirementsGameData());
        var itemBuffs = new ItemGameData();
        SetField(itemBuffs, "_itemGradeBuffs", new Dictionary<uint, Dictionary<byte, uint>>());
        var oldItemBuffs = SwapSingleton(itemBuffs);
        try
        {
            owner.ObjId = owner.Id;
            owner.BuyBackItems = new ItemContainer(owner.Id, SlotType.None, false, owner);
            owner.Hp = owner.Mp = 100;
            owner.ParentWorld = HousingPlacementWorld();
            owner.ParentWorld.AddObject(owner);
            owner.Abilities = new CharacterAbilities(owner);
            owner.Skills = new CharacterSkills(owner);
            var cleanerTemplate = new ItemTemplate
            {
                Id = 28242, MaxCount = 100, Price = 300000, UseSkillId = 22560, UseSkillAsReagent = true
            };
            HousingField<Dictionary<uint, ItemTemplate>>(graph.Items, "_templates").Add(28242, cleanerTemplate);
            var pack = new MerchantGoods(149);
            pack.AddItemToStock(28242, 0);
            owner.Money = 600001;
            Assert.True(StorePurchaseValidator.TryCreatePlan(pack, [new(28242, 2, ShopCurrencyType.Money)],
                graph.Items.GetTemplate, out var plan, out _));
            StorePurchaseExecutor.Execute(owner, pack, plan, [], false, true);
            var cleaner = Assert.Single(owner.Inventory.Bag.Items);
            Assert.Equal(2, cleaner.Count);
            Assert.Equal(1, owner.Money);

            var initial = condition switch
            {
                "active" => DateTime.UtcNow.Date.AddDays(-2),
                "unset" => DateTime.MinValue,
                _ => DateTime.UtcNow.Date.AddDays(-31)
            };
            SetField(graph.Items, "_grades", new Dictionary<int, GradeTemplate> { [0] = new() });
            SetField(graph.Items, "_config", new ItemConfig());
            SetField(graph.Items, "_itemUnitModifiers", new Dictionary<uint, List<BonusTemplate>>());
            services.AddTemplate(7511);
            HousingField<Dictionary<uint, List<uint>>>(SkillManager.Instance, "_taggedBuffs")[(uint)BuffConstants.ArmorBuffTag] = [];
            var costumeTemplate = new ArmorTemplate
            {
                Id = costumeId, BindType = condition == "wrapped" ? ItemBindType.BindOnUnpack : ItemBindType.BindOnEquip,
                RechargeBuffId = condition == "wrapped" ? 5797u : 7511u,
                WearableTemplate = new Wearable { SlotTypeId = 31 },
                SlotTemplate = new WearableSlot { SlotTypeId = 31 }, KindTemplate = new WearableKind { TypeId = 5 },
                ChargeLifetime = 43200, MaxCount = 1
            };
            EquipItem costume = condition == "weapon"
                ? new Weapon { Template = new WeaponTemplate { Id = costumeId, BindType = ItemBindType.BindOnPickup,
                    RechargeBuffId = 4874, ChargeLifetime = 2, MaxCount = 1 } }
                : new Armor { Template = costumeTemplate };
            costume.Id = owner.Id + 11UL;
            costume.Count = 1;
            costume.TemplateId = costumeId;
            costume.ChargeStartTime = initial;
            costume.DyeItemId = 25;
            if (condition is not ("wrapped" or "unset"))
                costume.SetFlag(ItemFlag.SoulBound);
            graph.Register(costume, 1);
            var other = graph.AddEquipment(2);
            other.ChargeStartTime = DateTime.UtcNow.Date.AddDays(-32);
            var otherTime = other.ChargeStartTime;
            Assert.True(graph.Save.TryCommitEconomy([owner]));

            var template = new SkillTemplate { Id = 22560, TargetType = SkillTargetType.Item,
                TargetSelection = SkillTargetSelection.Source, CastingTime = 5000, MaxRange = 4, TargetAlive = true,
                DefaultGcd = true, Effects = [new SkillEffect
                {
                    EffectId = 31435, Template = new SpecialEffect
                    { Id = 12446, SpecialEffectTypeId = SpecialType.RechargeItemBuff },
                    ApplicationMethod = SkillEffectApplicationMethod.Target, Friendly = true, NonFriendly = true,
                    Front = true, Back = true, Chance = 100, StartLevel = 1, EndLevel = 99, ConsumeItemCount = 1
                }] };
            services.AddSkill(template);
            var skill = new Skill(template);
            if (condition == "save failure")
                skill.CommitLaborBatch = (_, write) => graph.Save.TryCommitEconomy([owner], context =>
                {
                    write(context);
                    throw new InvalidOperationException("Fail before costume wash commit");
                });

            var before = DateTime.UtcNow;
            var source = new SkillItem(owner.ObjId, cleaner.Id, 28242);
            var target = new SkillCastItemTarget { Type = SkillCastTargetType.Item,
                ObjId = costumeId == 31889 ? 0 : owner.ObjId, Id = costume.Id };
            var result = skill.Use(owner, source, target, null, false, out _);
            if (condition is "active" or "unset")
            {
                Assert.Equal(SkillResult.InvalidTarget, result);
                Assert.Null(owner.SkillTask);
            }
            else
            {
                Assert.Equal(SkillResult.Success, result);
                var cast = Assert.IsType<CastTask>(owner.SkillTask);
                Assert.Same(owner, skill.InitialTarget);
                Assert.Equal(initial, costume.ChargeStartTime);
                Assert.Equal(2, cleaner.Count);
                if (condition == "moved")
                    Assert.True(owner.Inventory.Warehouse.AddOrMoveExistingItem(ItemTaskType.SwapItems, costume));
                if (condition == "interrupted")
                    skill.Stop(owner);
                else
                    await Task.Delay(TimeSpan.FromMilliseconds(template.CastingTime + 50));
                cast.Execute();
                cast.Execute(); // A second wakeup must not spend the cleaner twice.
            }
            var supported = costumeId is 31888 or 31889;
            if (supported)
                Assert.Equal(condition == "expired" ? 1 : 2, cleaner.Count);
            if (condition == "expired")
                Assert.InRange(costume.ChargeStartTime, before, DateTime.UtcNow);
            else
                Assert.Equal(initial, costume.ChargeStartTime);
            // Check the skill transaction before another save can write the in-memory items.
            var reloaded = graph.ReloadLifecycle().Items;
            var saved = Assert.IsAssignableFrom<EquipItem>(reloaded.GetItemByItemId(costume.Id));
            Assert.Equal(costume.GetType(), saved.GetType());
            Assert.Equal(costume.ChargeStartTime, saved.ChargeStartTime);
            Assert.Equal(25u, saved.DyeItemId);
            Assert.Equal(costume.ItemFlags, saved.ItemFlags);
            Assert.Equal(costume.UnpackTime, saved.UnpackTime);
            Assert.Equal(otherTime, reloaded.GetItemByItemId(other.Id).ChargeStartTime);
            if (supported)
                Assert.Equal(cleaner.Count, reloaded.GetItemByItemId(cleaner.Id).Count);
            Assert.Equal(1, Scalar($"SELECT money FROM characters WHERE id={owner.Id}"));
            if (condition is "expired" or "interrupted")
            {
                owner.Equipment = new EquipmentContainer(owner.Id, SlotType.Equipment, false, owner) { Owner = owner };
                Assert.True(owner.Equipment.AddOrMoveExistingItem(ItemTaskType.SwapItems, costume, (int)EquipmentItemSlot.Cosplay));
                Assert.Equal(condition == "expired", owner.Buffs.CheckBuff(7511));
            }
        }
        finally
        {
            SwapSingleton(oldItemBuffs);
            SwapSingleton(oldRequirements);
            SwapSingleton(oldPermissions);
            SwapSingleton(oldZones);
        }
    }
}
