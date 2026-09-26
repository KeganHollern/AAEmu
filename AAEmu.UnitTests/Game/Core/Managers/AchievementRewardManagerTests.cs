using System.Reflection;
using System.Runtime.CompilerServices;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;

using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class AchievementRewardManagerTests
{
    [Test]
    public async Task CreateRewardMailContent_UsesR208022AchievementExpressions()
    {
        var content = AchievementRewardManager.CreateRewardMailContent(
            "Hero's Path",
            "Sage's Rune");

        await Assert.That(content.Title).IsEqualTo("title('Hero\\'s Path')");
        await Assert.That(content.Body).IsEqualTo("body('Hero\\'s Path','Sage\\'s Rune')");
    }

    [Test]
    public async Task EscapeLuaString_EscapesBackslashesAndLineBreaks()
    {
        var escaped = AchievementRewardManager.EscapeLuaString("C:\\Reward\r\nItem");

        await Assert.That(escaped).IsEqualTo("C:\\\\Reward\\r\\nItem");
    }

    [Test]
    public async Task TryCreateInventoryPlan_DirtyPartialStackUsesInventory()
    {
        var template = CreateTemplate(34138, 100);
        var bag = CreateBag(1);
        var stack = new Item(10, template, 50)
        {
            SlotType = SlotType.Inventory,
            Slot = 0,
            _holdingContainer = bag,
            IsDirty = true
        };
        bag.Items.Add(stack);
        bag.UpdateFreeSlotCount();
        var reward = new Item(0, template, 1);

        var useInventory = AchievementRewardManager.TryCreateInventoryPlan(bag, reward, out var plan);

        await Assert.That(useInventory).IsTrue();
        await Assert.That(plan.Stack).IsSameReferenceAs(stack);
        await Assert.That(plan.OldCount).IsEqualTo(50);
        await Assert.That(plan.NewCount).IsEqualTo(51);
        await Assert.That(plan.Slot).IsEqualTo(0);
    }

    [Test]
    public async Task TryCreateInventoryPlan_FullBagUsesMailFallback()
    {
        var template = CreateTemplate(34138, 1);
        var bag = CreateBag(1);
        var occupied = new Item(10, template, 1)
        {
            SlotType = SlotType.Inventory,
            Slot = 0,
            _holdingContainer = bag,
            IsDirty = false
        };
        bag.Items.Add(occupied);
        bag.UpdateFreeSlotCount();
        var reward = new Item(0, template, 1);

        var useInventory = AchievementRewardManager.TryCreateInventoryPlan(bag, reward, out var plan);

        await Assert.That(useInventory).IsFalse();
        await Assert.That(plan).IsNull();
    }

    [Test]
    public async Task GetMailAttachmentSlot_SkipsOccupiedSlot()
    {
        var template = CreateTemplate(34138, 1);
        var mailAttachments = new ItemContainer(1, SlotType.Mail, false, null)
        {
            ContainerSize = 2,
            IsDirty = false
        };
        mailAttachments.Items.Add(new Item(10, template, 1)
        {
            SlotType = SlotType.Mail,
            Slot = 0,
            _holdingContainer = mailAttachments,
            IsDirty = false
        });
        mailAttachments.UpdateFreeSlotCount();

        var slot = AchievementRewardManager.GetMailAttachmentSlot(mailAttachments);

        await Assert.That(slot).IsEqualTo(1);
    }

    [Test]
    [Arguments(SlotType.Inventory, (byte)0)]
    [Arguments(SlotType.Inventory, (byte)1)]
    [Arguments(SlotType.Bank, (byte)0)]
    [Arguments(SlotType.Equipment, (byte)0)]
    public async Task TryCreateInventoryPlan_PickupLimitUsesMailFallbackForStacksAndFreeSlots(SlotType heldLocation, byte grade)
    {
        var character = CreateInventoryOwner();
        var bag = character.Inventory.Bag;
        var heldContainer = heldLocation switch
        {
            SlotType.Bank => character.Inventory.Warehouse,
            SlotType.Equipment => character.Inventory.Equipment,
            _ => bag
        };
        var template = CreateTemplate(34138, 100);
        template.PickupLimit = 2;
        var held = new Item(10, template, 2)
        {
            OwnerId = character.Id, SlotType = heldLocation, Slot = 0, Grade = grade,
            _holdingContainer = heldContainer, IsDirty = false
        };
        heldContainer.Items.Add(held);
        heldContainer.UpdateFreeSlotCount();
        var reward = new Item(0, template, 1);

        bool useInventory;
        AchievementRewardManager.InventoryDeliveryPlan plan;
        lock (SaveManager.PersistenceSyncRoot)
            useInventory = AchievementRewardManager.TryCreateInventoryPlan(bag, reward, out plan);

        await Assert.That(useInventory).IsFalse();
        await Assert.That(plan).IsNull();
        await Assert.That(held.Count).IsEqualTo(2);
        await Assert.That(held.IsDirty).IsFalse();
        await Assert.That(reward.Count).IsEqualTo(1);
        await Assert.That(reward._holdingContainer).IsNull();
        await Assert.That(bag.GetUnusedSlot(-1)).IsGreaterThanOrEqualTo(0);
        // The caller's false branch persists the reward as a mail attachment instead.
        await Assert.That(AchievementRewardManager.GetMailAttachmentSlot(character.Inventory.MailAttachments)).IsEqualTo(0);
    }

    [Test]
    public async Task TryCreateInventoryPlan_PickupLimitAllowsTheExactLimitAndIgnoresUnclaimedMail()
    {
        var character = CreateInventoryOwner();
        var bag = character.Inventory.Bag;
        var template = CreateTemplate(34138, 100);
        template.PickupLimit = 2;
        var held = new Item(10, template, 1)
        {
            OwnerId = character.Id, SlotType = SlotType.Inventory, Slot = 0, _holdingContainer = bag
        };
        bag.Items.Add(held);
        bag.UpdateFreeSlotCount();
        character.Inventory.MailAttachments.Items.Add(new Item(11, template, 2)
        {
            OwnerId = character.Id, SlotType = SlotType.Mail, Slot = 0, _holdingContainer = character.Inventory.MailAttachments
        });
        var reward = new Item(0, template, 1);

        bool useInventory;
        AchievementRewardManager.InventoryDeliveryPlan plan;
        lock (SaveManager.PersistenceSyncRoot)
            useInventory = AchievementRewardManager.TryCreateInventoryPlan(bag, reward, out plan);

        await Assert.That(useInventory).IsTrue();
        await Assert.That(plan.Stack).IsSameReferenceAs(held);
        await Assert.That(plan.NewCount).IsEqualTo(2);
        await Assert.That(held.Count).IsEqualTo(1);
    }

    private static CharacterMock CreateInventoryOwner()
    {
        var character = new CharacterMock { Id = 1, NumInventorySlots = 10, NumBankSlots = 10 };
        character.Inventory = (Inventory)RuntimeHelpers.GetUninitializedObject(typeof(Inventory));
        foreach (var (property, type) in new[]
        {
            (nameof(Inventory.Bag), SlotType.Inventory), (nameof(Inventory.Warehouse), SlotType.Bank),
            (nameof(Inventory.Equipment), SlotType.Equipment), (nameof(Inventory.MailAttachments), SlotType.Mail)
        })
        {
            var container = new ItemContainer(character.Id, type, false, character)
            {
                Owner = character, ContainerSize = 10, IsDirty = false
            };
            typeof(Inventory).GetProperty(property, BindingFlags.Instance | BindingFlags.Public)!.SetValue(character.Inventory, container);
        }
        return character;
    }

    private static ItemTemplate CreateTemplate(uint id, int maxCount)
    {
        return new ItemTemplate
        {
            Id = id,
            Name = $"Item {id}",
            MaxCount = maxCount,
            FixedGrade = 0
        };
    }

    private static ItemContainer CreateBag(int size)
    {
        return new ItemContainer(1, SlotType.Inventory, false, null)
        {
            ContainerSize = size,
            IsDirty = false
        };
    }
}
