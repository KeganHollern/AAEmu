
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

public class UnitCooldownsTests
{
    [Test]
    public async Task AddCooldown_ShouldAddCooldown_WhenSkillNotExists()
    {
        // Arrange
        var cooldowns = new UnitCooldowns();
        var skillId = 100u;
        var duration = 5000u;

        // Act
        cooldowns.AddCooldown(skillId, duration);

        // Assert
        await Assert.That(cooldowns.Contains(skillId)).IsTrue();
    }

    [Test]
    public async Task AddCooldown_ShouldNotDuplicate_WhenSkillAlreadyExists()
    {
        // Arrange
        var cooldowns = new UnitCooldowns();
        var skillId = 100u;
        var duration1 = 5000u;
        var duration2 = 10000u;

        // Act
        cooldowns.AddCooldown(skillId, duration1);
        cooldowns.AddCooldown(skillId, duration2);

        // Assert
        await Assert.That(cooldowns.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CheckCooldown_ShouldReturnFalse_WhenSkillNotExists()
    {
        // Arrange
        var cooldowns = new UnitCooldowns();
        var skillId = 100u;

        // Act
        var result = cooldowns.CheckCooldown(skillId);

        // Assert
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task CheckCooldown_ShouldReturnTrue_WhenCooldownIsActive()
    {
        // Arrange
        var cooldowns = new UnitCooldowns();
        var skillId = 100u;
        var duration = 60000u; // 60 seconds

        cooldowns.AddCooldown(skillId, duration);

        // Act
        var result = cooldowns.CheckCooldown(skillId);

        // Assert
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task CheckCooldown_ShouldReturnFalseAndRemove_WhenCooldownExpired()
    {
        // Arrange
        var cooldowns = new UnitCooldowns();
        var skillId = 100u;

        cooldowns.AddCooldown(skillId, 0);

        // Act
        var result = cooldowns.CheckCooldown(skillId);

        // Assert
        await Assert.That(result).IsFalse();
        await Assert.That(cooldowns.Contains(skillId)).IsFalse();
    }

    [Test]
    public async Task RemoveCooldown_ShouldRemoveSkill_WhenExists()
    {
        // Arrange
        var cooldowns = new UnitCooldowns();
        var skillId = 100u;
        cooldowns.AddCooldown(skillId, 60000);

        // Act
        cooldowns.RemoveCooldown(skillId);

        // Assert
        await Assert.That(cooldowns.Contains(skillId)).IsFalse();
    }

    [Test]
    public void RemoveCooldown_ShouldNotThrow_WhenSkillNotExists()
    {
        // Arrange
        var cooldowns = new UnitCooldowns();
        var skillId = 100u;

        // Act & Assert - should not throw
        cooldowns.RemoveCooldown(skillId);
    }

    [Test]
    [Arguments(0u)]
    [Arguments(1u)]
    [Arguments(100u)]
    [Arguments(999999u)]
    public async Task AddCooldown_ShouldAcceptVariousSkillIds(uint skillId)
    {
        // Arrange
        var cooldowns = new UnitCooldowns();
        var duration = 5000u;

        // Act
        cooldowns.AddCooldown(skillId, duration);

        // Assert
        await Assert.That(cooldowns.Contains(skillId)).IsTrue();
    }

    [Test]
    [Arguments(0u)]
    [Arguments(100u)]
    [Arguments(60000u)]
    [Arguments(uint.MaxValue)]
    public async Task CheckCooldown_ShouldHandleVariousDurations(uint duration)
    {
        // Arrange
        var cooldowns = new UnitCooldowns();
        var skillId = 100u;

        if (duration > 250) // Only add if it would be considered "active"
        {
            cooldowns.AddCooldown(skillId, duration);
            var result = cooldowns.CheckCooldown(skillId);
            await Assert.That(result).IsTrue();
        }
        else
        {
            cooldowns.AddCooldown(skillId, duration);
            var result = cooldowns.CheckCooldown(skillId);
            await Assert.That(result).IsFalse();
        }
    }

    [Test]
    public async Task GetActiveSnapshots_ShouldReturnStableWireValues()
    {
        var cooldowns = new UnitCooldowns();
        cooldowns.AddCooldown(200, 60000);
        cooldowns.AddCooldown(100, 30000);

        var snapshots = cooldowns.GetActiveSnapshots(150);

        await Assert.That(snapshots.Count).IsEqualTo(2);
        await Assert.That(snapshots[0].SkillId).IsEqualTo(100u);
        await Assert.That(snapshots[0].Duration).IsEqualTo(30000u);
        await Assert.That(snapshots[0].Remaining).IsGreaterThan(0u).And.IsLessThanOrEqualTo(30000u);
        await Assert.That(snapshots[1].SkillId).IsEqualTo(200u);
        await Assert.That(snapshots[1].Duration).IsEqualTo(60000u);
        await Assert.That(snapshots[1].Remaining).IsGreaterThan(0u).And.IsLessThanOrEqualTo(60000u);
    }

    [Test]
    public async Task GetActiveSnapshots_ShouldRemoveExpiredCooldowns()
    {
        var cooldowns = new UnitCooldowns();
        cooldowns.AddCooldown(100, 0);

        var snapshots = cooldowns.GetActiveSnapshots(150);

        await Assert.That(snapshots).IsEmpty();
        await Assert.That(cooldowns.Contains(100)).IsFalse();
    }

    [Test]
    public async Task GetActiveSnapshots_ShouldLimitEntryCount()
    {
        var cooldowns = new UnitCooldowns();
        for (uint skillId = 0; skillId < 200; skillId++)
            cooldowns.AddCooldown(skillId, 60000);

        var snapshots = cooldowns.GetActiveSnapshots(150);

        await Assert.That(snapshots.Count).IsEqualTo(150);
        await Assert.That(snapshots[0].SkillId).IsEqualTo(0u);
        await Assert.That(snapshots[^1].SkillId).IsEqualTo(149u);
    }
}
