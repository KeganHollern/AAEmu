using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

[NotInParallel]
public sealed class SharedCooldownTests
{
    private readonly ManualTimeProvider _clock = new();
    private object _previousSkills;

    [Before(Test)]
    public void SetUp()
    {
        var field = typeof(Singleton<SkillManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousSkills = field.GetValue(null);
        var skills = new SkillManager(null, null);
        typeof(SkillManager).GetField("_skillTags", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(skills, new Dictionary<uint, List<uint>> { [99] = [30, 144] });
        typeof(SkillManager).GetField("_skills", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(skills, new Dictionary<uint, SkillTemplate>
            {
                [11718] = new() { Id = 11718, CooldownTagId = 30 },
                [99] = new() { Id = 99, CooldownTagId = 900 }
            });
        field.SetValue(null, skills);
    }

    [After(Test)]
    public void TearDown()
    {
        typeof(Singleton<SkillManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, _previousSkills);
    }

    [Test]
    public async Task Potions_DifferentGradesShareOnlyTheTagBucket()
    {
        var cooldowns = new UnitCooldowns(_clock);
        cooldowns.AddCooldown(11715, 90000, 30);
        await Assert.That(cooldowns.CheckCooldown(new SkillTemplate { Id = 11718, CooldownTagId = 30 })).IsTrue();
        await Assert.That(cooldowns.CheckCooldown(new SkillTemplate { Id = 44, CooldownTagId = 144 })).IsFalse();
        var snapshot = cooldowns.GetActiveBuckets(150);
        await Assert.That(snapshot.Skills).IsEmpty();
        await Assert.That(snapshot.Tags.Single()).IsEqualTo(new UnitCooldowns.CooldownSnapshot(30, 90000, 90000));
    }

    [Test]
    public async Task AuthoredSkillTag_RejectsEvenWhenTheSkillStartsADifferentCooldown()
    {
        var cooldowns = new UnitCooldowns(_clock);
        cooldowns.AddCooldown(11715, 90000, 30);
        await Assert.That(cooldowns.CheckCooldown(new SkillTemplate { Id = 99, CooldownTagId = 900 })).IsTrue();
    }

    [Test]
    public async Task IdOnlyCaller_ChecksTheAuthoredCooldownAndSkillTags()
    {
        var cooldowns = new UnitCooldowns(_clock);
        cooldowns.AddCooldown(11715, 90000, 30);
        await Assert.That(cooldowns.CheckCooldown(11718)).IsTrue();
        await Assert.That(cooldowns.CheckCooldown(99)).IsTrue();
        await Assert.That(cooldowns.CheckCooldown(12345)).IsFalse();
        cooldowns.RemoveTagCooldown(30);
        await Assert.That(cooldowns.CheckCooldown(11718)).IsFalse();
    }

    [Test]
    public async Task SharedCooldown_ExpiresAtTheBoundedServerTolerance()
    {
        var cooldowns = new UnitCooldowns(_clock);
        cooldowns.AddCooldown(11715, 90000, 30);
        _clock.Advance(89949);
        await Assert.That(cooldowns.CheckTagCooldown(30)).IsTrue();
        _clock.Advance(1);
        await Assert.That(cooldowns.CheckTagCooldown(30)).IsFalse();
        cooldowns.AddCooldown(11718, 20000, 30);
        await Assert.That(cooldowns.GetActiveBuckets(150).Tags.Single().Duration).IsEqualTo(20000u);
    }

    [Test]
    public async Task ShorterRestart_DoesNotReduceTheActiveTagDuration()
    {
        var cooldowns = new UnitCooldowns(_clock);
        cooldowns.AddCooldown(11715, 90000, 30);
        _clock.Advance(10000);
        cooldowns.AddCooldown(11718, 20000, 30);
        await Assert.That(cooldowns.GetActiveBuckets(150).Tags.Single())
            .IsEqualTo(new UnitCooldowns.CooldownSnapshot(30, 90000, 80000));
        cooldowns.AddCooldown(11718, 120000, 30);
        await Assert.That(cooldowns.GetActiveBuckets(150).Tags.Single())
            .IsEqualTo(new UnitCooldowns.CooldownSnapshot(30, 120000, 120000));
    }

    [Test]
    public async Task ResetSkillAndTag_AreIndependent()
    {
        var cooldowns = new UnitCooldowns(_clock);
        cooldowns.AddCooldown(30, 90000);
        cooldowns.AddCooldown(11715, 90000, 30);
        cooldowns.RemoveCooldown(30);
        await Assert.That(cooldowns.CheckTagCooldown(30)).IsTrue();
        cooldowns.AddCooldown(30, 90000);
        cooldowns.RemoveTagCooldown(30);
        await Assert.That(cooldowns.CheckCooldown(30)).IsTrue();
        await Assert.That(cooldowns.CheckTagCooldown(30)).IsFalse();
    }

    [Test]
    public async Task ConcurrentAddResetAndSnapshot_PreservesTheFinalSharedTimer()
    {
        var cooldowns = new UnitCooldowns(_clock);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
        {
            for (var iteration = 0; iteration < 100; iteration++)
            {
                cooldowns.AddCooldown((uint)index, (uint)(1000 + index), 30);
                cooldowns.GetActiveBuckets(150);
                cooldowns.RemoveTagCooldown(30);
            }
        })));
        cooldowns.AddCooldown(11715, 90000, 30);
        await Assert.That(cooldowns.GetActiveBuckets(150).Tags.Single())
            .IsEqualTo(new UnitCooldowns.CooldownSnapshot(30, 90000, 90000));
    }

    internal sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(int milliseconds) => _utcNow = _utcNow.AddMilliseconds(milliseconds);
    }
}
