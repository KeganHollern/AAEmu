using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

[NotInParallel]
public sealed class BuffBreakerTests
{
    private readonly Dictionary<FieldInfo, object> _previous = [];
    private BaseUnit _owner;

    [Before(Test)]
    public void SetUp()
    {
        var skills = new SkillManager(null, null);
        BuffBreakerTestData.Configure(skills);
        SetInstance(skills);
        SetInstance(BuffBreakerTestData.Load());
        _owner = new BaseUnit { ObjId = 42 };
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, value) in _previous)
            field.SetValue(null, value);
    }

    [Test]
    public async Task AddFear_RemovesGliderAndSongOnceBeforeStart_KeepsOtherBuffs()
    {
        var glider = Add(2098);
        var song = Add(656);
        var other = Add(9000);
        var exits = 0;
        glider.Events.OnDispelled += (_, _) => exits++;
        song.Events.OnDispelled += (_, _) => exits++;
        var fear = NewBuff(156);
        var boundBuffPresentAtStart = true;
        fear.Events.OnBuffStarted += (_, _) => boundBuffPresentAtStart =
            _owner.Buffs.CheckBuff(2098) || _owner.Buffs.CheckBuff(656);

        _owner.Buffs.AddBuff(fear);

        await Assert.That(exits).IsEqualTo(2);
        await Assert.That(boundBuffPresentAtStart).IsFalse();
        await Assert.That(glider.State).IsEqualTo(EffectState.Finished);
        await Assert.That(song.State).IsEqualTo(EffectState.Finished);
        await Assert.That(_owner.Buffs.CheckBuff(156)).IsTrue();
        await Assert.That(other.InUse).IsTrue();
    }

    [Test]
    public async Task AddBoundBuff_DoesNotRemoveTheEarlierBreaker()
    {
        Add(156);
        Add(2098);

        await Assert.That(_owner.Buffs.CheckBuff(156)).IsTrue();
        await Assert.That(_owner.Buffs.CheckBuff(2098)).IsTrue();
    }

    [Test]
    public async Task ImmuneFear_DoesNotBreakTheGlider()
    {
        Add(2098);
        Add(9001);
        new BuffEffect { Buff = SkillManager.Instance.GetBuffTemplate(156), Chance = 100 }
            .Apply(_owner, new SkillCasterUnit(42), _owner, null, null, new EffectSource(), null, DateTime.UtcNow);

        await Assert.That(_owner.Buffs.CheckBuff(2098)).IsTrue();
        await Assert.That(_owner.Buffs.CheckBuff(156)).IsFalse();
    }

    [Test]
    public async Task AddFear_RemovesEveryStackFromDifferentCasters()
    {
        Add(2098);
        var second = NewBuff(2098);
        second.SkillCaster = new SkillCasterUnit(99);
        _owner.Buffs.AddBuff(second);

        Add(156);

        await Assert.That(_owner.Buffs.GetBuffCountById(2098)).IsEqualTo(0);
    }

    [Test]
    public async Task RejectedShorterRefresh_DoesNotBreakTheGlider()
    {
        var fear = Add(156);
        fear.Duration = 10000;
        fear.StartTime = DateTime.UtcNow;
        Add(2098);

        Add(156);

        await Assert.That(_owner.Buffs.CheckBuff(2098)).IsTrue();
    }

    [Test]
    public async Task LaborPreview_RemovesBoundBuffWithoutLiveCallbacks_RecordsItsDeletion()
    {
        var glider = Add(2098);
        var exits = 0;
        glider.Events.OnDispelled += (_, _) => exits++;
        var preview = ((AAEmu.Game.Models.Game.Units.Buffs)_owner.Buffs).CreateLaborPreview();

        preview.AddBuff(NewBuff(156));

        await Assert.That(preview.CheckBuff(2098)).IsFalse();
        await Assert.That(preview.CheckBuff(156)).IsTrue();
        await Assert.That(_owner.Buffs.CheckBuff(2098)).IsTrue();
        await Assert.That(exits).IsEqualTo(0);
        var changed = (HashSet<uint>)typeof(AAEmu.Game.Models.Game.Units.Buffs)
            .GetField("_laborChangedBuffIds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview)!;
        await Assert.That(changed.SetEquals([2098u, 156u])).IsTrue();
    }

    [Test]
    public async Task Loader_UsesBoundBuffToTagDirection_AndClearsOnReload()
    {
        var data = BuffGameData.Instance;
        await Assert.That(data.IsBrokenBy(2098, [12u])).IsTrue();
        await Assert.That(data.IsBrokenBy(656, [107u])).IsTrue();
        await Assert.That(data.IsBrokenBy(156, [12u])).IsFalse();
        await Assert.That(data.IsBrokenBy(2098, [999u])).IsFalse();
        using var connection = BuffBreakerTestData.Connection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM buff_breakers";
        command.ExecuteNonQuery();
        data.Load(connection);
        await Assert.That(data.IsBrokenBy(2098, [12u])).IsFalse();
    }

    private Buff NewBuff(uint id) => new(_owner, _owner, new SkillCasterUnit(_owner.ObjId),
        SkillManager.Instance.GetBuffTemplate(id), null, DateTime.UtcNow);

    private Buff Add(uint id)
    {
        var buff = NewBuff(id);
        _owner.Buffs.AddBuff(buff);
        return buff;
    }

    private void SetInstance<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previous.Add(field, field.GetValue(null));
        field.SetValue(null, instance);
    }
}

internal static class BuffBreakerTestData
{
    internal static SqliteConnection Connection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE buff_modifiers (id INTEGER);
            CREATE TABLE buff_tolerances (id INTEGER);
            CREATE TABLE buff_tolerance_steps (id INTEGER);
            CREATE TABLE buff_breakers (buff_id INTEGER, buff_tag_id INTEGER);
            INSERT INTO buff_breakers VALUES (2098,12),(2098,107),(656,12),(656,107);
            """;
        command.ExecuteNonQuery();
        return connection;
    }

    internal static BuffGameData Load()
    {
        using var connection = Connection();
        var data = new BuffGameData();
        data.Load(connection);
        data.PostLoad();
        return data;
    }

    internal static void Configure(SkillManager skills)
    {
        // Real r208022 fear/glider/song identifiers. Durations are zero to keep these tests synchronous.
        var templates = new Dictionary<uint, BuffTemplate>
        {
            [156] = new() { Id = 156, StackRule = BuffStackRule.Refresh },
            [2098] = new() { Id = 2098, Gliding = true },
            [656] = new() { Id = 656 },
            [9000] = new() { Id = 9000 },
            [9001] = new() { Id = 9001, ImmuneBuffTagId = 999 }
        };
        Set("_buffs", templates);
        Set("_buffTags", new Dictionary<uint, List<uint>> { [156] = [12] });
        Set("_taggedBuffs", new Dictionary<uint, List<uint>> { [999] = [156] });
        Set("_skillModifiers", new Dictionary<uint, List<SkillModifier>>());
        Set("_buffTriggers", new Dictionary<uint, List<BuffTriggerTemplate>>());
        Set("_combatBuffs", new Dictionary<uint, List<CombatBuffTemplate>>());

        void Set(string name, object value) => typeof(SkillManager)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(skills, value);
    }
}
