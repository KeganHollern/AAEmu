using System.Numerics;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.InstantGame.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Movements;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Game.Models.Game.Units;
using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers;

public sealed partial class ArenaMatchmakingTests
{
    private readonly Dictionary<uint, TransferVisibilityTests.RecordingSession> _transferSessions = [];
    private readonly WorldConfig _previousWorldConfig = AppConfiguration.Instance.World;

    [After(Test)]
    public void RestoreMovementWorldConfig() => AppConfiguration.Instance.World = _previousWorldConfig;

    [Test]
    public async Task GladiatorTransfer_MovesBothRegisteredPlayersAndRestoresTheirMainWorldMovement()
    {
        var (main, arena, first, second) = CreateTransferMatch();
        var match = first.CurrentInstantGame;
        foreach (var player in new[] { first, second })
        {
            match.PlayerInviteResponse(player, true, 1);
            await AssertMembership(player, arena, main);
            await Assert.That(player.Region).IsNull();
            var before = player.Transform.World.Position;
            Move(player, before + Vector3.UnitX);
            await Assert.That(player.Transform.World.Position).IsEqualTo(before);
            CompleteLoad(player);
            match.OnEnterWorld(player, 1);
        }
        var observer = _transferSessions[first.ObjId];
        var creates = Messages(observer, SCOffsets.SCUnitStatePacket).Count();
        new CSNotifyInGameCompletedPacket { Connection = second.Connection }.Read(new PacketStream());
        await Assert.That(Messages(observer, SCOffsets.SCUnitStatePacket).Count()).IsEqualTo(creates);
        await Assert.That(WorldManager.GetAround<Character>(first).Count(p => p == second)).IsEqualTo(1);
        await Assert.That(WorldManager.GetAround<Character>(second)).Contains(first);

        foreach (var player in new[] { first, second })
        {
            var opponent = player == first ? second : first;
            var target = new PacketStream().WriteBc(opponent.ObjId);
            target.Rollback();
            new CSChangeTargetPacket { Connection = player.Connection }.Read(target);
            await Assert.That(player.CurrentTarget).IsSameReferenceAs(opponent);
            var peerSession = _transferSessions[opponent.ObjId];
            var moves = Messages(peerSession, SCOffsets.SCOneUnitMovementPacket).Count();
            var destination = player.Transform.World.Position + Vector3.UnitX;
            Move(player, destination);
            await Assert.That(Messages(peerSession, SCOffsets.SCOneUnitMovementPacket).Count()).IsEqualTo(moves + 1);
            await Assert.That(Vector3.Distance(player.Transform.World.Position, destination)).IsLessThan(0.001f);
        }

        // Both clients complete the ordinary return.
        foreach (var player in new[] { first, second })
        {
            match.LeaveInstantGame(player);
            await AssertMembership(player, main, arena);
            CompleteLoad(player);
            var destination = player.Transform.World.Position + Vector3.UnitX;
            Move(player, destination);
            await Assert.That(Vector3.Distance(player.Transform.World.Position, destination)).IsLessThan(0.001f);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GladiatorTransfer_DisconnectRemovesOldActorBeforeReplacementSpawns(bool finishLoading)
    {
        var (main, arena, first, second) = CreateTransferMatch();
        var match = first.CurrentInstantGame;
        var original = first.Transform.World.Position;
        match.PlayerInviteResponse(first, true, 1);
        if (finishLoading)
        {
            CompleteLoad(first);
            match.OnEnterWorld(first, 1);
        }

        // Use the world-removal operations from SaveAndRemoveFromWorld and the normal new-session Spawn.
        var session = first.Connection;
        session.CompleteDisconnect(() => { }, () =>
        {
            first.Delete();
            _worlds.TryRemoveCharacter(first);
        }, () => { });
        await Assert.That(session.ActiveChar).IsNull();
        await Assert.That(session.IsClosed).IsTrue();
        await Assert.That(first.CurrentInstantGame).IsNull();
        await Assert.That(main.GetBaseUnit(first.ObjId)).IsNull();
        await Assert.That(arena.GetBaseUnit(first.ObjId)).IsNull();
        await Assert.That(_worlds.GetCharacterByObjId(first.ObjId)).IsNull();
        await Assert.That(first.MainWorldPosition.World.Position).IsEqualTo(original);
        var replacement = SpawnParticipant(first.Id, first.ObjId, original.X, main);
        await AssertMembership(replacement, main, arena);
        await Assert.That(main.GetCharacterCount()).IsEqualTo(2);
        await Assert.That(arena.GetCharacterCount()).IsEqualTo(0);
        await Assert.That(WorldManager.GetAround<Character>(second)).Contains(replacement);
        var afterLogin = replacement.Transform.World.Position + Vector3.UnitX;
        Move(replacement, afterLogin);
        await Assert.That(Vector3.Distance(replacement.Transform.World.Position, afterLogin)).IsLessThan(0.001f);
    }

    private (WorldInstance Main, WorldInstance Arena, Character First, Character Second) CreateTransferMatch()
    {
        AppConfiguration.Instance.World = new WorldConfig();
        var main = new WorldInstance(new WorldTemplate { Id = 1, Name = "main_world" }, 0, true, WorldManager.DefaultInstanceId);
        GC.SuppressFinalize(main);
        _instances[main.Id] = main;
        var arena = _instances[42];
        foreach (var world in new[] { main, arena })
        {
            world.Template.CellX = world.Template.CellY = 1;
            world.Template.ZoneKeyByRegions = new uint[16, 16];
            world.Regions = new Region[16, 16];
            world.MateManager = new MateManager(world);
            world.SlaveManager = new SlaveManager(world);
        }
        var items = new ItemManager(null, null, null, null, null, null);
        Set(items, "_config", new ItemConfig());
        Install(items);
        Install(new PremiumGameData());
        Install(new PermissionManager(Mock.Of<IAccountManager>().Object));
        Install(new SusManager(_worlds));
        Install(new TimeManager(null, _worlds, TimeProvider.System, null));

        var field = BattlefieldGameData.Instance.GetBattlefield(11);
        field.Spawns.Corps1Spawn.X = 55;
        field.Spawns.Corps2Spawn.X = 58;
        var first = SpawnParticipant(1, 1001, 8, main);
        var second = SpawnParticipant(2, 1002, 11, main);
        foreach (var player in new[] { first, second })
            _queue.ApplyToBattlefield(11, InstantCorps.Any, player);
        _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
        Set(first.CurrentInstantGame, "_clock", new FakeTimeProvider());
        return (main, arena, first, second);
    }

    private Character SpawnParticipant(uint id, uint objId, float x, WorldInstance main)
    {
        var player = new Character(new UnitCustomModelParams())
        {
            Id = id,
            AccountId = id,
            Hp = 100,
            ObjId = objId,
            Name = $"Player{id}",
            Level = 50,
            ParentWorld = main,
            Faction = new() { Id = id == 1 ? (FactionsEnum)159 : (FactionsEnum)160 },
            VisualOptions = new() { Stp = new byte[6] }
        };
        var buffs = Mock.Of<IBuffs>();
        buffs.CheckBuff(8000012).Returns(true);
        player.Buffs = buffs.Object;
        player.OriginFaction = player.Faction;
        var containers = Enum.GetValues<SlotType>().Where(type => type != SlotType.EquipmentMate)
            .Select((type, index) => new ItemContainer(id, type, false, player) { ContainerId = (ulong)index + 1 });
        Set(ItemManager.Instance, "_allPersistentContainers", containers.ToDictionary(c => c.ContainerId));
        player.Inventory = new Inventory(player);
        player.Equipment.ContainerSize = 28;
        player.Skills = new CharacterSkills(player);
        player.Abilities = new CharacterAbilities(player);
        player.Appellations = new CharacterAppellations(player);
        var session = _transferSessions[objId] = new TransferVisibilityTests.RecordingSession();
        var connection = player.Connection = new GameConnection(session);
        connection.TryAuthenticate(id);
        connection.TrySelectCharacter(player);
        _worlds.TryAddCharacter(player);
        connection.TryAdvanceWorldEntry(GameState.CharacterSelected, GameState.EnteringWorld);
        connection.TryAdvanceWorldEntry(GameState.EnteringWorld, GameState.World);
        connection.TryCompleteWorldEntry();
        player.Transform.Local.Position = new(x, 25, 30);
        player.Spawn();
        return player;
    }

    private static async Task AssertMembership(Character player, WorldInstance current, WorldInstance previous)
    {
        await Assert.That(player.ParentWorld).IsSameReferenceAs(current);
        await Assert.That(current.GetBaseUnit(player.ObjId)).IsSameReferenceAs(player);
        await Assert.That(current.GetGameObject(player.ObjId)).IsSameReferenceAs(player);
        await Assert.That(current.GetUnit(player.ObjId)).IsSameReferenceAs(player);
        await Assert.That(current.GetCharacterByObjId(player.ObjId)).IsSameReferenceAs(player);
        await Assert.That(previous.GetGameObject(player.ObjId)).IsNull();
        await Assert.That(previous.GetBaseUnit(player.ObjId)).IsNull();
        await Assert.That(previous.GetUnit(player.ObjId)).IsNull();
        await Assert.That(previous.GetCharacterByObjId(player.ObjId)).IsNull();
        await Assert.That(WorldManager.Instance.GetCharacterByObjId(player.ObjId)).IsSameReferenceAs(player);
    }

    private static void CompleteLoad(Character player)
    {
        new CSInstanceLoadedPacket { Connection = player.Connection }.Read(new PacketStream());
        new CSNotifyInGamePacket { Connection = player.Connection }.Execute();
        new CSNotifyInGameCompletedPacket { Connection = player.Connection }.Read(new PacketStream());
    }

    private static void Move(Character player, Vector3 position)
    {
        var data = new PacketStream().WriteBc(player.ObjId).Write((byte)MoveTypeEnum.Unit);
        new UnitMoveType { X = position.X, Y = position.Y, Z = position.Z, DeltaMovement = new sbyte[3] }.Write(data);
        data.Rollback();
        var movement = new CSMoveUnitPacket { Connection = player.Connection };
        movement.Read(data);
        movement.Execute();
    }
}
