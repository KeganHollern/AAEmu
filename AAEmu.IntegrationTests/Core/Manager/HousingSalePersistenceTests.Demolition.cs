using System.Reflection;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Game.Features;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.World.Interactions;
using Moq;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Mails;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class HousingSalePersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TaxDemolition_ReturnsToFormerOwnerOnceAndResumesDecayAfterReload(bool failFirst)
    {
        using var graph = new PurchaseGraph((uint)Interlocked.Add(ref _nextPurchaseId, 100));
        var (plant, contents, design) = graph.PrepareTaxDemolition();
        var position = plant.Transform.World.Position;
        var house = graph.House;
        var expected = contents.Append(graph.ReturnedBacking).ToArray();
        var restoredTemplate = graph.AddReturnableDecor();
        var adjacentVehicle = graph.AddOtherOwnersFixture();
        var trigger = $"fail_tax_return_{house.Id}";
        if (failFirst)
        {
            // This delete runs after the house, mail and items were written into the open transaction.
            FailReturnedCofferDeletion(trigger, graph.ReturnedContainerId, graph.Returned.DbId);
            try
            {
                graph.Housing.CheckHousingTaxes();
            }
            finally
            {
                Execute($"DROP TRIGGER {trigger}");
            }
            Assert.Equal(graph.Seller.Id, house.OwnerId);
            Assert.False(house.DemolitionReturnsCommitted);
            Assert.Same(graph.OldTaxOffer, Assert.Single(graph.Mails._allPlayerMails.Values));
            Assert.Same(graph.Guest, graph.Returned.OpenedBy);
            Assert.Same(house.Transform, plant.Transform.Parent);
            Assert.All(contents, item => Assert.Equal(SlotType.Trade, item.SlotType));
            Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM housings WHERE id={house.Id} AND owner={graph.Seller.Id} AND demolition_hp IS NULL"));
            Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mails WHERE id={graph.OldTaxOffer.Id}"));
            Assert.Equal(2, Scalar($"SELECT COUNT(*) FROM doodads WHERE id IN ({graph.Retained.DbId},{graph.Returned.DbId})"));
            Assert.Equal(contents.Length, Scalar($"SELECT COUNT(*) FROM items WHERE id IN ({string.Join(",", contents.Select(i => i.Id))}) AND owner={graph.Guest.Id}"));
        }

        graph.Housing.CheckHousingTaxes();
        graph.Housing.CheckHousingTaxes();
        Assert.Equal(0u, house.OwnerId);
        Assert.Equal(0u, house.AccountId);
        Assert.True(house.DemolitionReturnsCommitted);
        Assert.Equal(house.Hp, Scalar($"SELECT demolition_hp FROM housings WHERE id={house.Id}"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM mails WHERE id={graph.OldTaxOffer.Id}"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM items WHERE id={graph.RetainedBacking.Id}"));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM doodads WHERE id IN ({graph.Retained.DbId},{graph.Returned.DbId})"));
        Assert.Equal(0, Scalar($"SELECT house_id FROM doodads WHERE id={plant.DbId}"));
        Assert.Equal(position, plant.Transform.World.Position);
        Assert.Null(plant.Transform.Parent);
        Assert.DoesNotContain(plant, house.AttachedDoodads);
        Assert.Equal((long)DoodadOwnerType.Slave, Scalar($"SELECT owner_type FROM doodads WHERE id={adjacentVehicle.DbId}"));
        Assert.Equal(house.Id, adjacentVehicle.OwnerDbId);

        // The ordinary save retains partial burn progress.
        house.Hp -= 30;
        Assert.True(graph.Save.DoSave());
        var restored = graph.Reload();
        Assert.Equal(house.Hp, restored.House.Hp);
        Assert.True(restored.House.DemolitionReturnsCommitted);
        restored.Housing.Demolish(null, restored.House, true, false);
        var letters = restored.Mails._allPlayerMails.Values.Where(mail =>
            mail.Header.ReceiverId == graph.Seller.Id && mail.MailType == MailType.Demolish).ToArray();
        Assert.Equal(2, letters.Length);
        Assert.All(letters, mail =>
        {
            Assert.Equal(TimeSpan.FromHours(22), mail.Body.RecvDate - mail.Body.SendDate);
            Assert.False(mail.IsDelivered);
            Assert.Equal(0, mail.Body.CopperCoins);
        });
        var attachments = letters.SelectMany(mail => mail.Body.Attachments).ToArray();
        var designItem = Assert.Single(attachments, i => i.TemplateId == design);
        Assert.Equal(1, designItem.Count);
        Assert.Equal(2, Assert.Single(attachments, item => item.TemplateId == restoredTemplate).Count);
        Assert.Equal(expected.Length + 2, attachments.Length);
        foreach (var original in expected)
        {
            var item = Assert.Single(attachments, i => i.Id == original.Id);
            Assert.Same(restored.Items.GetItemByItemId(original.Id), item);
            Assert.Equal(original.Count, item.Count);
            Assert.Equal(original.UccId, item.UccId);
            Assert.Equal(original.ItemFlags, item.ItemFlags);
            Assert.Equal(graph.Seller.Id, item.OwnerId);
            Assert.Equal(graph.Seller.Id, item._holdingContainer.OwnerId);
        }
        Assert.DoesNotContain(restored.Mails._allPlayerMails.Values, mail =>
            mail.Header.ReceiverId == graph.Guest.Id && mail.MailType == MailType.Demolish);

        graph.CheckMailDelivery(restored.Mails, letters);
        restored.House.OnDeath(restored.House, EventArgs.Empty);
        Assert.Null(restored.Housing.GetHouseById(house.Id));
        var afterDeath = graph.ForReload(restored.Items, restored.Mails, restored.Housing);
        // A failed cleanup save must reserve the old ID until the DELETE actually commits.
        var deleteTrigger = $"fail_dead_house_{house.Id}";
        Execute($"CREATE TRIGGER {deleteTrigger} BEFORE DELETE ON housings FOR EACH ROW BEGIN " +
            $"IF OLD.id={house.Id} THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='injected'; END IF; END");
        try
        {
            Assert.False(afterDeath.DoSave());
            Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM housings WHERE id={house.Id}"));
            graph.HouseIds.Verify(ids => ids.ReleaseId(house.Id), Times.Never);
        }
        finally
        {
            Execute($"DROP TRIGGER {deleteTrigger}");
        }
        Assert.True(afterDeath.DoSave());
        graph.HouseIds.Verify(ids => ids.ReleaseId(house.Id), Times.Once);
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM housings WHERE id={house.Id}"));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM doodads WHERE id={plant.DbId} AND house_id=0 AND owner_id={graph.Guest.Id}"));
        Assert.Equal(2, Scalar($"SELECT COUNT(*) FROM mails WHERE receiver_id={graph.Seller.Id} AND type={(byte)MailType.Demolish}"));

        // Model a new saved property using the released ID and the same plot. Exercise normal restore and stale death.
        var replacement = House(house.Id);
        replacement.Template = house.Template;
        replacement.TemplateId = house.TemplateId;
        replacement.OwnerId = replacement.CoOwnerId = graph.Buyer.Id;
        replacement.AccountId = graph.Buyer.AccountId;
        replacement.Transform.Local.Position = house.Transform.World.Position;
        replacement.ProtectionEndDate = DateTime.UtcNow.AddDays(14);
        Assert.True(afterDeath.TryCommitEconomy([], context => Assert.True(replacement.Save(context))));
        var next = graph.Reload();
        restored.House.OnDeath(restored.House, EventArgs.Empty);
        Assert.Same(next.House, next.Housing.GetHouseById(house.Id));
        Assert.True(graph.ForReload(next.Items, next.Mails, next.Housing).DoSave());
        Assert.Equal(graph.Buyer.Id, Scalar($"SELECT owner FROM housings WHERE id={house.Id}"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PaidVoluntaryDemolition_UsesNormalCastAndRefundsDepositOnce(bool certificates)
    {
        using var graph = new PurchaseGraph((uint)Interlocked.Add(ref _nextPurchaseId, 100));
        var (_, _, design) = graph.PrepareTaxDemolition();
        graph.House.ProtectionEndDate = DateTime.UtcNow.AddDays(14);
        Assert.True(graph.Save.TryCommitEconomy([], context => Assert.True(graph.House.Save(context))));
        var door = graph.AddFixedDoor();
        var original = FeaturesManager.Fsets;
        var features = new FeatureSet();
        features.Set(Feature.taxItem, certificates);
        var property = typeof(FeaturesManager).GetProperty(nameof(FeaturesManager.Fsets))!;
        property.SetValue(null, features);
        try
        {
            var connection = new GameConnection(Mock.Of<ISession>()) { ActiveChar = graph.Seller };
            graph.Seller.Connection = connection;
            var action = new Demolish();
            void Cast() => action.Execute(graph.Seller, new SkillCasterUnit(graph.Seller.ObjId), graph.House,
                new SkillCastUnitTarget(graph.House.ObjId), 12945, 0);
            Cast();
            Cast();
            Assert.Equal(0u, graph.House.OwnerId);
            Assert.False(graph.House.DemolitionReturnsCommitted);
            Assert.Same(graph.House, graph.Housing.GetHouseById(graph.House.Id));
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM housings WHERE id={graph.House.Id}"));
            Assert.Same(door, graph.House.ParentWorld.GetDoodad(door.ObjId));
            Assert.False(door.IsPersistent);
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM doodads WHERE id={door.DbId}"));
            graph.House.OnDeath(graph.House, EventArgs.Empty);
            Assert.Null(graph.Housing.GetHouseById(graph.House.Id));
            Assert.Null(graph.House.ParentWorld.GetDoodad(door.ObjId));
            var reloaded = graph.Reload();
            Assert.Null(reloaded.House);
            var letters = reloaded.Mails.GetCurrentMailList(graph.Seller.Id).Values.Where(m => m.MailType == MailType.Demolish).ToArray();
            Assert.Equal(2, letters.Length);
            Assert.All(letters, mail => Assert.Equal(mail.Body.SendDate, mail.Body.RecvDate));
            var items = letters.SelectMany(mail => mail.Body.Attachments).ToArray();
            Assert.Equal(1, Assert.Single(items, item => item.TemplateId == design).Count);
            Assert.Equal(certificates ? 1 : 0, items.Where(i => i.TemplateId == Item.BoundTaxCertificate).Sum(i => i.Count));
            Assert.Equal(certificates ? 0 : 10000, letters.Sum(m => m.Body.CopperCoins));
            Assert.Equal(certificates ? 0 : 1, letters.Count(m => m.Body.CopperCoins != 0));
        }
        finally
        {
            property.SetValue(null, original);
        }
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(3)]
    public void TaxDemolition_UsesTheGameClockForTheStoredProtectionDate(int dayOffset)
    {
        using var graph = new PurchaseGraph((uint)Interlocked.Add(ref _nextPurchaseId, 100));
        graph.PrepareTaxDemolition();
        // Keep the database clock unchanged. A paid date in SQL must still protect the house from a stale tax check.
        var asOf = DateTime.UtcNow.Date.AddDays(dayOffset);
        graph.House.ProtectionEndDate = asOf.AddMinutes(1);
        Assert.True(graph.Save.TryCommitEconomy([], context => Assert.True(graph.House.Save(context))));
        graph.House.ProtectionEndDate = asOf.AddMinutes(-1);
        graph.Housing.Demolish(null, graph.House, true, false, asOf);
        Assert.Equal(graph.Seller.Id, graph.House.OwnerId);
        Assert.Same(graph.OldTaxOffer, Assert.Single(graph.Mails._allPlayerMails.Values));
        graph.Housing.Demolish(null, graph.House, true, false, asOf.AddMinutes(2));
        Assert.True(graph.House.DemolitionReturnsCommitted);
        Assert.Equal(2, graph.Mails._allPlayerMails.Count);
        Assert.All(graph.Mails._allPlayerMails.Values, mail => Assert.Equal(asOf.AddMinutes(2).AddHours(22), mail.Body.RecvDate));
    }

    [Fact]
    public void TaxDemolition_RefusesToSurrenderUnloadedProperty()
    {
        using var graph = new PurchaseGraph((uint)Interlocked.Add(ref _nextPurchaseId, 100));
        var (plant, _, _) = graph.PrepareTaxDemolition();
        graph.House.AttachedDoodads.Remove(plant);
        graph.House.ParentWorld.RemoveObject(plant);
        graph.Housing.CheckHousingTaxes();
        Assert.Equal(graph.Seller.Id, graph.House.OwnerId);
        Assert.Equal(graph.Seller.Id, Scalar($"SELECT owner FROM housings WHERE id={graph.House.Id}"));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM mails WHERE id={graph.OldTaxOffer.Id}"));
        Assert.Same(graph.Retained.ItemContainer, graph.RetainedContent._holdingContainer);
        graph.House.ParentWorld.AddObject(plant);
        graph.Housing.CheckHousingTaxes();
        Assert.True(graph.House.DemolitionReturnsCommitted);
    }

    private sealed partial class PurchaseGraph
    {
        public Mock<IHousingIdManager> HouseIds { get; } = new();

        public void CheckMailDelivery(MailManager mails, BaseMail[] letters)
        {
            Replace(mails);
            _world.Setup(w => w.GetCharacter(Seller.Name)).Returns(Seller);
            _world.Setup(w => w.GetCharacterById(Seller.Id)).Returns(Seller);
            mails.CheckAllMailTimings();
            Assert.Empty(mails.GetCurrentMailList(Seller.Id));
            Seller.Mails.ReadMail(false, letters[0].Id);
            Assert.All(letters, letter => Assert.False(letter.IsDelivered));
            Assert.Equal(MailStatus.Unread, letters[0].Header.Status);
            // Advance only these disposable letters by 22 hours, then use the normal delivery and read paths.
            var deliveredAt = DateTime.UtcNow.AddSeconds(-1);
            foreach (var letter in letters)
            {
                letter.Body.SendDate = deliveredAt.AddHours(-22);
                letter.Body.RecvDate = deliveredAt;
            }
            mails.CheckAllMailTimings();
            Assert.All(letters, letter => Assert.True(letter.IsDelivered));
            Assert.Equal(letters.Length, mails.GetCurrentMailList(Seller.Id).Count);
            Seller.Mails.ReadMail(false, letters[0].Id);
            Assert.Equal(MailStatus.Read, letters[0].Header.Status);
        }

        public uint AddReturnableDecor()
        {
            var template = House.Id + 32;
            _templates[template] = new ItemTemplate { Id = template, MaxCount = 5 };
            var data = HousingGameData.Instance;
            var rows = (Dictionary<uint, HousingDecoration>)typeof(HousingGameData).GetField("_housingDecorations",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(data)!;
            rows.Add(template, new HousingDecoration { Id = template, DoodadId = template });
            var designs = (List<ItemHousingDecoration>)typeof(HousingGameData).GetField("_housingItemHousingDecorations",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(data)!;
            designs.Add(new ItemHousingDecoration { DesignId = template, ItemId = template, Restore = true });
            for (uint i = 0; i < 2; i++)
            {
                var decoration = Doodad(House.Id + 53 + i, House, AttachPointKind.None);
                decoration.TemplateId = decoration.ItemTemplateId = template;
                House.AttachedDoodads.Add(decoration);
                decoration.Save();
            }
            return template;
        }

        public Doodad AddFixedDoor()
        {
            var idField = typeof(ObjectIdManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
            _oldSingletons.TryAdd(idField, idField.GetValue(null));
            var ids = new ObjectIdManager();
            ids.Initialize();
            idField.SetValue(null, ids);
            var door = Doodad(House.Id + 56, House, AttachPointKind.Driver);
            door.ParentWorld = House.ParentWorld;
            door.ObjId = ids.GetNextId();
            door.Transform.Parent = House.Transform;
            House.ParentWorld.AddObject(door);
            House.AttachedDoodads.Add(door);
            door.Save();
            return door;
        }

        public Doodad AddOtherOwnersFixture()
        {
            var vehicle = Doodad(House.Id + 55, House, AttachPointKind.None);
            vehicle.OwnerType = DoodadOwnerType.Slave;
            vehicle.ParentWorld = House.ParentWorld;
            vehicle.ObjId = ++_nextObjectId;
            House.ParentWorld.AddObject(vehicle);
            vehicle.Save();
            return vehicle;
        }

        public SaveManager ForReload(ItemManager items, MailManager mails, HousingManager housing) => NewSave(items, mails, housing);

        public (Doodad Plant, Item[] Contents, uint Design) PrepareTaxDemolition()
        {
            var id = House.Id;
            var design = id + 31;
            _templates[design] = new ItemTemplate { Id = design, MaxCount = 1 };
            _templates[Item.BoundTaxCertificate] = new ItemTemplate { Id = Item.BoundTaxCertificate, MaxCount = 100 };
            SetField(HousingGameData.Instance, "_housingItemHousings", new List<HousingItemHousings>
                { new() { Design_Id = House.Template.Id, Item_Id = design } });
            House.Hp = House.MaxHp;
            House.ProtectionEndDate = DateTime.UtcNow.AddMinutes(-1);
            House.IsDirty = true;
            var plant = Doodad(id + 52, House, AttachPointKind.None);
            plant.OwnerId = Guest.Id;
            plant.ParentWorld = House.ParentWorld;
            plant.ObjId = ++_nextObjectId;
            plant.Transform.Parent = House.Transform;
            plant.Transform.Local.Position = new(1, 2, 3);
            House.ParentWorld.AddObject(plant);
            House.ParentWorld.SpawnManager.AddPlayerDoodad(plant);
            House.AttachedDoodads.Add(plant);
            plant.Save();
            // Force a second letter and retain each guest-owned stack as a distinct attachment.
            var contents = new List<Item> { RetainedContent, ReturnedContent };
            for (uint i = 0; i < 8; i++)
                contents.Add(AddItem(Retained.ItemContainer, id + 12, id + 80 + i, (int)i + 1));
            ReturnedBacking.UccId = 918;
            Assert.True(Save.TryCommitEconomy([], context => Assert.True(House.Save(context))));
            return (plant, contents.ToArray(), design);
        }
    }
}
