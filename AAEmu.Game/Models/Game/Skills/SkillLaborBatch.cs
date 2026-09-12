using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Models.Game.Skills;

/// <summary>A synchronous effect batch. Each asynchronous plot continuation opens its own batch.</summary>
internal sealed class SkillLaborBatch
{
    [ThreadStatic] private static SkillLaborBatch s_current;
    public static SkillLaborBatch Current => s_current;
    public Character Owner { get; }
    public Skill Skill { get; }
    public InventoryMutation Inventory { get; }
    private readonly CharacterLaborMutation _labor;
    public bool IsCommitting { get; private set; }
    private readonly List<Action> _afterCommit = [];
    private readonly HashSet<object> _deferred = [];
    private readonly Dictionary<Doodad, Action> _doodads = [];
    private readonly HashSet<Doodad> _deletedDoodads = [];
    private readonly List<Action<PersistenceSaveContext>> _writes = [];
    private readonly List<Action> _restore = [];
    private readonly HashSet<Character> _participants = [];
    public IReadOnlyCollection<Character> Participants => _participants;

    private SkillLaborBatch(Character owner, Skill skill, InventoryMutation inventory, CharacterLaborMutation labor)
    {
        Owner = owner;
        Skill = skill;
        Inventory = inventory;
        _labor = labor;
        _participants.Add(owner);
    }

    public static SkillLaborBatch For(ICharacter owner)
    {
        if (s_current == null || owner == null)
            return null;
        if (ReferenceEquals(s_current.Owner, owner))
            return s_current;
        s_current.Fail();
        throw new InvalidOperationException("A paid skill cannot change another character's inventory.");
    }

    internal bool TryConsumeChildLabor(Skill child)
    {
        if (!ReferenceEquals(s_current, this) || IsCommitting || child == null)
            return Fail();
        if (child.LaborSettled)
            return true;
        var cost = child.GetLaborCost(Owner);
        if (cost == 0)
            return true;
        if (cost > short.MaxValue || !_labor.TryConsumeAdditional((short)cost, (uint)child.Template.ActabilityGroupId))
        {
            child.RejectLabor(Owner);
            return Fail();
        }
        child.LaborSettled = true;
        Enlist(null, () => child.LaborSettled = false);
        return true;
    }

    public void EnlistCharacter(Character character) => _participants.Add(character);
    public void Enlist(Action<PersistenceSaveContext> write, Action restore)
    {
        if (write != null)
            _writes.Add(write);
        if (restore != null)
            _restore.Add(restore);
    }

    public void TrackDoodad(Doodad doodad)
    {
        if (!_doodads.ContainsKey(doodad))
            _doodads.Add(doodad, doodad.CaptureLaborState());
    }

    public void DeleteDoodad(Doodad doodad, Action delete)
    {
        TrackDoodad(doodad);
        if (_deletedDoodads.Add(doodad))
        {
            var associatedItem = doodad.ItemId == 0 ? null : ItemManager.Instance.GetItemByItemId(doodad.ItemId);
            if (associatedItem is { _holdingContainer.ContainerType: SlotType.None or SlotType.System } &&
                !Inventory.TryConsume(associatedItem._holdingContainer, associatedItem, associatedItem.Count))
            {
                Fail();
                return;
            }
            doodad.MarkLaborDeletion(true);
            AfterCommit(() => { doodad.MarkLaborDeletion(false); delete(); });
        }
    }

    public void AfterCommit(Action action) => _afterCommit.Add(action);
    public bool DeferOnce(object key, Action action)
    {
        if (_deferred.Add(key))
            _afterCommit.Add(action);
        return true;
    }

    public bool Fail()
    {
        Skill.Cancelled = true;
        Owner.SkillCancelled = true;
        return false;
    }

    public int Consume(ItemContainer container, uint templateId, int count, Item preferred)
    {
        if (count <= 0)
            return 0;
        var items = preferred == null
            ? container.Items.Where(item => item.TemplateId == templateId).OrderBy(item => item.Slot).ToArray()
            : [preferred];
        var needed = count;
        foreach (var item in items)
        {
            var amount = Math.Min(needed, item.Count - TradeReservation.GetReservedCount(item));
            if (amount <= 0)
                continue;
            if (!Inventory.TryConsume(container, item, amount))
            {
                Fail();
                return 0;
            }
            needed -= amount;
            if (needed == 0)
                return count;
        }
        Fail();
        return 0;
    }

    public static bool Run(Character owner, Skill skill, bool chargeLabor, Action effects) =>
        RunCore(owner, skill, chargeLabor, effects, null);

    internal static bool RunPlacement(Character owner, Skill skill, int laborCost, Action effects)
    {
        if (laborCost < 0 || laborCost > short.MaxValue)
            return false;
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (s_current != null)
                return s_current.Fail();
            var previousCancelled = owner.SkillCancelled;
            owner.SkillCancelled = false;
            try
            {
                return RunCore(owner, skill, true, effects, laborCost);
            }
            finally
            {
                owner.SkillCancelled = previousCancelled;
            }
        }
    }

    private static bool RunCore(Character owner, Skill skill, bool chargeLabor, Action effects, int? placementLaborCost)
    {
        lock (SaveManager.PersistenceSyncRoot)
        lock (AccountManager.Instance.GetAccountSyncRoot(owner.AccountId))
        {
            if (skill.Cancelled)
                return false;
            if (s_current != null)
            {
                // Child effects can share their parent's batch, but cannot start a second paid cast.
                if (!ReferenceEquals(s_current.Owner, owner) || !ReferenceEquals(s_current.Skill, skill))
                    return s_current.Fail();
                effects();
                return !skill.Cancelled;
            }
            bool RejectLabor()
            {
                if (!placementLaborCost.HasValue)
                    return skill.RejectLabor(owner);
                owner.SendErrorMessage(ErrorMessageType.LaborPowerNeeded);
                return false;
            }

            var cost = placementLaborCost ?? skill.GetLaborCost(owner);
            if (!skill.LaborSettled && (cost > short.MaxValue || owner.LaborPower < cost))
                return RejectLabor();

            using var inventory = new InventoryMutation(placementLaborCost.HasValue
                ? ItemTaskType.DoodadCreate : ItemTaskType.SkillEffectGainItem);
            using var labor = new CharacterLaborMutation(owner);
            var charged = chargeLabor && !skill.LaborSettled && cost > 0;
            if (charged && !labor.TryConsume((short)cost, placementLaborCost.HasValue ? 0U : (uint)skill.Template.ActabilityGroupId))
                return RejectLabor();
            var batch = new SkillLaborBatch(owner, skill, inventory, labor);
            s_current = batch;
            var committed = false;
            try
            {
                effects();
                if (skill.Cancelled || owner.SkillCancelled || inventory.HasFailed)
                    return batch.Fail();
                if (charged && !placementLaborCost.HasValue && !skill.Template.PlotOnly && skill.Template.GainLifePoint > 0)
                    owner.ChangeGamePoints(GamePointKind.Vocation,
                        (int)Math.Ceiling(AppConfiguration.Instance.World.VocationRate * skill.Template.GainLifePoint));
                if (skill.Cancelled || owner.SkillCancelled || inventory.HasFailed)
                    return batch.Fail();
                if (labor.HasChanges || inventory.HasChanges || batch._doodads.Count > 0 || batch._writes.Count > 0 || batch._restore.Count > 0)
                {
                    batch.IsCommitting = true;
                    committed = skill.CommitLaborBatch(owner, context =>
                    {
                        if (labor.HasChanges)
                            labor.Save(context);
                        foreach (var doodad in batch._doodads.Keys)
                            doodad.SaveLaborState(context, batch._deletedDoodads.Contains(doodad));
                        foreach (var write in batch._writes)
                            write(context);
                    });
                    batch.IsCommitting = false;
                    if (!committed)
                    {
                        owner.SendErrorMessage(ErrorMessageType.InternalError);
                        return batch.Fail();
                    }
                }
                if (charged)
                {
                    skill.LaborSettled = true;
                    skill.LaborVocationSettled = !skill.Template.PlotOnly;
                }
                s_current = null;
                inventory.Complete();
                if (labor.HasChanges)
                    labor.Complete();
                foreach (var action in batch._afterCommit)
                    action();
                return true;
            }
            catch (Exception exception)
            {
                // A commit exception has an unknown outcome. Notifications run only after a known commit.
                if (committed || batch.IsCommitting)
                {
                    inventory.PreservePreparedState();
                    labor.PreservePreparedState();
                }
                skill.Cancelled = true;
                if (committed)
                    SaveManager.Instance.FailForConsistency(exception);
                throw;
            }
            finally
            {
                s_current = null;
                if (!committed && !batch.IsCommitting)
                {
                    foreach (var restore in batch._restore.AsEnumerable().Reverse())
                        restore();
                    foreach (var restore in batch._doodads.Values.Reverse())
                        restore();
                    if (skill.Cancelled && !placementLaborCost.HasValue)
                        owner.Craft?.CancelFromSkill(skill.Template.Id);
                }
            }
        }
    }
}
