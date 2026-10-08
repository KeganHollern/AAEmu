using System.Net;
using System.Net.Sockets;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

[NotInParallel]
public sealed class CharacterCraftReservationTests
{
    private static readonly FieldInfo s_items = typeof(Singleton<ItemManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo s_quests = typeof(Singleton<QuestManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo s_skills = typeof(Singleton<SkillManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private object _previousSkills;
    private object _previousItems;
    private object _previousQuests;
    private bool _previousDebugInfo;
    private CharacterMock _owner;
    private CharacterCraft _craft;
    private ItemManager _items;
    private Dictionary<ulong, Item> _allItems;
    private Dictionary<uint, ItemTemplate> _templates;

    [Before(Test)]
    public void SetUp()
    {
        _previousItems = s_items.GetValue(null);
        _previousSkills = s_skills.GetValue(null);
        var skills = new SkillManager(null, null);
        SetField(skills, "_skills", new Dictionary<uint, SkillTemplate> { [50] = new() { Id = 50 } });
        s_skills.SetValue(null, skills);
        _previousQuests = s_quests.GetValue(null);
        _previousDebugInfo = AppConfiguration.Instance.DebugInfo;
        AppConfiguration.Instance.DebugInfo = false;
        var ids = Mock.Of<IItemIdManager>();
        uint next = 1000;
        ids.GetNextId().Returns(() => next++);
        _items = new ItemManager(Mock.Of<ISkillManager>().Object, ids.Object,
            Mock.Of<IContainerIdManager>().Object, Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object);
        s_items.SetValue(null, _items);
        s_quests.SetValue(null, new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        _allItems = [];
        _templates = new Dictionary<uint, ItemTemplate>
        {
            [100] = new() { Id = 100, MaxCount = 100, BindType = ItemBindType.Normal },
            [200] = new() { Id = 200, MaxCount = 100, BindType = ItemBindType.Normal }
        };
        SetField(_items, "_allItems", _allItems);
        SetField(_items, "_templates", _templates);
        SetField(_items, "_removedItems", new List<ulong>());
        _owner = new CharacterMock { Id = 7, Name = "Crafter", Money = 100, NumInventorySlots = 10, NumBankSlots = 10 };
        var containers = new Dictionary<ulong, ItemContainer>();
        foreach (var type in Enum.GetValues<SlotType>())
        {
            if (type == SlotType.EquipmentMate) continue;
            var container = new ItemContainer(7, type, false, _owner)
                { Owner = _owner, ContainerId = (ulong)containers.Count + 1 };
            containers.Add(container.ContainerId, container);
        }
        SetField(_items, "_allPersistentContainers", containers);
        _owner.Inventory = new Inventory(_owner);
        _craft = _owner.Craft = new CharacterCraft(_owner);
    }

    [After(Test)]
    public void TearDown()
    {
        s_items.SetValue(null, _previousItems);
        s_skills.SetValue(null, _previousSkills);
        s_quests.SetValue(null, _previousQuests);
        AppConfiguration.Instance.DebugInfo = _previousDebugInfo;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReservedAssets_RejectCraftAdmissionBeforeAnyWorldOrSkillWork(bool money)
    {
        var material = Material(3);
        using var reservation = new TradeReservation();
        if (money) reservation.TryReserve(_owner, 1);
        else reservation.TryReserve(material, 1);
        _craft.Craft(Recipe(), 1, 0);
        await Assert.That(_craft.IsCrafting).IsFalse();
        await Assert.That(_allItems.Values.Single()).IsSameReferenceAs(material);
        await Assert.That(material.Count).IsEqualTo(3);
    }

    [Test]
    public async Task ReservedMaterialAtCastCompletion_CannotGrantProducts()
    {
        var material = Material(3);
        Arm(Recipe());
        using var reservation = new TradeReservation();
        reservation.TryReserve(material, 1);
        _craft.EndCraft();
        await Assert.That(_craft.IsCrafting).IsFalse();
        await Assert.That(_allItems.Values.Single()).IsSameReferenceAs(material);
        await Assert.That(material.Count).IsEqualTo(3);
        await Assert.That(TradeReservation.GetReservedCount(material)).IsEqualTo(1);
    }

    [Test]
    [Arguments("bank")]
    [Arguments("missing")]
    [Arguments("duplicates")]
    public async Task FinalPreflight_UsesAvailableBagMaterialsAndAggregatesRequirements(string failure)
    {
        var material = Material(failure == "missing" ? 2 : 3, failure == "bank");
        var recipe = Recipe();
        if (failure == "duplicates") recipe.CraftMaterials.Add(new CraftMaterial { ItemId = 100, Amount = 1 });
        Arm(recipe);
        _craft.EndCraft();
        await Assert.That(_allItems.Values.Single()).IsSameReferenceAs(material);
        await Assert.That(_craft.IsCrafting).IsFalse();
    }

    [Test]
    public async Task ProductCallback_SeesCraftBusyAndCannotReenterCompletion()
    {
        var material = Material(3);
        Arm(Recipe());
        var observed = false;
        var callbacks = 0;
        _owner.Events.OnItemGather += (_, _) =>
        {
            if (++callbacks != 1) return;
            observed = _craft.IsCrafting && Monitor.IsEntered(SaveManager.PersistenceSyncRoot);
            _craft.EndCraft();
        };
        _craft.EndCraft();
        await Assert.That(observed).IsTrue();
        await Assert.That(_owner.Inventory.Bag.Items.Single().TemplateId).IsEqualTo(200U);
        await Assert.That(_owner.Inventory.Bag.Items.Single().Count).IsEqualTo(1);
        await Assert.That(material.Count).IsEqualTo(0);
        await Assert.That(_craft.IsCrafting).IsFalse();
    }

    [Test]
    public async Task MatchingSkillCancellation_ClearsCraftWithoutGrantingOrConsuming()
    {
        var material = Material(3);
        Arm(Recipe());
        _craft.CancelFromSkill(99);
        await Assert.That(_craft.IsCrafting).IsTrue();
        _craft.CancelFromSkill(50);
        _craft.EndCraft();
        await Assert.That(_craft.IsCrafting).IsFalse();
        await Assert.That(_allItems.Values.Single()).IsSameReferenceAs(material);
        await Assert.That(material.Count).IsEqualTo(3);
    }

    [Test]
    public async Task CompletionException_ClearsBusyState()
    {
        Material(3);
        var recipe = Recipe();
        recipe.CraftProducts = null;
        Arm(recipe);
        Assert.Throws<NullReferenceException>(() => _craft.EndCraft());
        await Assert.That(_craft.IsCrafting).IsFalse();
    }

    [Test]
    [Arguments(false, false, ErrorMessageType.NotEnoughRequiredItem)]
    [Arguments(true, false, ErrorMessageType.BackpackOccupied)]
    [Arguments(true, true, ErrorMessageType.NotEnoughRequiredItem)]
    public async Task CraftAdmission_OnlyAutoEquippedProductsNeedTheBackpackSlot(
        bool backpackProduct, bool bindsOnEquip, ErrorMessageType expectedError)
    {
        var carriedPack = CarryPack();
        if (backpackProduct)
            _templates[200] = new BackpackTemplate
            {
                Id = 200, MaxCount = 1, BackpackType = BackpackType.TradePack,
                BindType = bindsOnEquip ? ItemBindType.BindOnEquip : ItemBindType.Normal
            };
        var session = new RecordingSession();
        _owner.Connection = new GameConnection(session) { ActiveChar = _owner };

        // There are no materials. A bag product must reach that check even while
        // a pack occupies the equipment slot. An auto-equipped pack must stop first.
        _craft.Craft(Recipe(), 1, 0);

        var bytes = session.Packets.Single();
        await Assert.That(BitConverter.ToUInt16(bytes, 6)).IsEqualTo(SCOffsets.SCErrorMsgPacket);
        var body = new PacketStream(bytes[8..]);
        await Assert.That(body.ReadInt16()).IsEqualTo((short)ErrorMessageType.CraftCantActAnyMore);
        await Assert.That(body.ReadInt16()).IsEqualTo((short)expectedError);
        await Assert.That(body.ReadUInt32()).IsEqualTo(0U);
        await Assert.That(body.ReadBoolean()).IsFalse();
        await Assert.That(body.Pos).IsEqualTo(body.Count);
        await Assert.That(_craft.IsCrafting).IsFalse();
        await Assert.That(_owner.Inventory.GetEquippedBySlot(EquipmentItemSlot.Backpack)).IsSameReferenceAs(carriedPack);
        await Assert.That(_owner.Inventory.Bag.Items).IsEmpty();
    }

    [Test]
    public async Task BagProductCompletion_LeavesCarriedPackEquipped()
    {
        var carriedPack = CarryPack();
        var material = Material(3);
        Arm(Recipe());

        _craft.EndCraft();

        await Assert.That(_owner.Inventory.GetEquippedBySlot(EquipmentItemSlot.Backpack)).IsSameReferenceAs(carriedPack);
        await Assert.That(_owner.Inventory.Bag.Items.Single().TemplateId).IsEqualTo(200U);
        await Assert.That(_owner.Inventory.Bag.Items.Single().Count).IsEqualTo(1);
        await Assert.That(material.Count).IsEqualTo(0);
        await Assert.That(_craft.IsCrafting).IsFalse();
    }

    private Backpack CarryPack()
    {
        var template = new BackpackTemplate { Id = 300, MaxCount = 1, BackpackType = BackpackType.TradePack };
        _templates[300] = template;
        var container = _owner.Inventory.Equipment;
        var item = new Backpack
        {
            Id = 2, TemplateId = 300, Template = template, Count = 1, OwnerId = _owner.Id,
            SlotType = SlotType.Equipment, Slot = (int)EquipmentItemSlot.Backpack, _holdingContainer = container
        };
        container.Items.Add(item);
        _allItems.Add(item.Id, item);
        return item;
    }

    private sealed class RecordingSession : ISession
    {
        internal List<byte[]> Packets { get; } = [];
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) => Packets.Add(packet.ToArray());
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }

    private static Craft Recipe() => new()
    {
        Id = 1, SkillId = 50, CraftMaterials = [new CraftMaterial { ItemId = 100, Amount = 3 }],
        CraftProducts = [new CraftProduct { ItemId = 200, Amount = 1 }]
    };

    private Item Material(int count, bool bank = false)
    {
        var container = bank ? _owner.Inventory.Warehouse : _owner.Inventory.Bag;
        var item = new Item { Id = 1, TemplateId = 100, Template = _templates[100], Count = count,
            OwnerId = 7, SlotType = container.ContainerType, Slot = 0, _holdingContainer = container };
        container.Items.Add(item);
        container.UpdateFreeSlotCount();
        _allItems.Add(1, item);
        return item;
    }

    private void Arm(Craft recipe)
    {
        typeof(CharacterCraft).GetProperty("CurrentCraft", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_craft, recipe);
        typeof(CharacterCraft).GetProperty("Count", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_craft, 1);
        _craft.IsCrafting = true;
    }

    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
