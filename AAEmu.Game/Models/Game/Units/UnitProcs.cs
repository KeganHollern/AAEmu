using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Items.Procs;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units.Static;

namespace AAEmu.Game.Models.Game.Units;

public class UnitProcs
{
    private readonly object _sync = new();
    private readonly Dictionary<uint, ItemProc> _procs = [];
    private Dictionary<uint, int> _activeLevels = [];
    private readonly Func<IItemManager> _items;
    private readonly Func<DateTime> _now;
    private readonly Func<double> _roll;
    private readonly Func<Unit, Unit, SkillTemplate, int, bool> _cast;
    private int _rolling;

    public Unit Owner { get; }

    public UnitProcs(Unit owner) : this(owner, () => ItemManager.Instance, () => DateTime.UtcNow,
        Random.Shared.NextDouble, ItemProc.Cast)
    {
    }

    internal UnitProcs(Unit owner, Func<IItemManager> items, Func<DateTime> now, Func<double> roll,
        Func<Unit, Unit, SkillTemplate, int, bool> cast)
    {
        Owner = owner;
        _items = items;
        _now = now;
        _roll = roll;
        _cast = cast;
    }

    public void RefreshEquipment()
    {
        // OnLeaveContainer can run before the source list removes the moved item.
        var equipment = Owner.Equipment.Items
            .Where(item => ReferenceEquals(item._holdingContainer, Owner.Equipment)).ToArray();
        RefreshSources(EquipmentProcSource.Collect(equipment, _items()));
    }

    internal void RefreshSources(IEnumerable<EquipmentProcSource> sources)
    {
        var levels = sources.GroupBy(source => source.Key.ProcId)
            .ToDictionary(group => group.Key, group => group.Max(source => source.ItemLevel));
        lock (_sync)
        {
            foreach (var procId in levels.Keys)
                if (!_procs.ContainsKey(procId) && _items().GetItemProcTemplate(procId) is { } template)
                    _procs.Add(procId, new ItemProc(template));
            _activeLevels = levels;
            // Retain cooldowns after unequip. Re-equipping must not bypass them.
        }
    }

    internal static double GetChance(ItemProcTemplate template, int itemLevel)
    {
        // Explicit r208022 server policy, approved in cluster issue #313.
        return Math.Clamp(template.ChanceRate + Math.Max(0, itemLevel) * (double)template.ItemLevelBasedChanceBonus / 100,
            0, 100);
    }

    public void RollProcsForKind(ProcChanceKind kind, Unit eventTarget = null, bool killingBlow = false)
    {
        RollProcs([kind], eventTarget, killingBlow);
    }

    private void RollProcs(IReadOnlyCollection<ProcChanceKind> kinds, Unit eventTarget, bool killingBlow)
    {
        if (Owner.Hp <= 0 || Interlocked.Exchange(ref _rolling, 1) != 0)
            return;
        try
        {
            (ItemProc Proc, int Level)[] candidates;
            lock (_sync)
                candidates = _activeLevels.Where(pair => _procs.TryGetValue(pair.Key, out var proc) && kinds.Contains(proc.Template.ChanceKind))
                    .Select(pair => (_procs[pair.Key], pair.Value)).ToArray();
            var now = _now();
            foreach (var (proc, level) in candidates)
            {
                lock (_sync)
                    if (!_activeLevels.ContainsKey(proc.TemplateId))
                        continue;
                proc.Apply(Owner, eventTarget, killingBlow, GetChance(proc.Template, level), level, now, _roll, _cast);
            }
        }
        finally
        {
            Volatile.Write(ref _rolling, 0);
        }
    }

    internal static void OnDamage(Unit attacker, Unit target, DamageType damageType, bool critical,
        bool killingBlow, bool fireProc, bool isItemProc)
    {
        if (!fireProc || isItemProc)
            return;
        attacker.Procs?.RollProcs(GetDamageKinds(damageType, critical, false), target, killingBlow);
        target.Procs?.RollProcs(GetDamageKinds(damageType, critical, true), attacker, false);
    }

    internal static IReadOnlyCollection<ProcChanceKind> GetDamageKinds(DamageType type, bool critical, bool received)
    {
        var kinds = new List<ProcChanceKind> { received ? ProcChanceKind.TakeDamageAny : ProcChanceKind.HitAny };
        var specific = type switch
        {
            DamageType.Melee => received ? ProcChanceKind.TakeDamageMelee : ProcChanceKind.HitMelee,
            DamageType.Magic => received ? ProcChanceKind.TakeDamageSpell : ProcChanceKind.HitSpell,
            DamageType.Ranged => received ? ProcChanceKind.TakeDamageRanged : ProcChanceKind.HitRanged,
            DamageType.Siege => received ? ProcChanceKind.TakeDamageSiege : ProcChanceKind.HitSiege,
            _ => (ProcChanceKind)0
        };
        if (specific != 0)
            kinds.Add(specific);
        if (critical && type != DamageType.Siege && specific != 0)
            kinds.Add(specific + 1);
        return kinds;
    }

    internal void OnHeal(Unit target, bool critical, bool isItemProc)
    {
        if (isItemProc)
            return;
        RollProcs(critical ? [ProcChanceKind.HitHeal, ProcChanceKind.HitHealCrit] : [ProcChanceKind.HitHeal], target, false);
    }
}
