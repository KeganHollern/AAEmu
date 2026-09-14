using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills;

internal static class SkillCooldowns
{
    // A server admission allowance, not a value claimed by the client.
    internal const int NetworkToleranceMilliseconds = 50;

    internal static SkillResult Check(Unit caster, SkillTemplate skill, bool bypassGlobalCooldown, DateTime utcNow)
    {
        if (Skill.CanIgnoreCooldowns(caster.GetOwnerCharacter()))
            return SkillResult.Success;
        if (caster.Cooldowns.CheckCooldown(skill))
            return SkillResult.CooldownTime;
        lock (caster.GcdLock)
        {
            return IsGlobalCooldownActive(caster, skill, bypassGlobalCooldown, utcNow)
                ? SkillResult.CooldownTime : SkillResult.Success;
        }
    }

    internal static bool TryStartGlobalCooldown(Unit caster, SkillTemplate skill, bool bypassGlobalCooldown, DateTime utcNow)
    {
        lock (caster.GcdLock)
        {
            if (IsGlobalCooldownActive(caster, skill, bypassGlobalCooldown, utcNow))
                return false;
            if (!bypassGlobalCooldown && !Skill.CanIgnoreCooldowns(caster.GetOwnerCharacter()))
            {
                var duration = skill.DefaultGcd ? 1000 : skill.CustomGcd;
                if (duration > 0)
                {
                    caster.GlobalCooldownDurationMilliseconds = ScaleGlobalCooldown(caster, duration);
                    caster.GlobalCooldown = utcNow.AddMilliseconds(caster.GlobalCooldownDurationMilliseconds);
                }
                caster.SkillLastUsed = utcNow;
            }
            return true;
        }
    }

    internal static void StartGlobalCooldown(Unit caster, int duration, DateTime utcNow)
    {
        if (Skill.CanIgnoreCooldowns(caster.GetOwnerCharacter()))
            return;
        lock (caster.GcdLock)
        {
            caster.GlobalCooldownDurationMilliseconds = ScaleGlobalCooldown(caster, duration);
            caster.GlobalCooldown = utcNow.AddMilliseconds(caster.GlobalCooldownDurationMilliseconds);
        }
    }

    private static bool IsGlobalCooldownActive(Unit caster, SkillTemplate skill, bool bypassGlobalCooldown, DateTime utcNow)
    {
        return !bypassGlobalCooldown && !skill.IgnoreGlobalCooldown &&
            !Skill.CanIgnoreCooldowns(caster.GetOwnerCharacter()) &&
            caster.GlobalCooldown - utcNow > TimeSpan.FromMilliseconds(
                Math.Min(NetworkToleranceMilliseconds, caster.GlobalCooldownDurationMilliseconds / 20.0));
    }

    internal static uint ScaleGlobalCooldown(Unit caster, int duration)
    {
        return (uint)Math.Clamp(Math.Floor(Math.Max(0, duration) * (caster.GlobalCooldownMul / 100.0)), 0, uint.MaxValue);
    }

    internal static void StartCooldown(Unit caster, Skill skill, double duration)
    {
        if (Skill.CanIgnoreCooldowns(caster.GetOwnerCharacter()))
            return;
        var milliseconds = (uint)Math.Clamp(double.IsFinite(duration) ? Math.Floor(duration) : 0, 0, uint.MaxValue);
        caster.Cooldowns.AddCooldown(skill.Template.Id, milliseconds, skill.Template.CooldownTagId);
    }
}
