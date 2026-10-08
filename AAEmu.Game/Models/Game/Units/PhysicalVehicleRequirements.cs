using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Skills.Static;

namespace AAEmu.Game.Models.Game.Units;

internal static class PhysicalVehicleRequirements
{
    internal static UnitReqsValidationResult Validate(Character character)
    {
        // r208022 checks the vehicle bound to the driver, not a deck or an ancestor.
        if (character?.Transform.Parent?.GameObject is Slave vehicle)
        {
            lock (vehicle.AttachmentSyncRoot)
            {
                if (!vehicle.AttachmentsRetired && character.AttachedPoint == AttachPointKind.Driver &&
                    ReferenceEquals(character.Transform.Parent, vehicle.Transform) &&
                    vehicle.AttachedCharacters.TryGetValue(AttachPointKind.Driver, out var driver) &&
                    ReferenceEquals(driver, character) &&
                    ModelManager.Instance.GetVehicleModels(vehicle.ModelId)?.UseWheeledVehicleSimulation == true &&
                    vehicle.VehicleVelocity.LengthSquared() > 25f)
                    return new UnitReqsValidationResult(SkillResultKeys.skill_urk_unknown, 0x308, 0);
            }
        }

        return new UnitReqsValidationResult(SkillResultKeys.ok, 0, 0);
    }
}
