using System.Numerics;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;

namespace AAEmu.Game.Models.Game.Char;

public partial class Character
{
    private ResurrectionOffer _resurrectionOffer;

    public bool BuyPriestBuff(uint offerId, Npc npc)
    {
        lock (StorePurchaseSyncRoot)
        {
            var offer = PriestBuffGameData.Instance.Get(offerId);
            var template = offer == null ? null : SkillManager.Instance.GetBuffTemplate(offer.BuffId);
            if (Hp <= 0 || IsInBattle || offer == null || template == null ||
                !offer.TryGetCost(Level, out var cost) || Buffs.CheckBuff(offer.BuffId) ||
                !ServiceInteraction.CanUseNpc(this, npc, value => value.Priest))
            {
                SendErrorMessage(ErrorMessageType.InvalidTarget);
                return false;
            }
            var buff = new Buff(this, npc, new SkillCasterUnit(npc.ObjId), template, null, DateTime.UtcNow);
            return CompletePriestPurchase(cost,
                () =>
                {
                    Buffs.AddBuff(buff);
                    if (!ReferenceEquals(Buffs.GetEffectFromBuffId(offer.BuffId), buff))
                        throw new InvalidOperationException("Priest buff was not applied");
                },
                () => Buffs.RemoveEffect(buff),
                () => SaveManager.Instance.TryCommitEconomy([this]));
        }
    }

    internal bool CompletePriestPurchase(int cost, Action applyBuff, Action removeBuff, Func<bool> commit)
    {
        lock (StorePurchaseSyncRoot)
        {
            using var inventory = new InventoryMutation(ItemTaskType.StoreBuy);
            if (cost < 0 || (cost > 0 && !inventory.TryChangeMoney(this, -cost)))
            {
                SendErrorMessage(ErrorMessageType.NotEnoughCoin);
                return false;
            }
            var commitAttempted = false;
            try
            {
                applyBuff();
                // False means a known rollback. SaveManager stops Game on an unconfirmed commit.
                commitAttempted = true;
                if (!commit())
                {
                    commitAttempted = false;
                    removeBuff();
                    SendErrorMessage(ErrorMessageType.InvalidTarget);
                    return false;
                }
                inventory.Complete();
                return true;
            }
            catch
            {
                if (commitAttempted)
                    inventory.PreservePreparedState();
                else
                    removeBuff();
                throw;
            }
        }
    }

    internal BuffTemplate FindPriestResurrectionBuff(DateTime now)
    {
        foreach (var offer in PriestBuffGameData.Instance.Offers)
        {
            var buff = Buffs.GetEffectFromBuffId(offer.BuffId);
            if (HasActivePriestResurrection(buff, now))
                return buff.Template;
        }
        return null;
    }

    internal static bool HasActivePriestResurrection(Buff buff, DateTime now) =>
        buff != null && buff.State == EffectState.Acting && buff.Duration > 0 &&
        now < buff.StartTime.AddMilliseconds(buff.Duration) && buff.Template.ResurrectionHealth > 0;

    internal void OfferResurrection(SkillCaster caster, int health, int mana, bool percent = true, int restoreExperience = 0)
    {
        lock (StorePurchaseSyncRoot)
        {
            if (Hp > 0 || health <= 0 || mana < 0 || (percent && (health > 100 || mana > 100)))
                return;
            if (_resurrectionOffer?.DeathTime == DeadTime)
                restoreExperience = Math.Max(restoreExperience, _resurrectionOffer.RestoreExperience);
            _resurrectionOffer = new ResurrectionOffer(DeadTime, Transform.World.Position, Transform.World.Rotation.Z,
                health, mana, percent, Math.Max(0, restoreExperience));
            SendPacket(new SCNotifyResurrectionPacket(caster));
        }
    }

    internal bool TryTakeResurrectionOffer(out ResurrectionOffer offer)
    {
        lock (StorePurchaseSyncRoot)
        {
            offer = _resurrectionOffer;
            _resurrectionOffer = null;
            return Hp <= 0 && offer != null && offer.DeathTime == DeadTime;
        }
    }

    internal void ClearResurrectionOffer() => _resurrectionOffer = null;

    internal void RestorePriestExperience(ResurrectionOffer offer)
    {
        if (offer.RestoreExperience <= 0)
            return;
        var restore = Math.Min(offer.RestoreExperience, Math.Max(0, LastExpLoss));
        Experience = checked(Experience + restore);
        LastExpLoss = 0;
        RecoverableExp = 0;
        SendPacket(new SCExpChangedPacket(ObjId, restore, false));
        SendPacket(new SCRecoverableExpPacket(ObjId, 0, 0, 0));
    }

    internal sealed record ResurrectionOffer(DateTime DeathTime, Vector3 Position, float Rotation,
        int Health, int Mana, bool Percent, int RestoreExperience)
    {
        public int GetHealth(int maximum) => Math.Max(1, Percent ? (int)((long)maximum * Health / 100) : Math.Min(maximum, Health));
        public int GetMana(int maximum) => Percent ? (int)((long)maximum * Mana / 100) : Math.Min(maximum, Mana);
    }
}
