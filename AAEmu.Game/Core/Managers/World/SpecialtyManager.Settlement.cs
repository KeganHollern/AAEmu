using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Features;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Trading;

namespace AAEmu.Game.Core.Managers.World;

public partial class SpecialtyManager
{
    public int SellSpecialty(Character player, uint npcObjId)
    {
        lock (SaveManager.PersistenceSyncRoot)
        lock (AccountManager.Instance.GetAccountSyncRoot(player.AccountId))
        {
            var error = TryQuote(player, npcObjId, out var backpack, out var npc, out var destination, out var basePrice);
            if (error != ErrorMessageType.NoErrorMessage)
            {
                player.SendErrorMessage(error);
                return 0;
            }
            var commerce = player.Actability.Actabilities.GetValueOrDefault((uint)ActabilityType.Commerce);
            var laborCost = (short)Math.Ceiling(60 * (commerce?.GetLaborCostMultiplier() ?? 1f));
            if (player.LaborPower < laborCost)
            {
                player.SendErrorMessage(ErrorMessageType.NotEnoughLaborPower);
                return 0;
            }

            var utcNow = DateTime.UtcNow;
            var demand = GetDemand(backpack.TemplateId, destination, utcNow);
            var ratio = (int)decimal.Floor(demand.Ratio);
            var crafterId = backpack.MadeUnitId != player.Id ? backpack.MadeUnitId : 0;
            var share = crafterId != 0 && FeaturesManager.Fsets.Check(Feature.backpackProfitShare);
            var reward = npc.Template.SpecialtyCoinId;
            var payout = SpecialtyPrice.Calculate(basePrice, ratio, reward != 0, share);
            if (payout.Seller <= 0)
            {
                player.SendErrorMessage(ErrorMessageType.Invalid);
                return 0;
            }

            using var inventory = new InventoryMutation(ItemTaskType.SellBackpack);
            using var mails = mailManager.BeginMutation();
            using var labor = new CharacterLaborMutation(player);
            if (!inventory.TryConsume(player.Inventory.Equipment, backpack, 1) ||
                !labor.TryConsume(laborCost, (uint)ActabilityType.Commerce) ||
                !StagePayout(player.Id, player.Name, backpack.TemplateId, ratio, reward, payout, false, share,
                    utcNow, inventory, mails) ||
                (payout.Crafter > 0 && !StagePayout(crafterId, NameManager.Instance.GetCharacterName(crafterId),
                    backpack.TemplateId, ratio, reward, payout, true, true, utcNow, inventory, mails)))
            {
                player.SendErrorMessage(ErrorMessageType.MailUnknownFailure);
                return 0;
            }

            var nextDemand = demand.RecordSale(utcNow, AppConfiguration.Instance.Specialty);
            bool committed;
            try
            {
                committed = saveManager.Value.TryCommitEconomy([player], context =>
                {
                    labor.Save(context);
                    SpecialtyDemandStore.Save(context.Connection, context.Transaction, nextDemand);
                });
            }
            catch
            {
                inventory.PreservePreparedState();
                mails.PreservePreparedState();
                labor.PreservePreparedState();
                _demand[(backpack.TemplateId, destination)] = nextDemand;
                throw;
            }
            if (!committed)
            {
                player.SendErrorMessage(ErrorMessageType.MailUnknownFailure);
                return 0;
            }
            _demand[(backpack.TemplateId, destination)] = nextDemand;
            try
            {
                inventory.Complete();
                mails.Complete();
                labor.Complete();
            }
            catch
            {
                inventory.PreservePreparedState();
                mails.PreservePreparedState();
                labor.PreservePreparedState();
                throw;
            }
            player.Achievements?.Increment(CharRecordKind.SellItem, backpack.TemplateId, npc.TemplateId);
            return basePrice;
        }
    }

    private bool StagePayout(uint receiverId, string receiverName, uint packId, int ratio, uint reward,
        SpecialtyPayout payout, bool forCrafter, bool shared, DateTime utcNow, InventoryMutation inventory, MailMutation mails)
    {
        if (receiverId == 0 || string.IsNullOrEmpty(receiverName))
            return false;
        var amount = forCrafter ? payout.Crafter : payout.Seller;
        var mail = new BaseMail { MailType = MailType.SysSellBackpack, ReceiverName = receiverName, Title = "Speciality Payment" };
        mail.Header.SenderId = 0;
        mail.Header.SenderName = ".sellBackpack";
        mail.Header.ReceiverId = receiverId;
        mail.Body.SendDate = utcNow;
        mail.Body.RecvDate = utcNow.AddMinutes(AppConfiguration.Instance.Specialty.TradePackMailDelayInMinutes);
        var packName = LocalizationManager.Instance.Get("items", "name", packId).Replace("\\", "\\\\").Replace("'", "\\'");
        var receiverCase = forCrafter ? 0 : shared ? 1 : 2;
        if (reward == 0)
        {
            mail.Body.CopperCoins = amount;
            mail.Body.Text = FormattableString.Invariant(
                $"body('{packName}', {ratio}, {payout.Price}, {payout.PriceWithInterest}, 0, {amount}, {receiverCase}, 1, 0, 0)");
        }
        else
        {
            var container = itemManager.GetItemContainerForCharacter(receiverId, SlotType.Mail, null, 0);
            if (container == null || !inventory.TryGrant(container, reward, amount, out var items))
                return false;
            mail.Body.Attachments.AddRange(items);
            mail.Body.Text = FormattableString.Invariant(
                $"body('{packName}', {ratio}, 0, 0, 0, 0, {receiverCase}, 0, {payout.Crafter}, {payout.Seller})");
        }
        return mails.TryAdd(mail);
    }
}
