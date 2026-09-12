using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Formulas;

namespace AAEmu.Game.Models.Game.Char;

public partial class Character
{
    internal Action CaptureLaborRewardState()
    {
        var experience = Experience;
        var level = Level;
        var abilities = Abilities?.Values.ToDictionary(ability => ability, ability => ability.Exp);
        var actabilities = Actability?.Actabilities.Values.ToDictionary(actability => actability, actability => actability.Point);
        return () =>
        {
            Experience = experience;
            Level = level;
            if (abilities != null)
                foreach (var (ability, exp) in abilities)
                    ability.Exp = exp;
            if (actabilities != null)
                foreach (var (actability, points) in actabilities)
                    actability.Point = points;
        };
    }

    internal Action PrepareLaborRewards(short amount, uint actabilityId)
    {
        var actabilityChange = 0;
        byte step = 0;
        var expMultiplier = 1f;
        var pointsAfter = 0;
        var hasActability = actabilityId != 0 && Actability.Actabilities.TryGetValue(actabilityId, out _);
        if (hasActability)
        {
            var actability = Actability.Actabilities[actabilityId];
            expMultiplier = actability.GetExpMultiplier();
            step = actability.Step;
            var previousPoints = actability.Point;
            var limit = CharacterManager.Instance.GetExpertLimit(step);
            actability.Point = (int)Math.Min((long)previousPoints +
                (int)(amount * AppConfiguration.Instance.World.ActabilityRate), limit.UpLimit);
            pointsAfter = actability.Point;
            actabilityChange = pointsAfter - previousPoints;
        }

        Action publishExperience = null;
        var formula = FormulaManager.Instance.GetFormula((uint)FormulaKind.ExpByLaborPower);
        if (formula != null)
        {
            var rawExperience = (int)(formula.Evaluate(new Dictionary<string, double>
                { ["labor_power"] = amount, ["pc_level"] = Level }) * expMultiplier);
            if (rawExperience != 0)
                publishExperience = PrepareExperienceReward(CalculateExperienceGain(rawExperience, labor: true), true);
        }

        return () =>
        {
            if (hasActability)
                Achievements?.UpdateMaximum(CharRecordKind.GetActability, actabilityId, 0, (uint)Math.Max(pointsAfter, 0));
            publishExperience?.Invoke();
            Achievements?.Increment(CharRecordKind.SpendLabor, 0, 0, (uint)amount);
            SendPacket(new SCCharacterLaborPowerChangedPacket(-amount, (int)actabilityId, actabilityChange, step));
        };
    }
}
