using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.TowerDefs;

namespace AAEmu.Game.Models.Game.Skills.Plots.Tree;

public class PlotState(
    BaseUnit caster,
    SkillCaster casterCaster,
    BaseUnit target,
    SkillCastTarget targetCaster,
    SkillObject skillObject,
    Skill skill)
{
    private volatile bool _cancellationRequest;
    private readonly object _castWaitLock = new();
    private readonly List<CastWindow> _castWaits = [];
    private bool _cancelledWhileCasting;
    private bool _cancelledWhileChanneling;

    internal CastWindow RegisterCastWait(PlotNextEvent next, DateTime deadline)
    {
        if (next == null || (!next.Casting && !next.Channeling))
            return null;
        var wait = new CastWindow(deadline, next.Casting, next.Channeling, next.CastingDelayable);
        lock (_castWaitLock)
        {
            _castWaits.RemoveAll(window => !window.Active);
            _castWaits.Add(wait);
        }
        return wait;
    }

    internal bool CancelCastWaits()
    {
        lock (_castWaitLock)
        {
            var active = _castWaits.Where(wait => wait.Active).ToArray();
            if (active.Length == 0)
                return false;
            _cancelledWhileCasting |= active.Any(wait => wait.Casting);
            _cancelledWhileChanneling |= active.Any(wait => wait.Channeling);
            // Publish cancellation before closing a wait that the plot loop can see.
            _cancellationRequest = true;
            foreach (var wait in active)
                wait.TryCancel();
            return true;
        }
    }

    internal bool DelayCastWaits(DateTime now, int milliseconds)
    {
        lock (_castWaitLock)
        {
            var delayed = false;
            foreach (var wait in _castWaits)
                delayed |= wait.TryDelay(now, milliseconds);
            return delayed;
        }
    }

    internal bool HasCastWaits
    {
        get
        {
            lock (_castWaitLock)
                return _castWaits.Any(wait => wait.Active);
        }
    }

    internal bool HasDelayableCastWaits
    {
        get
        {
            lock (_castWaitLock)
                return _castWaits.Any(wait => wait.Delayable && wait.Active);
        }
    }

    private readonly TowerDefenseSpawnToken _eventToken = (caster as Npc)?.TowerDefenseSpawnToken;
    private bool _finishChanneling = false;
    private readonly Dictionary<uint, float> _aoeDamageMultipliers = [];
    public Dictionary<uint, int> Tickets { get; set; } = [];
    public int[] Variables { get; set; } = new int[12];
    public byte CombatDiceRoll { get; set; }
    public bool IsCasting
    {
        get
        {
            lock (_castWaitLock)
                return _cancelledWhileCasting || _castWaits.Any(wait => wait.Casting && wait.Active);
        }
    }
    public bool IsChanneling
    {
        get
        {
            lock (_castWaitLock)
                return _cancelledWhileChanneling || _castWaits.Any(wait => wait.Channeling && wait.Active);
        }
    }

    public Skill ActiveSkill { get; set; } = skill;
    public Unit Caster { get; set; } = caster as Unit;
    public SkillCaster CasterCaster { get; set; } = casterCaster;
    public BaseUnit Target { get; set; } = target;
    public SkillCastTarget TargetCaster { get; set; } = targetCaster;
    public SkillObject SkillObject { get; set; } = skillObject;
    public List<(BaseUnit unit, uint buffId)> ChanneledBuffs { get; set; } = [];

    public Dictionary<uint, List<GameObject>> HitObjects { get; set; } = [];

    internal float GetAoeDamageMultiplier(Unit target)
    {
        // Per-target child nodes share this cast state. Repeated hits on the same
        // target keep their rate until the plot executes its authored reset effect.
        if (!_aoeDamageMultipliers.TryGetValue(target.ObjId, out var multiplier))
        {
            multiplier = AoeDiminishingGameData.Instance.GetMultiplier(_aoeDamageMultipliers.Count + 1);
            _aoeDamageMultipliers.Add(target.ObjId, multiplier);
        }
        return multiplier;
    }

    internal void ResetAoeDiminishing()
    {
        _aoeDamageMultipliers.Clear();
    }

    public bool CancellationRequested() => _cancellationRequest ||
        (_eventToken != null && (_eventToken.Lifetime.IsCancelled ||
         Caster is not Npc npc || !ReferenceEquals(npc.TowerDefenseSpawnToken, _eventToken)));
    public bool RequestCancellation()
    {
        lock (_castWaitLock)
        {
            _cancelledWhileCasting |= _castWaits.Any(wait => wait.Casting && wait.Active);
            _cancelledWhileChanneling |= _castWaits.Any(wait => wait.Channeling && wait.Active);
            return _cancellationRequest = true;
        }
    }
    public bool ChannelingFinishRequested() => _finishChanneling;
    public bool FinishChanneling() => _finishChanneling = true;
    public bool PermitChanneling() => _finishChanneling = false;
}
