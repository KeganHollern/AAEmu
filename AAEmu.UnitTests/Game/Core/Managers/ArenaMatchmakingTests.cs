using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.InstantGame;
using AAEmu.Game.Models.Game.InstantGame.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Xml;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public sealed class ArenaMatchmakingTests
{
    private readonly WorldManager _worlds = new(null, null, null, null, null);
    private readonly InstantGameManager _queue = new();
    private readonly List<(FieldInfo Field, object Original)> _originals = [];

    [Before(Test)]
    public void SetUp()
    {
        var fields = new BattlefieldGameData();
        var camp = new Battlefield
        {
            Id = 7, ZoneKey = 200,
            RuleSet = new() { Id = 22, CorpsSize = 5, Corps1FactionId = 159, Corps2FactionId = 160 }
        };
        var duel = new Battlefield
        {
            Id = 11, ZoneKey = 302,
            RuleSet = new() { Id = 28, CorpsSize = 1, Corps1FactionId = 159, Corps2FactionId = 160 }
        };
        Set(fields, "_battlefields", new Dictionary<uint, Battlefield> { [camp.Id] = camp, [duel.Id] = duel });
        Install(fields);

        // Supply an existing world so normal invitations do not need to start physics in this test.
        var template = new WorldTemplate { Id = 0, Name = "arena", ZoneKeys = [camp.ZoneKey, duel.ZoneKey] };
        template.XmlWorld.Zones = new ConcurrentDictionary<uint, XmlWorldZone>(
            template.ZoneKeys.ToDictionary(key => key, _ => new XmlWorldZone()));
        var world = new WorldInstance(template, 0, true, 42);
        GC.SuppressFinalize(world);
        _worlds.WorldTemplates.Add(template.Name, template);
        Set(_worlds, "<WorldNames>k__BackingField", new List<string> { template.Name });
        Set(_worlds, "_worldIdByZoneKey", template.ZoneKeys.ToDictionary(key => key, _ => template.Id));
        Set(_worlds, "_worlds", new ConcurrentDictionary<uint, WorldInstance>(new Dictionary<uint, WorldInstance> { [world.Id] = world }));
        Install(_worlds);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, original) in _originals)
            field.SetValue(null, original);
    }

    [Test]
    public async Task APlayerWhoLeavesTheWorldBeforeTheTickDoesNotFillATeamSlot()
    {
        var first = new CharacterMock { Id = 1, ObjId = 10, Buffs = Mock.Of<IBuffs>().Object };
        var applicant = new CharacterMock { Id = 2, ObjId = 20, Buffs = Mock.Of<IBuffs>().Object };
        var returned = new CharacterMock { Id = first.Id, ObjId = 30, Buffs = Mock.Of<IBuffs>().Object };
        _worlds.TryAddCharacter(first);
        _worlds.TryAddCharacter(applicant);
        _queue.ApplyToBattlefield((uint)InstantGameType.Gladiator, InstantCorps.Any, first);
        _queue.ApplyToBattlefield((uint)InstantGameType.Gladiator, InstantCorps.Any, applicant);
        _worlds.TryRemoveCharacter(first.ObjId);
        _worlds.TryAddCharacter(returned);

        _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
        await Assert.That(applicant.CurrentInstantGame).IsNull();
        await Assert.That(returned.CurrentInstantGame).IsNull();

        _queue.ApplyToBattlefield((uint)InstantGameType.Gladiator, InstantCorps.Any, returned);
        _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
        await Assert.That(applicant.CurrentInstantGame).IsNotNull();
        await Assert.That(applicant.CurrentInstantGame.IsFull).IsTrue();
        await Assert.That(returned.CurrentInstantGame).IsSameReferenceAs(applicant.CurrentInstantGame);
    }

    [Test]
    [Arguments(InstantGameType.DrillCamp, 10)]
    [Arguments(InstantGameType.Gladiator, 2)]
    public async Task QueueWaitsForBothTeamsAndRefillsAVacancy(InstantGameType mode, int playersNeeded)
    {
        var players = Enumerable.Range(1, playersNeeded + 1).Select(id => new CharacterMock
        {
            Id = (uint)id, ObjId = (uint)id, Name = $"Player{id}", Buffs = Mock.Of<IBuffs>().Object
        }).ToArray();
        foreach (var player in players)
            _worlds.TryAddCharacter(player);

        foreach (var player in players.Take(playersNeeded - 1))
        {
            _queue.ApplyToBattlefield((uint)mode, InstantCorps.Any, player);
            _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
            await Assert.That(player.CurrentInstantGame).IsNull();
        }

        _queue.ApplyToBattlefield((uint)mode, InstantCorps.Any, players[playersNeeded - 1]);
        _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
        var match = players[0].CurrentInstantGame;
        await Assert.That(match).IsNotNull();
        await Assert.That(match.IsFull).IsTrue();
        await Assert.That(players.Take(playersNeeded).All(player => player.CurrentInstantGame == match)).IsTrue();

        _queue.WithdrawFromBattlefield(players[0]);
        _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
        await Assert.That(players[0].CurrentInstantGame).IsNull();
        await Assert.That(match.IsFull).IsFalse();
        _queue.ApplyToBattlefield((uint)mode, InstantCorps.Any, players[^1]);
        _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
        await Assert.That(players[^1].CurrentInstantGame).IsSameReferenceAs(match);
        await Assert.That(match.IsFull).IsTrue();
    }

    private static void Set<T>(T target, string name, object value) =>
        typeof(T).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private void Install<T>(T manager) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _originals.Add((field, field.GetValue(null)));
        field.SetValue(null, manager);
    }
}
