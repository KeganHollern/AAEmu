using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Buffs;

namespace AAEmu.Game.Models.Game.Units;

public partial class Buffs
{
    private bool _laborPreview;
    private readonly HashSet<uint> _laborChangedBuffIds = [];

    internal void SaveLaborBuffs(AAEmu.Game.Core.Managers.PersistenceSaveContext context, uint characterId)
    {
        if (_laborChangedBuffIds.Count > 0)
            SaveActiveBuffs(context.Connection, context.Transaction, characterId, _laborChangedBuffIds);
    }

    internal Buffs CreateLaborPreview()
    {
        lock (_lock)
        {
            var preview = new Buffs(GetOwner()) { _laborPreview = true, _nextIndex = _nextIndex };
            foreach (var effect in _effects)
                preview._effects.Add(CloneLaborBuff(effect));
            foreach (var (id, counter) in _toleranceCounters)
                preview._toleranceCounters.Add(id, new BuffToleranceCounter
                {
                    Tolerance = counter.Tolerance, CurrentStep = counter.CurrentStep, LastStep = counter.LastStep
                });
            return preview;
        }
    }

    internal static Buff CloneLaborBuff(Buff buff) => new(buff.Owner, buff.Caster, buff.SkillCaster,
        buff.Template, buff.Skill, buff.StartTime)
    {
        Index = buff.Index, State = buff.State, InUse = buff.InUse, Duration = buff.Duration,
        Tick = buff.Tick, EndTime = buff.EndTime, Charge = buff.Charge,
        Passive = buff.Passive, AbLevel = buff.AbLevel
    };
}
