using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Formulas;

internal static class CombatFormulas
{
    public static float GetDamageMultiplier(Unit source, Unit target, DamageType damageType, bool adjustByHeight)
    {
        var multiplier = 1d;
        if (source is Character && target is Npc)
            multiplier *= Evaluate(20, new() { ["pc_level"] = source.Level, ["npc_level"] = target.Level });
        else if (source is Npc && target is Character)
            multiplier *= Evaluate(21, new() { ["pc_level"] = target.Level, ["npc_level"] = source.Level });

        var sourcePosition = source.Transform.World.Position;
        var targetPosition = target.Transform.World.Position;
        if (adjustByHeight)
            multiplier *= Evaluate(11, new() { ["range"] = sourcePosition.Z - targetPosition.Z });
        if (damageType == DamageType.Ranged)
            multiplier *= Evaluate(12, new() { ["range"] = Vector3.Distance(sourcePosition, targetPosition) });

        return (float)Math.Max(0, multiplier);
    }

    public static float GetNpcPerceptionRange(int npcLevel, int playerLevel, float range)
    {
        return (float)Evaluate(5, new()
        {
            ["npc_level"] = npcLevel,
            ["pc_level"] = playerLevel,
            ["range"] = range
        });
    }

    public static float GetStealthDetectionRange(int sourceLevel, int targetLevel, uint stealthLevel,
        bool inFront, double modifier)
    {
        var range = inFront
            ? Evaluate(4, new()
            {
                ["source_level"] = sourceLevel,
                ["target_level"] = targetLevel,
                ["stealth_level"] = stealthLevel
            })
            : Evaluate(10, new());
        return (float)Math.Max(0, range * (100d + modifier) / 100d);
    }

    public static float GetBullsEyeReduction(int bullsEye, int facets)
    {
        return GetRatingPercent(24, "bulls_eye", bullsEye, facets);
    }

    public static float GetCriticalChanceReduction(int flexibility, int facets)
    {
        return GetRatingPercent(25, "flexibility", flexibility, facets);
    }

    public static float GetCriticalBonusReduction(int flexibility)
    {
        return flexibility <= 0 ? 0 : (float)Math.Max(0, Evaluate(26, new() { ["flexibility"] = flexibility }) / 10d);
    }

    private static float GetRatingPercent(uint id, string parameter, int rating, int facets)
    {
        if (rating <= 0 || facets <= 0)
            return 0;

        return (float)Math.Clamp(Evaluate(id, new() { [parameter] = rating }) / facets * 100d, 0, 100);
    }

    private static double Evaluate(uint id, Dictionary<string, double> parameters) =>
        FormulaManager.Instance.GetFormula(id).Evaluate(parameters);
}
