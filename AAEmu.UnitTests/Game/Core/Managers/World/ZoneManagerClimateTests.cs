using System.Reflection;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.World.Zones;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

public class ZoneManagerClimateTests
{
    private const uint ZoneKey = 100;

    [Test]
    public async Task DoodadHasMatchingClimate_MatchingStaticClimate_ReturnsTrue()
    {
        var manager = CreateManager();
        var doodad = CreateDoodad(Climate.Arctic, ZoneKey);

        await Assert.That(manager.DoodadHasMatchingClimate(doodad)).IsTrue();
    }

    [Test]
    public async Task DoodadHasMatchingClimate_DifferentStaticClimate_ReturnsFalse()
    {
        var manager = CreateManager();
        var doodad = CreateDoodad(Climate.Tropical, ZoneKey);

        await Assert.That(manager.DoodadHasMatchingClimate(doodad)).IsFalse();
    }

    [Test]
    [Arguments(Climate.None)]
    [Arguments(Climate.Any)]
    public async Task DoodadHasMatchingClimate_NonSpecificClimate_ReturnsFalse(Climate climate)
    {
        var manager = CreateManager();
        var doodad = CreateDoodad(climate, ZoneKey);

        await Assert.That(manager.DoodadHasMatchingClimate(doodad)).IsFalse();
    }

    [Test]
    public async Task DoodadHasMatchingClimate_MissingTemplate_ReturnsFalse()
    {
        var manager = CreateManager();
        var doodad = new Doodad();
        doodad.Transform.ZoneId = ZoneKey;

        await Assert.That(manager.DoodadHasMatchingClimate(doodad)).IsFalse();
    }

    [Test]
    public async Task DoodadHasMatchingClimate_UnknownZone_ReturnsFalse()
    {
        var manager = CreateManager();
        var doodad = CreateDoodad(Climate.Arctic, 999);

        await Assert.That(manager.DoodadHasMatchingClimate(doodad)).IsFalse();
    }

    private static ZoneManager CreateManager()
    {
        var manager = new ZoneManager(Mock.Of<IWorldManager>().Object);
        SetPrivateField(manager, "_zones", new Dictionary<uint, Zone>
        {
            [ZoneKey] = new Zone { ZoneKey = ZoneKey, ZoneClimateId = 6 }
        });
        SetPrivateField(manager, "_climateElem", new Dictionary<uint, ZoneClimateElem>
        {
            [1] = new ZoneClimateElem { Id = 1, ZoneClimateId = 6, ClimateId = Climate.Temperate },
            [2] = new ZoneClimateElem { Id = 2, ZoneClimateId = 6, ClimateId = Climate.Arctic }
        });
        return manager;
    }

    private static Doodad CreateDoodad(Climate climate, uint zoneKey)
    {
        var doodad = new Doodad
        {
            Template = new DoodadTemplate { ClimateId = climate }
        };
        doodad.Transform.ZoneId = zoneKey;
        return doodad;
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        target.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);
    }
}
