using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Skills;

namespace AAEmu.Game.Models.Game.Char;

public partial class Character
{
    // The caller already applied the normal experience modifiers.
    private void StageSkillExperience(SkillLaborBatch batch, int amount, bool addAbilityExperience)
    {
        var oldExperience = Experience;
        var oldLevel = Level;
        var oldAbilities = Abilities.Abilities.ToDictionary(pair => pair.Key, pair => pair.Value.Exp);
        batch.Enlist(null, () =>
        {
            Experience = oldExperience;
            Level = oldLevel;
            foreach (var (id, experience) in oldAbilities)
                Abilities.Abilities[id].Exp = experience;
        });
        var experienceAfter = (int)Math.Clamp((long)Experience + amount, 0, int.MaxValue);
        var levelAfter = ExperienceManager.Instance.GetLevelFromExp(experienceAfter, Level, out var overflow);
        if (levelAfter >= ExperienceManager.Instance.MaxPlayerLevel)
            experienceAfter -= overflow;
        Experience = experienceAfter;
        Level = levelAfter;
        var active = Abilities.GetActiveAbilities();
        if (addAbilityExperience)
        {
            var maximum = ExperienceManager.Instance.GetExpForLevel(ExperienceManager.Instance.MaxPlayerLevel);
            foreach (var id in active)
                Abilities.Abilities[id].Exp = (int)Math.Clamp((long)Abilities.Abilities[id].Exp + amount, 0, maximum);
        }
        batch.AfterCommit(() =>
        {
            Achievements?.UpdateLevel(levelAfter);
            if (addAbilityExperience)
                foreach (var id in active)
                    Achievements?.UpdateAbilityLevel(id, Abilities.GetAbilityLevel(id));
            SendPacket(new SCExpChangedPacket(ObjId, amount, addAbilityExperience));
            if (levelAfter > oldLevel)
            {
                Expedition?.OnCharacterRefresh(this);
                BroadcastPacket(new SCLevelChangedPacket(ObjId, levelAfter), true);
            }
            if (Connection != null)
                QuestManager.Instance.DoOnLevelUpEvents(Connection.ActiveChar);
        });
    }
}
