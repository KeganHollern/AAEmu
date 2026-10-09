using System.Runtime.ExceptionServices;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Units.Static;

namespace AAEmu.Game.Models.Game.Units;

public sealed partial class Mate
{
    internal const uint InjuryBuffId = 1578;
    internal const uint DownedBuffId = 1579;

    internal SummonMate SummonItem { get; private set; }
    public bool IsInjured { get; private set; }
    public bool IsDowned { get; private set; }

    public override int Hp
    {
        get => base.Hp;
        set
        {
            lock (SaveManager.PersistenceSyncRoot)
                base.Hp = IsInjured && value > 1 ? 1 : value;
        }
    }

    internal void RestoreInjuryState(SummonMate item, int savedHp, bool downed)
    {
        if (IsTemporarySummon || item == null || item.Id != ItemId)
            return;
        SummonItem = item;
        IsInjured = item.DetailInjured || savedHp <= 0;
        IsDowned = IsInjured && downed;
        if (IsInjured)
        {
            Hp = 1;
            SetItemInjury(true);
            if (DbInfo != null)
                DbInfo.Hp = Hp;
        }
    }

    public override void ReduceCurrentHp(BaseUnit attacker, int value, KillReason killReason = KillReason.Damage)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!AttachmentsRetired)
                base.ReduceCurrentHp(attacker, value, killReason);
        }
    }

    protected override int GetHealthAfterDamage(BaseUnit attacker, int damage, KillReason killReason)
    {
        var health = base.GetHealthAfterDamage(attacker, damage, killReason);
        if (!IsTemporarySummon && SummonItem != null && health <= 0)
        {
            BecomeInjured();
            return 1;
        }
        return health;
    }

    public override void DoDie(BaseUnit killer, KillReason killReason)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (AttachmentsRetired)
                return;
            if (IsTemporarySummon || SummonItem == null)
            {
                base.DoDie(killer, killReason);
                return;
            }
            BecomeInjured();
            BroadcastPacket(new SCUnitPointsPacket(ObjId, Hp, Mp), true);
        }
    }

    private void BecomeInjured()
    {
        if (IsInjured && IsDowned)
        {
            Hp = 1;
            return;
        }
        IsInjured = true;
        IsDowned = true;
        Hp = 1;
        SetItemInjury(true);
        if (DbInfo != null)
        {
            DbInfo.Hp = Hp;
            DbInfo.Mp = Mp;
        }
        FallMovement.Reset();
        InterruptSkills();
        IsInBattle = false;
        CurrentTarget = null;
        ResetRidingMovement();
        foreach (var (seat, passenger) in Passengers.ToArray())
        {
            if (passenger._objId == 0)
                continue;
            var rider = WorldManager.Instance.GetCharacterByObjId(passenger._objId);
            if (rider != null)
                ParentWorld.MateManager.UnMountMate(rider, TlId, seat, AttachUnitReason.None);
        }
        RefreshInjuryBuffs();
        BroadcastPacket(new SCTargetChangedPacket(ObjId, 0), true);
        PublishSummonItem();
    }

    internal void RefreshInjuryBuffs()
    {
        SetRecoveryBuff(InjuryBuffId, IsInjured);
        SetRecoveryBuff(DownedBuffId, IsDowned);
    }

    private void SetRecoveryBuff(uint id, bool active)
    {
        if (!active)
        {
            Buffs.RemoveBuff(id);
            return;
        }
        if (Buffs.CheckBuff(id))
            return;
        var template = SkillManager.Instance.GetBuffTemplate(id) ??
            throw new InvalidOperationException($"Missing r208022 mate recovery buff {id}.");
        Buffs.AddBuff(new Buff(this, this, new SkillCasterUnit(ObjId), template, null, DateTime.UtcNow));
    }

    private void SetItemInjury(bool injured)
    {
        if (SummonItem == null || SummonItem.DetailInjured == injured)
            return;
        SummonItem.DetailInjured = injured;
        SummonItem.IsDirty = true;
    }

    internal void StageRecovery(SkillLaborBatch batch, bool getUpOnly, int restoredHp, int restoredMp)
    {
        var oldInjured = IsInjured;
        var oldDowned = IsDowned;
        var oldHp = Hp;
        var oldMp = Mp;
        var oldItemInjured = SummonItem.DetailInjured;
        var oldItemDirty = SummonItem.IsDirty;
        var oldDbHp = DbInfo.Hp;
        var oldDbMp = DbInfo.Mp;
        var oldUpdatedAt = DbInfo.UpdatedAt;
        batch.Enlist(null, () =>
        {
            IsInjured = oldInjured;
            IsDowned = oldDowned;
            Hp = oldHp;
            Mp = oldMp;
            SummonItem.DetailInjured = oldItemInjured;
            SummonItem.IsDirty = oldItemDirty;
            DbInfo.Hp = oldDbHp;
            DbInfo.Mp = oldDbMp;
            DbInfo.UpdatedAt = oldUpdatedAt;
        });

        IsDowned = false;
        if (!getUpOnly)
        {
            IsInjured = false;
            SetItemInjury(false);
            Hp = restoredHp;
            Mp = restoredMp;
        }
        DbInfo.Hp = Hp;
        DbInfo.Mp = Mp;
        batch.AfterCommit(() =>
        {
            RefreshInjuryBuffs();
            BroadcastPacket(new SCUnitPointsPacket(ObjId, Hp, Mp), true);
            PublishSummonItem();
        });
    }

    internal Action PrepareStablemasterRecovery()
    {
        var injured = IsInjured;
        var downed = IsDowned;
        var hp = Hp;
        var mp = Mp;
        IsInjured = false;
        IsDowned = false;
        Hp = Math.Max(1, hp);
        return () =>
        {
            IsInjured = injured;
            IsDowned = downed;
            Hp = hp;
            Mp = mp;
        };
    }

    internal void PublishStablemasterRecovery()
    {
        Exception notificationFailure = null;
        foreach (var id in new[] { InjuryBuffId, DownedBuffId })
        {
            try
            {
                // Exit marks the buff finished and removes it from the owner before
                // the dispel packet. RemoveBuff sends that packet before removal.
                Buffs.GetEffectFromBuffId(id)?.Exit();
            }
            catch (Exception exception)
            {
                notificationFailure ??= exception;
            }
        }
        try
        {
            BroadcastPacket(new SCUnitPointsPacket(ObjId, Hp, Mp), true);
        }
        catch (Exception exception)
        {
            notificationFailure ??= exception;
        }
        if (notificationFailure != null)
            ExceptionDispatchInfo.Capture(notificationFailure).Throw();
    }

    private void PublishSummonItem()
    {
        if (SummonItem != null && GetOwnerCharacter() is Character owner)
            owner.SendPacket(new SCItemTaskSuccessPacket(ItemTaskType.UpdateSummonMateItem,
                new ItemUpdate(SummonItem), []));
    }
}
