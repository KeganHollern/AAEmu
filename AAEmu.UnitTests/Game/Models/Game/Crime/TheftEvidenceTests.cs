using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.UnitTests.Game.Models.Game.Crime;

public sealed class TheftEvidenceTests
{
    [Test]
    [Arguments("no_actor")]
    [Arguments("no_actor_faction")]
    [Arguments("no_actor_id")]
    [Arguments("invalid_actor_faction")]
    [Arguments("no_doodad")]
    [Arguments("system_owner")]
    [Arguments("housing_owner")]
    [Arguments("slave_owner")]
    [Arguments("no_owner")]
    [Arguments("self_owner")]
    public async Task DirectTheft_InvalidOwnership_DoesNotReadTheDatabaseOrCreateEvidence(string scenario)
    {
        var criminal = new Character(new UnitCustomModelParams())
        {
            Id = 1,
            Faction = new SystemFaction { Id = FactionsEnum.NuiaAlliance }
        };
        var doodad = new Doodad { OwnerType = DoodadOwnerType.Character, OwnerId = 2 };
        switch (scenario)
        {
            case "no_actor": criminal = null; break;
            case "no_actor_faction": criminal.Faction = null; break;
            case "no_actor_id": criminal.Id = 0; break;
            case "invalid_actor_faction": criminal.Faction.Id = FactionsEnum.Invalid; break;
            case "no_doodad": doodad = null; break;
            case "system_owner": doodad.OwnerType = DoodadOwnerType.System; break;
            case "housing_owner": doodad.OwnerType = DoodadOwnerType.Housing; break;
            case "slave_owner": doodad.OwnerType = DoodadOwnerType.Slave; break;
            case "no_owner": doodad.OwnerId = 0; break;
            case "self_owner": doodad.OwnerId = criminal.Id; break;
        }

        // No database or world services exist in this unit fixture. Direct callers
        // must reject these inputs before they can reach either dependency.
        await Assert.That(new CrimeManager().GenerateEvidenceFromTheft(criminal, doodad)).IsNull();
    }
}
