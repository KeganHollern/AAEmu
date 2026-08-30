using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;

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
