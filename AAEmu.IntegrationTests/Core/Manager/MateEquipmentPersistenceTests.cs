using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;

using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class MateEquipmentPersistenceTests
{
    [Fact]
    public void SavedMateGear_LoadsBeforeMateCreationAndKeepsContainerAfterAnotherSave()
    {
        const uint ownerId = 1640000;
        const uint mateId = ownerId + 1;
        const ulong containerId = ownerId + 2;
        const ulong itemId = ownerId + 3;
        const uint templateId = ownerId + 4;
        var itemInstance = typeof(Singleton<ItemManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var worldInstance = typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousItems = itemInstance.GetValue(null);
        var previousWorld = worldInstance.GetValue(null);
        try
        {
            worldInstance.SetValue(null, new WorldManager(null, null, null, null, null));
            var template = new ArmorTemplate
            {
                Id = templateId, Name = "Saved mate gear", FixedGrade = -1, Gradable = true,
                MaxCount = 1, LevelRequirement = 35, BindType = ItemBindType.BindOnEquip,
                WearableTemplate = new Wearable { SlotTypeId = (uint)EquipmentItemSlotType.Head }
            };
            var container = new MateEquipmentContainer(ownerId, SlotType.EquipmentMate, false, null)
                { ContainerId = containerId, MateId = mateId };
            var original = new Armor
            {
                Id = itemId, TemplateId = templateId, Template = template, OwnerId = ownerId,
                SlotType = SlotType.EquipmentMate, Slot = (int)EquipmentItemSlot.Head, Count = 1,
                Grade = 6, Durability = 29, RuneId = 35, GemIds = [1, 2, 3, 4, 5, 6, 7],
                TemperPhysical = 110, TemperMagical = 120, ItemFlags = ItemFlag.SoulBound,
                CreateTime = new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc),
                _holdingContainer = container
            };
            container.Items.Add(original);
            container.UpdateFreeSlotCount();
            var manager = CreateManager();
            itemInstance.SetValue(null, manager);
            SetField(manager, "_allPersistentContainers", new Dictionary<ulong, ItemContainer> { [containerId] = container });
            SetField(manager, "_allItems", new Dictionary<ulong, Item> { [itemId] = original });
            SetField(manager, "_removedItems", new List<ulong>());
            Save(manager);

            var reloaded = CreateManager();
            var templates = ReadFallbackTemplates();
            templates[templateId] = template;
            SetField(reloaded, "_templates", templates);
            itemInstance.SetValue(null, reloaded);
            reloaded.LoadUserItems();

            var loaded = Assert.IsType<Armor>(reloaded.GetItemByItemId(itemId));
            var restored = Assert.IsType<MateEquipmentContainer>(reloaded.GetItemContainerByDbId(containerId));
            Assert.Null(restored.ParentUnit);
            Assert.Same(restored, loaded._holdingContainer);
            Assert.Same(loaded, Assert.Single(restored.Items));
            Assert.Equal(ownerId, loaded.OwnerId);
            Assert.Equal(mateId, restored.MateId);
            Assert.Equal(original.Slot, loaded.Slot);
            Assert.Equal(original.ItemFlags, loaded.ItemFlags);
            Assert.Equal(original.Grade, loaded.Grade);
            Assert.Equal(original.Durability, loaded.Durability);
            Assert.Equal(original.RuneId, loaded.RuneId);
            Assert.Equal(original.GemIds, loaded.GemIds);
            Assert.Equal(original.TemperPhysical, loaded.TemperPhysical);
            Assert.Equal(original.TemperMagical, loaded.TemperMagical);
            Assert.Equal(original.CreateTime, loaded.CreateTime);
            Assert.False(loaded.IsDirty);
            Assert.False(restored.IsDirty);

            // CharacterMates uses this same lookup after the mate exists.
            var mate = new Mate { Id = mateId, Level = 35, Template = new NpcTemplate() };
            mate.Equipment = reloaded.GetItemContainerForCharacter(ownerId, SlotType.EquipmentMate, mate, mateId);
            Assert.Same(restored, mate.Equipment);
            Assert.Same(mate, restored.ParentUnit);
            Assert.Same(loaded, Assert.Single(mate.Equipment.Items));
            loaded.IsDirty = true;
            Save(reloaded);

            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT container_id, slot_type, slot, owner FROM items WHERE id=@id";
            command.Parameters.AddWithValue("@id", itemId);
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(containerId, reader.GetUInt64(0));
            Assert.Equal((int)SlotType.EquipmentMate, reader.GetInt32(1));
            Assert.Equal(original.Slot, reader.GetInt32(2));
            Assert.Equal(ownerId, reader.GetUInt32(3));
        }
        finally
        {
            itemInstance.SetValue(null, previousItems);
            worldInstance.SetValue(null, previousWorld);
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM items WHERE id=@item; DELETE FROM item_containers WHERE container_id=@container";
            command.Parameters.AddWithValue("@item", itemId);
            command.Parameters.AddWithValue("@container", containerId);
            command.ExecuteNonQuery();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidOrDuplicateSavedSlot_StopsStartupAndDoesNotRewriteStoredContainers(bool duplicateSlot)
    {
        const uint ownerId = 1640010;
        const ulong containerId = ownerId + 1;
        const ulong itemId = ownerId + 2;
        const ulong otherItemId = itemId + 10;
        const uint templateId = ownerId + 3;
        var itemInstance = typeof(Singleton<ItemManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var worldInstance = typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousItems = itemInstance.GetValue(null);
        var previousWorld = worldInstance.GetValue(null);
        try
        {
            worldInstance.SetValue(null, new WorldManager(null, null, null, null, null));
            var template = new ArmorTemplate { Id = templateId, Name = "Invalid saved mate gear", FixedGrade = -1,
                WearableTemplate = new Wearable { SlotTypeId = (uint)EquipmentItemSlotType.Head } };
            var container = new MateEquipmentContainer(ownerId, SlotType.EquipmentMate, false, null)
                { ContainerId = containerId, MateId = ownerId };
            var item = new Armor { Id = itemId, TemplateId = templateId, Template = template, OwnerId = ownerId,
                SlotType = SlotType.EquipmentMate, Slot = duplicateSlot ? 0 : container.ContainerSize,
                Count = 1, _holdingContainer = container };
            container.Items.Add(item);
            var allItems = new Dictionary<ulong, Item> { [itemId] = item };
            if (duplicateSlot)
            {
                var other = new Armor { Id = otherItemId, TemplateId = templateId, Template = template, OwnerId = ownerId,
                    SlotType = SlotType.EquipmentMate, Slot = item.Slot, Count = 1, _holdingContainer = container };
                container.Items.Add(other);
                allItems.Add(otherItemId, other);
            }
            var manager = CreateManager();
            itemInstance.SetValue(null, manager);
            SetField(manager, "_allPersistentContainers", new Dictionary<ulong, ItemContainer> { [containerId] = container });
            SetField(manager, "_allItems", allItems);
            SetField(manager, "_removedItems", new List<ulong>());
            Save(manager);

            var reloaded = CreateManager();
            var templates = ReadFallbackTemplates();
            templates[templateId] = template;
            SetField(reloaded, "_templates", templates);
            itemInstance.SetValue(null, reloaded);

            var error = Assert.Throws<InvalidOperationException>(() => reloaded.LoadUserItems());
            Assert.Contains($"container {containerId}", error.Message);
            var rejectedId = reloaded.GetItemByItemId(itemId) == null ? itemId : otherItemId;
            Assert.Contains($"item {rejectedId}", error.Message);
            Assert.Null(reloaded.GetItemByItemId(rejectedId));
            Assert.Equal(duplicateSlot ? 1 : 0, reloaded.GetItemContainerByDbId(containerId).Items.Count);
            Save(reloaded);

            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT container_id, slot FROM items WHERE id IN (@id, @otherId)";
            command.Parameters.AddWithValue("@id", itemId);
            command.Parameters.AddWithValue("@otherId", otherItemId);
            using var reader = command.ExecuteReader();
            var rows = 0;
            while (reader.Read())
            {
                rows++;
                Assert.Equal(containerId, reader.GetUInt64(0));
                Assert.Equal(item.Slot, reader.GetInt32(1));
            }
            Assert.Equal(duplicateSlot ? 2 : 1, rows);
        }
        finally
        {
            itemInstance.SetValue(null, previousItems);
            worldInstance.SetValue(null, previousWorld);
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM items WHERE id IN (@item, @other); DELETE FROM item_containers WHERE container_id=@container";
            command.Parameters.AddWithValue("@item", itemId);
            command.Parameters.AddWithValue("@other", otherItemId);
            command.Parameters.AddWithValue("@container", containerId);
            command.ExecuteNonQuery();
        }
    }

    private static ItemManager CreateManager()
    {
        return new ItemManager(Mock.Of<ISkillManager>(), Mock.Of<IItemIdManager>(), Mock.Of<IContainerIdManager>(),
            Mock.Of<ILocalizationManager>(), Mock.Of<ITaskManager>(), Mock.Of<IWorldManager>());
    }

    private static Dictionary<uint, ItemTemplate> ReadFallbackTemplates()
    {
        var templates = new Dictionary<uint, ItemTemplate>();
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT template_id FROM items";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetUInt32(0);
            templates.Add(id, new ItemTemplate { Id = id, Name = "Fixture item", MaxCount = 100, FixedGrade = -1, Gradable = true });
        }
        return templates;
    }

    private static void Save(ItemManager manager)
    {
        using var connection = MySQL.CreateConnection();
        using var transaction = connection.BeginTransaction();
        manager.Save(connection, transaction);
        transaction.Commit();
    }

    private static void SetField(object target, string name, object value)
    {
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    }
}
