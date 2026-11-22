using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.World.Interactions;

public class Demolish : IWorldInteraction
{
    public void Execute(BaseUnit caster, SkillCaster casterType, BaseUnit target, SkillCastTarget targetType,
        uint skillId, uint doodadId, DoodadFuncTemplate objectFunc = null)
    {
        if (target is House house && caster is Character { Connection: { IsClosed: false } connection } character &&
            ReferenceEquals(connection.ActiveChar, character))
        {
            HousingManager.Instance.Demolish(connection, house, false, false);
        }
    }
}
