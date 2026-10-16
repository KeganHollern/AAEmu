using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.AI.v2.Framework;

internal sealed class NpcWeaponCadence
{
    private readonly Dictionary<uint, DateTime> _started = [];

    internal static bool IsBasicAttack(uint skillId) => skillId is 2 or 3 or 4;

    internal bool CanUse(Unit owner, uint skillId, DateTime now)
    {
        if (!IsBasicAttack(skillId) || !_started.TryGetValue(skillId, out var started))
            return true;

        // Native NPC intervals use the common fist spec, even when the NPC has equipment.
        // Keep skill cooldowns and authored AI delays outside this weapon-only gate.
        var baseMilliseconds = ItemManager.Instance.GetHoldable(0).Speed;
        var interval = AttackTiming.GetWeaponInterval(owner, baseMilliseconds, skillId == 4, false);
        return now >= started.AddMilliseconds(interval);
    }

    internal void RecordResult(uint skillId, DateTime started, SkillResult result)
    {
        if (IsBasicAttack(skillId) && result == SkillResult.Success)
            _started[skillId] = started;
    }

    internal void Reset() => _started.Clear();
}
