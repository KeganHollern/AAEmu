using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Packets.C2G;

public class CSMoveUnitPacketTests
{
    [Test]
    public async Task ShouldIncludeTargetCharacter_MovementAuthorIsTarget_ReturnsFalse()
    {
        var movementAuthor = new CharacterMock { ObjId = 100 };

        await Assert.That(CSMoveUnitPacket.ShouldIncludeTargetCharacter(movementAuthor, movementAuthor)).IsFalse();
    }

    [Test]
    public async Task ShouldIncludeTargetCharacter_DifferentCharacterIsTarget_ReturnsTrue()
    {
        var movementAuthor = new CharacterMock { ObjId = 100 };
        var targetCharacter = new CharacterMock { ObjId = 200 };

        await Assert.That(CSMoveUnitPacket.ShouldIncludeTargetCharacter(movementAuthor, targetCharacter)).IsTrue();
    }

    [Test]
    public async Task ShouldIncludeTargetCharacter_NonCharacterIsTarget_ReturnsFalse()
    {
        var movementAuthor = new CharacterMock { ObjId = 100 };
        var targetUnit = new BaseUnit { ObjId = 200 };

        await Assert.That(CSMoveUnitPacket.ShouldIncludeTargetCharacter(movementAuthor, targetUnit)).IsFalse();
    }
}
