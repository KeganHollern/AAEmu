using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Units;

using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class PlayerMailSendPersistenceTests
{
    private static int _nextId = 1100000;

    [Theory]
    [InlineData(MailType.Normal, 310)]
    [InlineData(MailType.Express, 460)]
    public void SuccessfulSend_ReloadsExactAttachmentsRecipientAndPaidMoney(MailType type, int cost)
    {
        using var graph = new SendGraph();
        var first = graph.AddItem(0);
        Assert.True(graph.Save.TryCommitEconomy([graph.Sender]));
        first.Count = 7; // A previously saved attachment also has unsaved source changes.
        var second = graph.AddEquipment(1);
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM items WHERE id={second.Id}"));

        Assert.Equal(MailResult.Success, graph.Send(type, 200, 0, 1));

        graph.AssertCommitted(type, cost, 200, first, second);
        graph.AssertReloaded(type, cost, 200, first, second);
    }

    [Theory]
    [InlineData("mails", MailType.Normal, 310)]
    [InlineData("items", MailType.Normal, 310)]
    [InlineData("characters", MailType.Normal, 310)]
    [InlineData("mails", MailType.Express, 460)]
    [InlineData("items", MailType.Express, 460)]
    [InlineData("characters", MailType.Express, 460)]
    public void SqlFailure_RestoresDirtySourcesAndWallet_ThenRetryCommitsOnce(string table, MailType type, int cost)
    {
        using var graph = new SendGraph();
        var first = graph.AddItem(0);
        Assert.True(graph.Save.TryCommitEconomy([graph.Sender]));
        first.Count = 7;
        var second = graph.AddEquipment(1);
        var trigger = $"player_mail_fail_{graph.Sender.Id}";
        var targetId = table switch
        {
            "characters" => graph.Sender.Id,
            "items" => second.Id,
            _ => graph.NextMailId
        };
        Execute($"CREATE TRIGGER {trigger} BEFORE INSERT ON {table} FOR EACH ROW BEGIN IF NEW.id={targetId} THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected player mail save failure'; END IF; END");
        try
        {
            Assert.Equal(MailResult.MailErrorOccurred, graph.Send(type, 200, 0, 1));
            Assert.Equal(10000L, graph.Sender.Money);
            Assert.Equal(10000, Scalar($"SELECT money FROM characters WHERE id={graph.Sender.Id}"));
            Assert.Empty(graph.Mails._allPlayerMails);
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mails WHERE sender_id={graph.Sender.Id}"));
            Assert.Equal(5, Scalar($"SELECT count FROM items WHERE id={first.Id}"));
            Assert.Equal(graph.Sender.Id, Scalar($"SELECT owner FROM items WHERE id={first.Id}"));
            Assert.Equal((long)SlotType.Inventory, Scalar($"SELECT slot_type FROM items WHERE id={first.Id}"));
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM items WHERE id={second.Id}"));
            Assert.Equal(7, first.Count);
            Assert.Equal(new Item[] { first, second }, graph.Sender.Inventory.Bag.Items);
            foreach (var item in new Item[] { first, second })
            {
                Assert.Same(item, graph.Items.GetItemByItemId(item.Id));
                Assert.Same(graph.Sender.Inventory.Bag, item._holdingContainer);
                Assert.Equal(graph.Sender.Id, item.OwnerId);
                Assert.Equal(SlotType.Inventory, item.SlotType);
                Assert.True(item.IsDirty);
            }
            Assert.Equal(0, first.Slot);
            Assert.Equal(1, second.Slot);
            Assert.Empty(graph.Receiver.Inventory.MailAttachments.Items);
            Assert.Single(graph.ReleasedMailIds);
        }
        finally
        {
            Execute($"DROP TRIGGER {trigger}");
        }

        Assert.Equal(MailResult.Success, graph.Send(type, 200, 0, 1));
        Assert.Equal(MailResult.IncorrectItemInformation, graph.Send(type, 200, 0, 1));
        graph.AssertCommitted(type, cost, 200, first, second);
        graph.AssertReloaded(type, cost, 200, first, second);
    }

    [Fact]
    public async Task ConcurrentAttachmentReplay_CommitsOneMailAndOneDebit()
    {
        using var graph = new SendGraph();
        var item = graph.AddEquipment(0);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            graph.Send(MailType.Express, 200, 0))));

        Assert.Single(results, result => result == MailResult.Success);
        Assert.Single(results, result => result == MailResult.IncorrectItemInformation);
        graph.AssertCommitted(MailType.Express, 380, 200, item);
        graph.AssertReloaded(MailType.Express, 380, 200, item);
    }

    private sealed class SendGraph : IDisposable
    {
        private readonly ItemManager _oldItems;
        private readonly QuestManager _oldQuests;
        private readonly WorldManager _oldWorld;
        private readonly Dictionary<ulong, Item> _allItems = [];
        private readonly Dictionary<uint, ItemTemplate> _templates = [];
        private readonly IWorldManager _world;
        private readonly ITaskManager _tasks = Mock.Of<ITaskManager>();
        private readonly Mock<IMailIdManager> _mailIds = new();
        private readonly NameManager _names = new();
        private uint _nextMailId;

        public ItemManager Items { get; }
        public MailManager Mails { get; }
        public SaveManager Save { get; }
        public Character Sender { get; }
        public Character Receiver { get; }
        public uint NextMailId => _nextMailId + 1;
        public List<uint> ReleasedMailIds { get; } = [];

        public SendGraph()
        {
            var id = (uint)Interlocked.Add(ref _nextId, 100);
            _nextMailId = id + 50;
            var world = new Mock<IWorldManager>();
            // Explicit participants must save the sender even when no online character is enumerated.
            world.Setup(manager => manager.GetAllCharacters()).Returns([]);
            _world = world.Object;
            Items = NewItemStore();
            _oldItems = SwapSingleton(Items);
            _oldQuests = SwapSingleton(new QuestManager(_tasks, Mock.Of<IZoneManager>()));
            _oldWorld = SwapSingleton(new WorldManager(Mock.Of<ITickManager>(), Mock.Of<IWorldIdManager>(),
                new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>()),
                new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>()),
                new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>())));
            SetField(Items, "_allItems", _allItems);
            SetField(Items, "_removedItems", new List<ulong>());
            SetField(Items, "_templates", _templates);
            var containers = new Dictionary<ulong, ItemContainer>();
            SetField(Items, "_allPersistentContainers", containers);
            Sender = Character(id, "Sender");
            Receiver = Character(id + 1, "Receiver");
            ulong nextContainer = id * 10UL;
            foreach (var character in new[] { Sender, Receiver })
            {
                foreach (var type in Enum.GetValues<SlotType>().Where(type => type != SlotType.EquipmentMate))
                {
                    var container = new ItemContainer(character.Id, type, false, character)
                        { ContainerId = ++nextContainer, Owner = character };
                    containers.Add(container.ContainerId, container);
                }
                character.Inventory = new Inventory(character);
                character.Mails = new CharacterMails(character);
            }
            _names.Load([], [], []);
            _names.AddCharacter(Sender.Id, Sender.Name, Sender.AccountId);
            _names.AddCharacter(Receiver.Id, Receiver.Name, Receiver.AccountId);
            _mailIds.Setup(manager => manager.GetNextId()).Returns(() => ++_nextMailId);
            _mailIds.Setup(manager => manager.ReleaseId(It.IsAny<uint>()))
                .Callback<uint>(ReleasedMailIds.Add);
            Mails = NewMailStore(Items);
            Save = new SaveManager(_tasks, Mock.Of<IHousingManager>(), Mails, Items,
                Mock.Of<IAuctionManager>(), Mock.Of<ICrimeManager>(), _world, Mock.Of<IZoneManager>());
            Assert.True(Save.TryCommitEconomy([Sender]));
        }

        public Item AddItem(byte slot)
        {
            var template = new ItemTemplate { Id = Sender.Id + 2, MaxCount = 100,
                BindType = ItemBindType.Normal, FixedGrade = -1, Gradable = true };
            return Register(new Item(Sender.Id + 10UL + slot, template, 5), slot);
        }

        public EquipItem AddEquipment(byte slot)
        {
            var template = new EquipItemTemplate { Id = Sender.Id + 3, MaxCount = 1,
                BindType = ItemBindType.Normal, FixedGrade = -1, Gradable = true };
            return (EquipItem)Register(new EquipItem(Sender.Id + 10UL + slot, template, 1)
            {
                Grade = 4, UccId = 753, ImageItemTemplateId = 812,
                Durability = 67, RuneId = 91, DyeItemId = 182,
                GemIds = [11, 22, 33, 0, 0, 0, 0], TemperPhysical = 104, TemperMagical = 103
            }, slot);
        }

        private Item Register(Item item, byte slot)
        {
            item.OwnerId = Sender.Id;
            item.SlotType = SlotType.Inventory;
            item.Slot = slot;
            item.CreateTime = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
            item._holdingContainer = Sender.Inventory.Bag;
            _templates[item.TemplateId] = item.Template;
            _allItems.Add(item.Id, item);
            Sender.Inventory.Bag.Items.Add(item);
            Sender.Inventory.Bag.UpdateFreeSlotCount();
            return item;
        }

        public MailResult Send(MailType type, int copper, params byte[] slots) =>
            PlayerMailSendExecutor.Execute(Sender, type, Receiver.Name, "Persistence test", "Exact attachments",
                copper, 0, 0, slots.Select(slot => (SlotType.Inventory, slot)).ToArray(), Mails, Items, _names,
                () => Save.TryCommitEconomy([Sender]));

        public void AssertCommitted(MailType type, int cost, int copper, params Item[] attachments)
        {
            Assert.Equal(10000L - cost, Sender.Money);
            Assert.Equal(Sender.Money, Scalar($"SELECT money FROM characters WHERE id={Sender.Id}"));
            Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mails WHERE sender_id={Sender.Id}"));
            var mail = Assert.Single(Mails._allPlayerMails.Values);
            Assert.Equal(type, mail.MailType);
            Assert.Equal(Receiver.Id, mail.Header.ReceiverId);
            Assert.Equal(copper, mail.Body.CopperCoins);
            Assert.Equal(attachments, mail.Body.Attachments);
            Assert.Equal(attachments.Length + 1, mail.Header.Attachments);
            Assert.Equal(type == MailType.Express, mail.IsDelivered);
            Assert.Empty(Sender.Inventory.Bag.Items);
            Assert.Equal(copper, Scalar($"SELECT money_amount_1 FROM mails WHERE id={mail.Id}"));
            foreach (var item in attachments)
            {
                Assert.Same(item, Items.GetItemByItemId(item.Id));
                Assert.Equal(Receiver.Id, Scalar($"SELECT owner FROM items WHERE id={item.Id}"));
                Assert.Equal((long)SlotType.Mail, Scalar($"SELECT slot_type FROM items WHERE id={item.Id}"));
                Assert.Equal(item.Count, Scalar($"SELECT count FROM items WHERE id={item.Id}"));
                Assert.False(item.IsDirty);
            }
        }

        public void AssertReloaded(MailType type, int cost, int copper, params Item[] attachments)
        {
            // Use the startup loaders, with an empty world so every owner is offline.
            var reloadedItems = NewItemStore();
            var templates = new Dictionary<uint, ItemTemplate>();
            using (var connection = MySQL.CreateConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT DISTINCT template_id, type FROM items";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetUInt32("template_id");
                    var itemClass = typeof(Item).Assembly.GetType(reader.GetString("type"));
                    ItemTemplate template = itemClass != null && typeof(EquipItem).IsAssignableFrom(itemClass)
                        ? new EquipItemTemplate() : new ItemTemplate();
                    template.Id = id;
                    template.MaxCount = int.MaxValue;
                    template.FixedGrade = -1;
                    template.Gradable = true;
                    templates[id] = template;
                }
            }
            foreach (var (id, template) in _templates)
                templates[id] = template;
            SetField(reloadedItems, "_templates", templates);
            SwapSingleton(reloadedItems);
            try
            {
                reloadedItems.LoadUserItems();
                var reloadedMails = NewMailStore(reloadedItems);
                reloadedMails.Load();
                var mail = Assert.Single(reloadedMails._allPlayerMails.Values.Where(mail => mail.Header.SenderId == Sender.Id));
                Assert.Equal(Mails._allPlayerMails.Values.Single().Id, mail.Id);
                Assert.Equal(type, mail.MailType);
                Assert.Equal(Receiver.Id, mail.Header.ReceiverId);
                Assert.Equal(Receiver.Name, mail.ReceiverName);
                Assert.Equal(copper, mail.Body.CopperCoins);
                Assert.Equal(10000L - cost, Scalar($"SELECT money FROM characters WHERE id={Sender.Id}"));
                Assert.Equal(attachments.Select(item => item.Id), mail.Body.Attachments.Select(item => item.Id));
                foreach (var original in attachments)
                {
                    var restored = reloadedItems.GetItemByItemId(original.Id);
                    Assert.NotNull(restored);
                    Assert.NotSame(original, restored);
                    Assert.Same(restored, mail.Body.Attachments.Single(item => item.Id == original.Id));
                    Assert.Equal(Receiver.Id, restored.OwnerId);
                    Assert.Equal(SlotType.Mail, restored.SlotType);
                    Assert.Equal(Receiver.Id, restored._holdingContainer.OwnerId);
                    Assert.Equal(original.Count, restored.Count);
                    Assert.Equal(original.Grade, restored.Grade);
                    Assert.Equal(original.UccId, restored.UccId);
                    Assert.Equal(original.CreateTime, restored.CreateTime);
                    if (original is EquipItem equipment)
                    {
                        var loadedEquipment = Assert.IsType<EquipItem>(restored);
                        Assert.Equal(equipment.ImageItemTemplateId, loadedEquipment.ImageItemTemplateId);
                        Assert.Equal(equipment.Durability, loadedEquipment.Durability);
                        Assert.Equal(equipment.RuneId, loadedEquipment.RuneId);
                        Assert.Equal(equipment.DyeItemId, loadedEquipment.DyeItemId);
                        Assert.Equal(equipment.GemIds, loadedEquipment.GemIds);
                        Assert.Equal(equipment.TemperPhysical, loadedEquipment.TemperPhysical);
                        Assert.Equal(equipment.TemperMagical, loadedEquipment.TemperMagical);
                    }
                }
            }
            finally
            {
                SwapSingleton(Items);
            }
        }

        private ItemManager NewItemStore() => new(Mock.Of<ISkillManager>(), Mock.Of<IItemIdManager>(),
            Mock.Of<IContainerIdManager>(), Mock.Of<ILocalizationManager>(), _tasks, _world);

        private MailManager NewMailStore(IItemManager items) => new(_mailIds.Object, _names, items,
            _tasks, _world, new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>()),
            Mock.Of<ILocalizationManager>()) { _allPlayerMails = [] };

        public void Dispose()
        {
            SwapSingleton(_oldItems);
            SwapSingleton(_oldQuests);
            SwapSingleton(_oldWorld);
        }

        private static Character Character(uint id, string prefix) => new(new UnitCustomModelParams())
        {
            Id = id, AccountId = id, Name = $"{prefix}{id}", Faction = new SystemFaction(), FactionName = "",
            Slots = [], Created = DateTime.UtcNow, NumInventorySlots = 10, NumBankSlots = 10, Money = 10000
        };
    }

    private static T SwapSingleton<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var old = (T)field.GetValue(null);
        field.SetValue(null, value);
        return old;
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static long Scalar(string sql)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(string sql)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
