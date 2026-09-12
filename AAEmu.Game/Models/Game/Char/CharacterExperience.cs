namespace AAEmu.Game.Models.Game.Char;

public partial class Character
{
    internal int CalculateExperienceGain(int amount, bool labor = false) =>
        ScaleExperienceGain(amount, AppConfiguration.Instance.World.ExpRate, labor);

    internal int ScaleExperienceGain(int amount, double worldRate, bool labor = false)
    {
        if (amount <= 0)
            return amount;
        var result = amount * worldRate * ExperienceMultiplier;
        if (labor)
            result *= LaborExperienceMultiplier;
        return (int)Math.Clamp(result, 0, int.MaxValue);
    }

    internal void AddLaborExperience(int amount)
    {
        lock (StorePurchaseSyncRoot)
            AddExpLocked(amount, true, labor: true);
    }

    internal void RestoreExperience(int amount)
    {
        if (amount <= 0)
            return;
        lock (StorePurchaseSyncRoot)
            AddExpLocked(amount, false, applyModifiers: false);
    }
}
