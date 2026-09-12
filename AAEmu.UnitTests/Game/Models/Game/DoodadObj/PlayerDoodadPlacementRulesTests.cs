using System.Numerics;

using AAEmu.Game.Models.Game.DoodadObj;

namespace AAEmu.UnitTests.Game.Models.Game.DoodadObj;

public sealed class PlayerDoodadPlacementRulesTests
{
    [Test]
    [Arguments(30f, 0f, 0f, true)]
    [Arguments(30.001f, 0f, 0f, false)]
    [Arguments(0f, 0f, 30f, true)]
    [Arguments(0f, 0f, 30.001f, false)]
    [Arguments(20f, 20f, 20f, false)]
    public async Task Check_UsesNativeThreeDimensionalRange(float x, float y, float z, bool expected)
    {
        var result = PlayerDoodadPlacementRules.Check(new Vector3(100, 200, 300),
            new Vector3(100 + x, 200 + y, 300 + z), 0, 1, 0, 54);
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0u, 54u, true)]
    [Arguments(54u, 54u, true)]
    [Arguments(54u, 43u, false)]
    [Arguments(0u, 0u, false)]
    public async Task Check_RequiresTheAuthoredZoneGroup(uint restriction, uint actual, bool expected)
    {
        await Assert.That(PlayerDoodadPlacementRules.Check(Vector3.Zero, Vector3.One, 0, 1,
            restriction, actual)).IsEqualTo(expected);
    }

    [Test]
    public async Task Check_RejectsNonFiniteCoordinatesRotationAndScale()
    {
        foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            await Assert.That(PlayerDoodadPlacementRules.Check(new Vector3(value, 0, 0), Vector3.Zero,
                0, 1, 0, 1)).IsFalse();
            await Assert.That(PlayerDoodadPlacementRules.Check(Vector3.Zero, new Vector3(0, 0, value),
                0, 1, 0, 1)).IsFalse();
            await Assert.That(PlayerDoodadPlacementRules.Check(Vector3.Zero, Vector3.Zero,
                value, 1, 0, 1)).IsFalse();
            await Assert.That(PlayerDoodadPlacementRules.Check(Vector3.Zero, Vector3.Zero,
                0, value, 0, 1)).IsFalse();
        }
        await Assert.That(PlayerDoodadPlacementRules.Check(Vector3.Zero, Vector3.Zero, 0, 0, 0, 1)).IsFalse();
        await Assert.That(PlayerDoodadPlacementRules.Check(Vector3.Zero, Vector3.Zero, 0, -1, 0, 1)).IsFalse();
    }
}
