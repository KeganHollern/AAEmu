using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

public class CharacterCraftTests
{
    [Test]
    public async Task CraftPackInteraction_DoesNotAdvanceTheStationPhase()
    {
        var station = new Doodad { ToNextPhase = true };
        new DoodadFuncCraftPack { CraftPackId = 33 }.Use(null, station, 0, -1);
        await Assert.That(station.ToNextPhase).IsFalse();
    }

    [Test]
    [Arguments(0u, new uint[] { }, 0u, new uint[] { }, true)]
    [Arguments(568u, new uint[] { }, 568u, new uint[] { }, true)]
    [Arguments(568u, new uint[] { 33u }, 2241u, new uint[] { 33u }, true)]
    [Arguments(7442u, new uint[] { 122u }, 7442u, new uint[] { }, false)]
    [Arguments(568u, new uint[] { 33u }, 568u, new uint[] { 34u }, false)]
    [Arguments(568u, new uint[] { }, 9999u, new uint[] { }, false)]
    [Arguments(568u, new uint[] { 33u }, 9999u, new uint[] { 34u }, false)]
    [Arguments(0u, new uint[] { 33u }, 9999u, new uint[] { 33u }, true)]
    [Arguments(0u, new uint[] { 33u }, 9999u, new uint[] { }, false)]
    public async Task IsCraftLocationAuthorized_UsesRequiredDoodadOrExposedPack(uint requiredDoodadId,
        uint[] allowedPacks, uint actualDoodadId, uint[] exposedPacks, bool expected)
    {
        var actual = CharacterCraft.IsCraftLocationAuthorized(requiredDoodadId, allowedPacks.ToHashSet(),
            actualDoodadId, exposedPacks.ToHashSet());

        await Assert.That(actual).IsEqualTo(expected);
    }
}
