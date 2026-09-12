using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.StaticValues;

using Moq;
using Xunit;

using ShutdownTask = AAEmu.Game.Models.Tasks.ShutdownTask;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class TradePersistenceTests
{
    private static int _nextId = 990000;

    [Fact]
    public void SuccessfulTrade_ReloadsExactOriginalAndSplitItemsWithBothWallets()
    {
        using var graph = new TradeGraph();
        var original = graph.AddItem(graph.Owner, 20, 100, 5);
        var returnItem = graph.AddItem(graph.Target, 21, 200, 1);
        original.Grade = 3;
        original.CreateTime = new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(graph.Save.TryCommitEconomy([graph.Owner, graph.Target]));
        graph.BeginExchange(original, returnItem);
        graph.ConfirmBoth();
        graph.Trade.OkTrade(graph.Owner);
        graph.Trade.OkTrade(graph.Target);

        var split = graph.Target.Inventory.Bag.Items.Single();
        Assert.NotEqual(original.Id, split.Id);
        Assert.Equal(1, graph.Checkpoint.Calls);
        Assert.Equal(1, graph.Checkpoint.Successes);
        Assert.Equal(1, graph.PacketCount(graph.Owner, SCOffsets.SCTradeMadePacket));
        Assert.Equal(1, graph.PacketCount(graph.Target, SCOffsets.SCTradeMadePacket));
        Assert.Equal(125L, ReadWallet(graph.Owner.Id));
        Assert.Equal(75L, ReadWallet(graph.Target.Id));
        Assert.Equal(3, CountOwnedItems(graph.Owner.Id, graph.Target.Id));

        // Use the production item/container loader with a fresh manager, as on restart.
        var reloaded = graph.ReloadItems();
        AssertLoadedItem(reloaded, original.Id, graph.Owner, 3, original.Grade);
        AssertLoadedItem(reloaded, returnItem.Id, graph.Owner, 1, returnItem.Grade);
        AssertLoadedItem(reloaded, split.Id, graph.Target, 2, original.Grade);
        Assert.Equal(original.CreateTime, reloaded.GetItemByItemId(split.Id).CreateTime);
        Assert.Equal(original.CreateTime, reloaded.GetItemByItemId(original.Id).CreateTime);
        Assert.NotSame(original, reloaded.GetItemByItemId(original.Id));
        Assert.NotSame(split, reloaded.GetItemByItemId(split.Id));
    }

    [Theory]
    [InlineData("source_item")]
    [InlineData("split_item")]
    [InlineData("recipient_character")]
    public void SqlFailure_RestoresBothInventoriesAndWalletsThenFreshTradeRetriesOnce(string failure)
    {
        using var graph = new TradeGraph();
        var original = graph.AddItem(graph.Owner, 20, 100, 5);
        var returnItem = graph.AddItem(graph.Target, 21, 200, 1);
        Assert.True(graph.Save.TryCommitEconomy([graph.Owner, graph.Target]));
        var splitId = graph.ItemIds.Next;
        var (table, id) = failure switch
        {
            "source_item" => ("items", original.Id),
            "split_item" => ("items", (ulong)splitId),
            "recipient_character" => ("characters", (ulong)graph.Target.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };
        var trigger = $"trade_fail_{graph.Id}";
        Execute($"CREATE TRIGGER {trigger} BEFORE INSERT ON {table} FOR EACH ROW BEGIN IF NEW.id = {id} THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Injected trade write failure'; END IF; END");
        try
        {
            graph.BeginExchange(original, returnItem);
            graph.ConfirmBoth();
            graph.Trade.OkTrade(graph.Target);
            Assert.Equal(1, graph.Checkpoint.Calls);
            Assert.Equal(0, graph.Checkpoint.Successes);
            Assert.Same(original, Assert.Single(graph.Owner.Inventory.Bag.Items));
            Assert.Same(returnItem, Assert.Single(graph.Target.Inventory.Bag.Items));
            Assert.Equal(5, original.Count);
            Assert.Equal(100L, graph.Owner.Money);
            Assert.Equal(100L, graph.Target.Money);
            Assert.False(original.IsDirty);
            Assert.False(returnItem.IsDirty);
            Assert.Equal(0, TradeReservation.GetReservedCount(original));
            Assert.Equal(0, TradeReservation.GetReservedMoney(graph.Target));
            Assert.Equal(0, graph.PacketCount(graph.Owner, SCOffsets.SCTradeMadePacket));
            Assert.Equal(100L, ReadWallet(graph.Owner.Id));
            Assert.Equal(100L, ReadWallet(graph.Target.Id));
            Assert.Equal(2, CountOwnedItems(graph.Owner.Id, graph.Target.Id));
            Assert.Equal(0, CountItem(splitId));
            var rolledBack = graph.ReloadItems();
            AssertLoadedItem(rolledBack, original.Id, graph.Owner, 5, original.Grade);
            AssertLoadedItem(rolledBack, returnItem.Id, graph.Target, 1, returnItem.Grade);
            Assert.Null(rolledBack.GetItemByItemId(splitId));
            graph.RestoreItemManager();
        }
        finally
        {
            Execute($"DROP TRIGGER {trigger}");
        }

        graph.BeginExchange(original, returnItem);
        graph.ConfirmBoth();
        graph.Trade.OkTrade(graph.Owner);
        graph.Trade.OkTrade(graph.Target);
        Assert.Equal(2, graph.Checkpoint.Calls);
        Assert.Equal(1, graph.Checkpoint.Successes);
        Assert.Equal(125L, ReadWallet(graph.Owner.Id));
        Assert.Equal(75L, ReadWallet(graph.Target.Id));
        Assert.Equal(3, CountOwnedItems(graph.Owner.Id, graph.Target.Id));
        var received = Assert.Single(graph.Target.Inventory.Bag.Items);
        Assert.NotEqual((ulong)splitId, received.Id);
        var successful = graph.ReloadItems();
        AssertLoadedItem(successful, original.Id, graph.Owner, 3, original.Grade);
        AssertLoadedItem(successful, returnItem.Id, graph.Owner, 1, returnItem.Grade);
        AssertLoadedItem(successful, received.Id, graph.Target, 2, original.Grade);
        Assert.Null(successful.GetItemByItemId(splitId));
    }

    [Fact]
    public async Task ConcurrentFinalConfirmations_SaveOneExchangeThatReloadsWithoutDuplicates()
    {
        using var graph = new TradeGraph();
        var original = graph.AddItem(graph.Owner, 20, 100, 5);
        var returnItem = graph.AddItem(graph.Target, 21, 200, 1);
        Assert.True(graph.Save.TryCommitEconomy([graph.Owner, graph.Target]));
        graph.BeginExchange(original, returnItem);
        graph.Trade.LockTrade(graph.Owner, true);
        graph.Trade.LockTrade(graph.Target, true);
        graph.Trade.OkTrade(graph.Owner);
        using var start = new ManualResetEventSlim();
        var attempts = Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
        {
            start.Wait();
            graph.Trade.OkTrade(graph.Target);
        })).ToArray();
        start.Set();
        await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, graph.Checkpoint.Calls);
        Assert.Equal(1, graph.Checkpoint.Successes);
        Assert.Equal(125L, ReadWallet(graph.Owner.Id));
        Assert.Equal(75L, ReadWallet(graph.Target.Id));
        Assert.Equal(3, CountOwnedItems(graph.Owner.Id, graph.Target.Id));
        var received = Assert.Single(graph.Target.Inventory.Bag.Items);
        var reloaded = graph.ReloadItems();
        AssertLoadedItem(reloaded, original.Id, graph.Owner, 3, original.Grade);
        AssertLoadedItem(reloaded, returnItem.Id, graph.Owner, 1, returnItem.Grade);
        AssertLoadedItem(reloaded, received.Id, graph.Target, 2, original.Grade);
    }

    [Fact]
    public void SplitRegistrationReturnsFalse_PreservesSavedAssetsWithoutCheckpoint()
    {
        using var graph = new TradeGraph();
        var original = graph.AddItem(graph.Owner, 20, 100, 5);
        var returnItem = graph.AddItem(graph.Target, 21, 200, 1);
        Assert.True(graph.Save.TryCommitEconomy([graph.Owner, graph.Target]));
        // Force AddItem's ordinary duplicate-key false result, without throwing.
        graph.ItemIds.Next = (uint)original.Id;
        graph.BeginExchange(original, returnItem);
        graph.ConfirmBoth();

        Assert.Equal(0, graph.Checkpoint.Calls);
        Assert.Same(original, Assert.Single(graph.Owner.Inventory.Bag.Items));
        Assert.Same(returnItem, Assert.Single(graph.Target.Inventory.Bag.Items));
        Assert.Same(original, graph.Items.GetItemByItemId(original.Id));
        Assert.Equal(5, original.Count);
        Assert.Equal(100L, graph.Owner.Money);
        Assert.Equal(100L, graph.Target.Money);
        Assert.Equal(100L, ReadWallet(graph.Owner.Id));
        Assert.Equal(100L, ReadWallet(graph.Target.Id));
        Assert.Equal(2, CountOwnedItems(graph.Owner.Id, graph.Target.Id));
        Assert.Equal(0, graph.PacketCount(graph.Owner, SCOffsets.SCTradeMadePacket));
        var reloaded = graph.ReloadItems();
        AssertLoadedItem(reloaded, original.Id, graph.Owner, 5, original.Grade);
        AssertLoadedItem(reloaded, returnItem.Id, graph.Target, 1, returnItem.Grade);
    }

    private static void AssertLoadedItem(ItemManager manager, ulong id, Character owner, int count, byte grade)
    {
        var item = manager.GetItemByItemId(id);
        Assert.NotNull(item);
        Assert.Equal((ulong)owner.Id, item.OwnerId);
        Assert.Equal(count, item.Count);
        Assert.Equal(grade, item.Grade);
        Assert.Equal(SlotType.Inventory, item.SlotType);
        Assert.NotNull(item._holdingContainer);
        Assert.Equal(owner.Inventory.Bag.ContainerId, item._holdingContainer.ContainerId);
        Assert.Same(item, item._holdingContainer.GetItemBySlot(item.Slot));
    }

    private static long ReadWallet(uint id) => ReadScalar("SELECT money FROM characters WHERE id = @id", id);
    private static long CountItem(ulong id) => ReadScalar("SELECT COUNT(*) FROM items WHERE id = @id", id);

    private static long CountOwnedItems(uint owner, uint target)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM items WHERE owner IN (@owner, @target)";
        command.Parameters.AddWithValue("@owner", owner);
        command.Parameters.AddWithValue("@target", target);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static long ReadScalar(string sql, ulong id)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@id", id);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(string sql)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class TradeGraph : IDisposable
    {
        private readonly Dictionary<FieldInfo, object> _previousInstances = [];
        private readonly Dictionary<uint, RecordingSession> _sessions = [];
        private readonly Dictionary<ulong, Item> _allItems = [];
        private readonly Dictionary<ulong, ItemContainer> _containers = [];
        private readonly Dictionary<uint, ItemTemplate> _templates = [];
        private readonly WorldManager _worldManager;
        private readonly WorldInstance _world;
        public uint Id { get; } = (uint)Interlocked.Add(ref _nextId, 1000);
        public Ids ItemIds { get; }
        public Character Owner { get; }
        public Character Target { get; }
        public ItemManager Items { get; }
        public SaveManager Save { get; }
        public CountingSave Checkpoint { get; }
        public TradeManager Trade { get; }

        public TradeGraph()
        {
            _worldManager = new WorldManager(Mock.Of<ITickManager>(), Mock.Of<IWorldIdManager>(),
                new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>()),
                new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>()),
                new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>()));
            SetInstance(_worldManager);
            _world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1);
            ((ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
                .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_worldManager)!)
                .TryAdd(1, _world);
            ItemIds = new Ids { Next = Id + 500 };
            Items = CreateItemManager(ItemIds);
            SetInstance(Items);
            SetInstance(new QuestManager(Mock.Of<ITaskManager>(), Mock.Of<IZoneManager>()));
            SetField(Items, "_allItems", _allItems);
            SetField(Items, "_removedItems", new List<ulong>());
            SetField(Items, "_allPersistentContainers", _containers);
            SetField(Items, "_templates", _templates);
            Owner = CreateCharacter(Id);
            Target = CreateCharacter(Id + 1);
            Save = new SaveManager(Mock.Of<ITaskManager>(), Mock.Of<IHousingManager>(),
                Mock.Of<IMailManager>(), Items, Mock.Of<IAuctionManager>(), Mock.Of<ICrimeManager>(),
                _worldManager, Mock.Of<IZoneManager>());
            Checkpoint = new CountingSave(Save);
            Trade = new TradeManager(new Ids(), _worldManager, Items, Checkpoint);
            SetInstance(Trade);
        }

        public void BeginExchange(Item original, Item returnItem)
        {
            Trade.CanStartTrade(Owner, Target);
            Trade.StartTrade(Owner, Target);
            Trade.AddItem(Owner, SlotType.Inventory, (byte)original.Slot, 2);
            Trade.AddItem(Target, SlotType.Inventory, (byte)returnItem.Slot, 1);
            Trade.AddMoney(Target, 25);
        }

        public void ConfirmBoth()
        {
            Trade.LockTrade(Owner, true);
            Trade.LockTrade(Target, true);
            Trade.OkTrade(Owner);
            Trade.OkTrade(Target);
        }

        public Item AddItem(Character owner, uint offset, uint templateId, int count)
        {
            if (!_templates.TryGetValue(templateId, out var template))
                _templates.Add(templateId, template = Template(templateId));
            var bag = owner.Inventory.Bag;
            var item = new Item(0, Id + offset, template, count)
            {
                OwnerId = owner.Id, SlotType = SlotType.Inventory, Slot = bag.Items.Count,
                _holdingContainer = bag
            };
            bag.Items.Add(item);
            bag.UpdateFreeSlotCount();
            _allItems.Add(item.Id, item);
            return item;
        }

        public ItemManager ReloadItems()
        {
            var manager = CreateItemManager(new Ids { Next = Id + 700 });
            var templates = new Dictionary<uint, ItemTemplate>(_templates);
            using (var connection = MySQL.CreateConnection())
            using (var command = connection.CreateCommand())
            {
                // This shared disposable fixture may contain items from other test cases.
                // Compact-derived stats are irrelevant to persisted identity and ownership.
                command.CommandText = "SELECT DISTINCT template_id FROM items";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetUInt32(0);
                    templates.TryAdd(id, Template(id));
                }
            }
            SetField(manager, "_templates", templates);
            SetInstance(manager);
            manager.LoadUserItems();
            return manager;
        }

        public void RestoreItemManager() => SetInstance(Items);

        public int PacketCount(Character character, ushort opcode) => _sessions[character.Id].Packets
            .Count(packet => BitConverter.ToUInt16(packet, 6) == opcode);

        public void Dispose()
        {
            Trade.CancelTrade(Owner, 0);
            Trade.CancelTrade(Target, 0);
            foreach (var (field, previous) in _previousInstances)
                field.SetValue(null, previous);
        }

        private Character CreateCharacter(uint id)
        {
            var session = new RecordingSession();
            _sessions.Add(id, session);
            var character = new TestCharacter
            {
                Id = id, ObjId = id, AccountId = id, Name = $"Trade{id}", Money = 100, Hp = 100, Level = 50,
                NumInventorySlots = 10, NumBankSlots = 10, ParentWorld = _world,
                Faction = new SystemFaction { Id = FactionsEnum.NuiaAlliance, MotherId = FactionsEnum.NuiaAlliance },
                FactionName = "", Slots = [], Created = DateTime.UtcNow,
                Connection = new GameConnection(session)
            };
            character.Connection.ActiveChar = character;
            typeof(Character).GetField("<IsOnline>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(character, true);
            _worldManager.TryAddCharacter(character);
            foreach (var slotType in Enum.GetValues<SlotType>())
            {
                if (slotType == SlotType.EquipmentMate)
                    continue;
                var container = new ItemContainer(id, slotType, false, character)
                {
                    ContainerId = Id + 100 + (ulong)_containers.Count,
                    Owner = character
                };
                _containers.Add(container.ContainerId, container);
            }
            character.Inventory = new Inventory(character);
            return character;
        }

        private ItemManager CreateItemManager(IItemIdManager ids) => new(Mock.Of<ISkillManager>(), ids,
            Mock.Of<IContainerIdManager>(), Mock.Of<ILocalizationManager>(), Mock.Of<ITaskManager>(), _worldManager);

        private static ItemTemplate Template(uint id) => new()
        {
            Id = id, Name = $"Trade item {id}", MaxCount = 10000,
            BindType = ItemBindType.Normal, FixedGrade = -1, Gradable = true
        };

        private void SetInstance<T>(T instance) where T : class
        {
            var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
            _previousInstances.TryAdd(field, field.GetValue(null));
            field.SetValue(null, instance);
        }

        private static void SetField(object owner, string field, object value) => owner.GetType()
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    }

    private sealed class TestCharacter() : Character(new UnitCustomModelParams())
    {
        public override void BroadcastPacket(GamePacket packet, bool self) { }
    }

    private sealed class Ids : IItemIdManager, ITradeIdManager
    {
        public uint Next { get; set; } = 10000;
        public bool Initialize(bool forceReset = false) => true;
        public void Load() { }
        public uint GetNextId() => Next++;
        public uint[] GetNextId(int count) => Enumerable.Range(0, count).Select(_ => GetNextId()).ToArray();
        public void ReleaseId(uint usedObjectId) { }
        public void ReleaseId(IEnumerable<uint> usedObjectIds) { }
    }

    private sealed class CountingSave(SaveManager save) : ISaveManager
    {
        public ShutdownTask ShutdownTask { get; set; }
        public int Calls { get; private set; }
        public int Successes { get; private set; }
        public void Initialize() { }
        public Task StopAsync() => Task.CompletedTask;
        public void SaveTickStart() { }
        public bool DoSave() => save.DoSave();
        public bool TryCommitEconomy(IReadOnlyCollection<Character> participants,
            Action<PersistenceSaveContext> writeSettlement = null)
        {
            Calls++;
            var result = save.TryCommitEconomy(participants, writeSettlement);
            if (result)
                Successes++;
            return result;
        }
    }

    private sealed class RecordingSession : ISession
    {
        private readonly Dictionary<string, object> _attributes = [];
        public List<byte[]> Packets { get; } = [];
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) => Packets.Add(packet.ToArray());
        public void AddAttribute(string name, object attribute) => _attributes.Add(name, attribute);
        public object GetAttribute(string name) => _attributes.GetValueOrDefault(name);
        public void ClearAttribute(string name) => _attributes.Remove(name);
        public void Close() { }
    }
}
