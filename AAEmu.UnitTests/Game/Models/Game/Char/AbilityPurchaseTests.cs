using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

public class AbilityPurchaseTests
{
    [Test]
    [Arguments((byte)10, 2000)]
    [Arguments((byte)50, 10000)]
    [Arguments((byte)55, 11000)]
    public async Task SwapCost_UsesExactClientLevelFactor(byte level, int expected)
    {
        var valid = CharacterAbilities.TryGetSwapCost(level, false, true,
            AbilityType.Fight, AbilityType.Magic, AbilityType.Love,
            AbilityType.Magic, AbilityType.Death, out var cost);
        await Assert.That(valid).IsTrue();
        await Assert.That(cost).IsEqualTo(expected);
    }

    [Test]
    [Arguments((byte)4, AbilityType.None, AbilityType.None, false)]
    [Arguments((byte)5, AbilityType.None, AbilityType.None, true)]
    [Arguments((byte)9, AbilityType.Magic, AbilityType.None, false)]
    [Arguments((byte)10, AbilityType.Magic, AbilityType.None, true)]
    [Arguments((byte)55, AbilityType.Magic, AbilityType.Love, false)]
    public async Task InitialChoice_PreservesSlotUnlockLevels(byte level, AbilityType second, AbilityType third, bool expected)
    {
        var valid = CharacterAbilities.TryGetSwapCost(level, false, true,
            AbilityType.Fight, second, third, AbilityType.None, AbilityType.Death, out var cost);
        await Assert.That(valid).IsEqualTo(expected);
        await Assert.That(cost).IsEqualTo(0);
    }

    [Test]
    [Arguments((byte)9, false, true, AbilityType.Magic, AbilityType.Death)]
    [Arguments((byte)50, true, true, AbilityType.Magic, AbilityType.Death)]
    [Arguments((byte)50, false, false, AbilityType.Magic, AbilityType.Death)]
    [Arguments((byte)50, false, true, AbilityType.Magic, AbilityType.Love)]
    [Arguments((byte)50, false, true, AbilityType.Magic, (AbilityType)255)]
    [Arguments((byte)50, false, true, (AbilityType)255, AbilityType.Death)]
    [Arguments((byte)50, false, true, AbilityType.Wild, AbilityType.Death)]
    public async Task Swap_InvalidRequestDoesNotProduceACharge(byte level, bool combat, bool alive,
        AbilityType oldAbility, AbilityType newAbility)
    {
        var valid = CharacterAbilities.TryGetSwapCost(level, combat, alive,
            AbilityType.Fight, AbilityType.Magic, AbilityType.Love, oldAbility, newAbility, out var cost);
        await Assert.That(valid).IsFalse();
        await Assert.That(cost).IsEqualTo(0);
    }

    [Test]
    public async Task Swap_WithoutNpcPreservesMoneyAndLearnedSkills()
    {
        var character = CreateCharacter(10000);
        var result = character.Abilities.Swap(AbilityType.Magic, AbilityType.Death, 999);
        await Assert.That(result).IsFalse();
        await Assert.That(character.Money).IsEqualTo(10000L);
        await Assert.That(character.Ability2).IsEqualTo(AbilityType.Magic);
        await Assert.That(character.Skills.Skills.ContainsKey(100)).IsTrue();
    }

    [Test]
    public async Task Reset_WithoutMoneyPreservesLearnedSkills()
    {
        var character = CreateCharacter(999);
        var result = character.Skills.Reset(AbilityType.Magic);
        await Assert.That(result).IsFalse();
        await Assert.That(character.Money).IsEqualTo(999L);
        await Assert.That(character.Skills.Skills.ContainsKey(100)).IsTrue();
    }

    [Test]
    public async Task Reset_ChargesPerLearnedEntryAndDoesNotNeedAnNpc()
    {
        var character = CreateCharacter(1000);
        var result = character.Skills.Reset(AbilityType.Magic);
        await Assert.That(result).IsTrue();
        await Assert.That(character.Money).IsEqualTo(0L);
        await Assert.That(character.Skills.Skills).IsEmpty();
    }

    [Test]
    public async Task ResetCost_CountsPassiveEntriesAndIgnoresSkillPointWeights()
    {
        var character = CreateCharacter(10000);
        character.Skills.PassiveBuffs.Add(200, new PassiveBuff
        {
            Id = 200, Template = new PassiveBuffTemplate { AbilityId = AbilityType.Magic }
        });
        await Assert.That(character.Skills.GetResetCost(AbilityType.Magic)).IsEqualTo(2000);
    }

    [Test]
    [Arguments(0)] [Arguments(1)] [Arguments(2)] [Arguments(3)] [Arguments(4)] [Arguments(5)] [Arguments(7)]
    public async Task SwapPacket_RejectsTruncatedAndTrailingBytes(int length)
    {
        var stream = new PacketStream(new byte[length]);
        await Assert.That(CSSwapAbilityPacket.TryReadRequest(stream, out _, out _, out _, out _)).IsFalse();
    }

    [Test]
    public async Task SwapPacket_ReadsExactNativeBody()
    {
        var stream = new PacketStream(new byte[] { 0x56, 0x34, 0x12, 7, 5, 1 });
        var valid = CSSwapAbilityPacket.TryReadRequest(stream, out var npc, out var oldAbility, out var newAbility, out var autoUse);
        await Assert.That(valid).IsTrue();
        await Assert.That(npc).IsEqualTo(0x123456U);
        await Assert.That(oldAbility).IsEqualTo(AbilityType.Magic);
        await Assert.That(newAbility).IsEqualTo(AbilityType.Death);
        await Assert.That(autoUse).IsTrue();
        await Assert.That(stream.Pos).IsEqualTo(stream.Count);
    }

    [Test]
    [Arguments(0)] [Arguments(1)] [Arguments(3)]
    public async Task ResetPacket_RejectsTruncatedAndTrailingBytes(int length)
    {
        await Assert.That(CSResetSkillsPacket.TryReadRequest(new PacketStream(new byte[length]), out _, out _)).IsFalse();
    }

    [Test]
    public async Task ResetPacket_ReadsExactNativeBodyAndRejectsInvalidBoolean()
    {
        var stream = new PacketStream(new byte[] { 7, 0 });
        await Assert.That(CSResetSkillsPacket.TryReadRequest(stream, out var ability, out var autoUse)).IsTrue();
        await Assert.That(ability).IsEqualTo(AbilityType.Magic);
        await Assert.That(autoUse).IsFalse();
        await Assert.That(stream.Pos).IsEqualTo(stream.Count);
        await Assert.That(CSResetSkillsPacket.TryReadRequest(new PacketStream(new byte[] { 7, 2 }), out _, out _)).IsFalse();
        await Assert.That(CSSwapAbilityPacket.TryReadRequest(new PacketStream(new byte[] { 1, 0, 0, 7, 5, 2 }), out _, out _, out _, out _)).IsFalse();
    }

    private static CharacterMock CreateCharacter(long money)
    {
        var character = new CharacterMock
        {
            Money = money, Level = 50, Hp = 100,
            Ability1 = AbilityType.Fight, Ability2 = AbilityType.Magic, Ability3 = AbilityType.Love
        };
        character.Skills = new CharacterSkills(character);
        character.Abilities = new CharacterAbilities(character);
        character.Skills.Skills.Add(100, new Skill
        {
            Id = 100, Template = new SkillTemplate { Id = 100, AbilityId = AbilityType.Magic, SkillPoints = 3 }
        });
        return character;
    }
}
