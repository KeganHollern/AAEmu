using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Features;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Core.Managers;

public partial class HousingManager
{
    private bool TrySettleDemolition(House house, bool failedToPayTax, bool forceRestoreAllDecor, DateTime asOf)
    {
        var owner = worldManager.GetCharacterById(house.OwnerId);
        var oldFaction = house.Faction?.Id ?? 0;
        var newFaction = factionManager.GetFaction(FactionsEnum.Monstrosity);
        var expired = asOf.AddSeconds(-1);

        return ExecuteHousingSettlement(house, owner,
            (inventory, mail, furniture) => PrepareDemolitionReturns(house, failedToPayTax, forceRestoreAllDecor,
                asOf, inventory, mail, furniture),
            () =>
            {
                UpdateTaxInfo(house);
                house.BroadcastPacket(new SCHouseDemolishedPacket(house.TlId), false);
                owner?.SendPacket(new SCMyHouseRemovedPacket(house.TlId));
                house.BroadcastPacket(new SCUnitFactionChangedPacket(house.ObjId, house.Name, oldFaction, FactionsEnum.Monstrosity, false), true);
                SetForSaleMarkers(house, false);
            }, includeFurniture: true, writeHouse: context => SaveDemolition(context, house, failedToPayTax, expired, asOf),
            itemTask: ItemTaskType.Mail, commitHouse: () =>
            {
                house.OwnerId = house.CoOwnerId = house.AccountId = 0;
                house.SellPrice = house.SellToPlayerId = 0;
                house.Permission = HousingPermission.Public;
                house.Faction = newFaction;
                house.ProtectionEndDate = expired;
                if (failedToPayTax)
                    house.AcknowledgeDemolitionReturns(asOf);
                house.IsDirty = false;
            });
    }

    private bool PrepareDemolitionReturns(House house, bool failedToPayTax, bool forceRestoreAllDecor,
        DateTime asOf, InventoryMutation inventory, MailMutation mail, HousingFurnitureSettlement furniture)
    {
        var formerOwner = house.OwnerId;
        var destination = itemManager.GetItemContainerForCharacter(formerOwner, SlotType.Mail, null, 0);
        var design = HousingGameData.Instance.GetItemIdByDesign(house.Template.Id);
        if (design == 0 || !inventory.TryGrant(destination, design, 1, out var designItems))
            return false;
        var returned = designItems.ToList();
        var copper = 0;
        if (!failedToPayTax)
        {
            var tax = house.Template.Taxation.Tax;
            if (FeaturesManager.Fsets.Check(Feature.taxItem))
            {
                var count = checked((int)(tax / 5000));
                if (count > 0)
                {
                    if (!inventory.TryGrant(destination, Item.BoundTaxCertificate, count, out var certificates))
                        return false;
                    returned.AddRange(certificates);
                }
            }
            else
                copper = checked((int)(tax * 2L));
        }
        if (!furniture.TryPrepareDemolition(inventory, forceRestoreAllDecor, removeSavedHouse: !failedToPayTax))
            return false;
        if (furniture.ReturnedItems.TryGetValue(formerOwner, out var decor))
            returned.AddRange(decor);
        // Both the mailbox schema and the client store whole seconds. An immediate return must remain due after reload.
        var sent = asOf.AddTicks(-(asOf.Ticks % TimeSpan.TicksPerSecond));
        foreach (var attachments in returned.Chunk(MailBody.MaxMailAttachments))
        {
            var letter = new BaseMail
            {
                MailType = MailType.Demolish, ReceiverName = nameManager.GetCharacterName(formerOwner), Title = "title",
                Header = { ReceiverId = formerOwner, SenderName = ".houseDemolish", Extra = house.Id },
                Body = { Text = "body", CopperCoins = copper, SendDate = sent,
                    RecvDate = failedToPayTax ? sent.AddHours(HoursForFailedTaxToReturnHouse) : sent }
            };
            copper = 0;
            letter.Body.Attachments.AddRange(attachments);
            if (!mail.TryAdd(letter))
                return false;
        }
        foreach (var bill in mailManager.GetMyHouseMails(house.Id))
            if (!mail.TryRemove(bill))
                return false;
        return true;
    }

    private static void SaveDemolition(PersistenceSaveContext context, House house, bool failedToPayTax,
        DateTime expired, DateTime asOf)
    {
        using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        // Only the original owned row can authorize returns, including after a restart.
        const string expected = " WHERE id=@id AND owner=@owner AND account_id=@account AND demolition_hp IS NULL";
        command.CommandText = failedToPayTax
            ? "UPDATE housings SET owner=0,co_owner=0,account_id=0,sell_to=0,sell_price=0," +
              "permission=@public,faction_id=@faction,protected_until=@expired,demolition_hp=@hp" + expected +
              " AND protected_until<=@asOf"
            : "DELETE FROM housings" + expected;
        command.Parameters.AddWithValue("@id", house.Id);
        command.Parameters.AddWithValue("@owner", house.OwnerId);
        command.Parameters.AddWithValue("@account", house.AccountId);
        if (failedToPayTax)
        {
            command.Parameters.AddWithValue("@public", (byte)HousingPermission.Public);
            command.Parameters.AddWithValue("@faction", (uint)FactionsEnum.Monstrosity);
            command.Parameters.AddWithValue("@expired", expired);
            command.Parameters.AddWithValue("@asOf", asOf);
            command.Parameters.AddWithValue("@hp", house.Hp);
        }
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"Housing {house.Id} no longer matches its expected ownership or tax period.");
    }
}
