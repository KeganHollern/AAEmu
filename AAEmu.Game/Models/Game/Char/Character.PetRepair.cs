using System.Runtime.ExceptionServices;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Char;

public partial class Character
{
    public bool RepairPets(Npc npc) => RepairPets(npc, () => SaveManager.Instance.TryCommitEconomy([this]));

    internal static int GetPetRepairCost(byte level)
    {
        // r208022 39447a40 rounds each item separately, with float intermediates.
        var power = (float)Math.Pow(level, (double)2.3f);
        var subtotal = level * 0.5f + power;
        return (int)MathF.Floor(subtotal + 0.5f);
    }

    internal bool RepairPets(Npc npc, Func<bool> commit)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (Hp <= 0 || Inventory == null || Mates == null ||
                !ServiceInteraction.CanUseNpc(this, npc, template => template.Stabler))
            {
                SendErrorMessage(ErrorMessageType.NoInteractionAvailable);
                return false;
            }

            var repairs = new List<PetRepairState>();
            var activeMates = ParentWorld.MateManager.GetActiveMates(Id);
            long cost = 0;
            foreach (var container in new[] { Inventory.Bag, Inventory.Warehouse })
            {
                foreach (var item in container.Items.OfType<SummonMate>().OrderBy(item => item.Slot))
                {
                    if (!item.DetailInjured || item.Template is not SummonMateTemplate || item.TemplateId == 0 ||
                        item.Count <= 0 || item.OwnerId != Id || item.SlotType != container.ContainerType ||
                        !ReferenceEquals(item._holdingContainer, container) ||
                        !ReferenceEquals(container.GetItemBySlot(item.Slot), item) ||
                        !ReferenceEquals(Inventory.GetItemById(item.Id), item) ||
                        TradeReservation.GetReservedCount(item) != 0)
                        continue;

                    var saved = Mates.GetMateInfo(item.Id);
                    if (saved != null && (saved.Owner != Id || saved.ItemId != item.Id))
                        continue;
                    var mate = activeMates.FirstOrDefault(value => value.ItemId == item.Id);
                    if (mate != null && (mate.IsTemporarySummon || mate.AttachmentsRetired || mate.OwnerId != Id ||
                        mate.OwnerObjId != ObjId || !ReferenceEquals(mate.ParentWorld, ParentWorld) ||
                        !ParentWorld.MateManager.IsOwnedMate(this, mate) ||
                        !ReferenceEquals(mate.SummonItem, item) || saved == null ||
                        !ReferenceEquals(mate.DbInfo, saved)))
                        continue;

                    cost += GetPetRepairCost(item.DetailLevel);
                    if (cost > int.MaxValue)
                        return false;
                    repairs.Add(new PetRepairState(item, saved, mate));
                }
            }
            if (repairs.Count == 0)
                return false;

            using var inventory = new InventoryMutation(ItemTaskType.RepairPets);
            if (!inventory.TryChangeMoney(this, -(int)cost))
            {
                SendErrorMessage(ErrorMessageType.NotEnoughMoney);
                return false;
            }
            var settled = false;
            try
            {
                foreach (var repair in repairs)
                    repair.Prepare();
                bool committed;
                try
                {
                    committed = commit();
                }
                catch
                {
                    // SaveManager stops Game when the commit outcome is uncertain.
                    // Do not restore assets that the database might already contain.
                    settled = true;
                    inventory.PreservePreparedState();
                    throw;
                }
                if (!committed)
                {
                    SendErrorMessage(ErrorMessageType.InternalError);
                    return false;
                }
                settled = true;
                try
                {
                    // Finish every active recovery even if a disconnected observer
                    // throws during one notification. No committed asset can roll back.
                    Exception notificationFailure = null;
                    foreach (var repair in repairs)
                        Publish(() => repair.Mate?.PublishStablemasterRecovery());
                    Publish(() => inventory.Complete());
                    foreach (var batch in repairs.Chunk(30))
                        Publish(() => SendPacket(new SCItemTaskSuccessPacket(ItemTaskType.RepairPets,
                            batch.Select(repair => (ItemTask)new ItemUpdate(repair.Item)).ToList(), [])));
                    if (notificationFailure != null)
                        ExceptionDispatchInfo.Capture(notificationFailure).Throw();
                    return true;

                    void Publish(Action notify)
                    {
                        try { notify(); }
                        catch (Exception exception) { notificationFailure ??= exception; }
                    }
                }
                catch
                {
                    inventory.PreservePreparedState();
                    throw;
                }
            }
            finally
            {
                if (!settled)
                    foreach (var repair in repairs)
                        repair.Restore();
            }
        }
    }

    private sealed class PetRepairState(SummonMate item, MateDb saved, Units.Mate mate)
    {
        public SummonMate Item { get; } = item;
        public Units.Mate Mate { get; } = mate;
        private readonly bool _injured = item.DetailInjured;
        private readonly bool _dirty = item.IsDirty;
        private readonly int _hp = saved?.Hp ?? 0;
        private readonly int _mp = saved?.Mp ?? 0;
        private readonly DateTime _updatedAt = saved?.UpdatedAt ?? default;
        private Action _restoreMate;

        public void Prepare()
        {
            _restoreMate = Mate?.PrepareStablemasterRecovery();
            Item.DetailInjured = false;
            Item.IsDirty = true;
            if (saved == null)
                return;
            // Injury treatment preserves current points. Old dead rows become
            // living at 1 HP so a later summon does not restore the injury.
            saved.Hp = Math.Max(1, Mate?.Hp ?? saved.Hp);
            saved.Mp = Mate?.Mp ?? saved.Mp;
            saved.UpdatedAt = DateTime.UtcNow;
        }

        public void Restore()
        {
            _restoreMate?.Invoke();
            Item.DetailInjured = _injured;
            Item.IsDirty = _dirty;
            if (saved == null)
                return;
            saved.Hp = _hp;
            saved.Mp = _mp;
            saved.UpdatedAt = _updatedAt;
        }
    }
}
