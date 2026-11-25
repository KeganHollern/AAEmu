using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Movements;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;

using NLog;
using NLog.Config;
using NLog.Targets;

using ReturnPortal = AAEmu.Game.Models.Game.Portal;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

public sealed partial class ResurrectionRulesTests
{
    private static readonly Vector3 DeathSite = new(8, 2, 20);
    private static readonly Vector3 TempleSite = new(328, 2, 20);

    [Test]
    public async Task Temple_PlayerDiesAgainAtTheReturnPointAndRevivesInTheSameSession()
    {
        using var scene = new TempleScene(TempleSite);
        var player = scene.Player;
        var departure = scene.MakeCharacter(2, DeathSite);
        var arrival = scene.MakeCharacter(3, TempleSite);
        var initialGuard = scene.GuardAt(DeathSite);
        var templeGuard = scene.GuardAt(TempleSite);
        player.Transform.Local.Rotation = new Vector3(0, 0, 1.25f);
        player.ReduceCurrentHp(initialGuard, player.Hp);
        Temple(player);

        await Assert.That(player.Transform.World.Position).IsEqualTo(TempleSite);
        await Assert.That(player.Region).IsSameReferenceAs(arrival.Region);
        await Assert.That(player.Transform.World.Rotation.Z).IsEqualTo(1.25f);
        await Assert.That(scene.Records(player).ReturnHeadings.Single()).IsEqualTo(63f);
        await Assert.That(scene.Records(departure).Count(SCOffsets.SCCharacterResurrectedPacket)).IsEqualTo(1);
        await Assert.That(scene.Records(departure).Removed.SequenceEqual(new[] { player.ObjId })).IsTrue();
        await Assert.That(scene.Records(arrival).Created.SequenceEqual(new[] { player.ObjId })).IsTrue();
        await Assert.That(scene.Records(arrival).Points.Contains((player.ObjId, 10))).IsTrue();

        // The second guard deals ordinary damage before the player sends any movement.
        player.ReduceCurrentHp(templeGuard, player.Hp);
        await Assert.That(player.DeadCount).IsEqualTo((short)2);
        await Assert.That(player.RezWaitDuration).IsEqualTo(5000);
        await Assert.That(scene.Records(departure).Count(SCOffsets.SCUnitDeathPacket)).IsEqualTo(1);
        await Assert.That(scene.Records(arrival).Count(SCOffsets.SCUnitDeathPacket)).IsEqualTo(1);

        Temple(player);
        await Assert.That(player.IsDead).IsTrue();
        await Task.Delay(player.GetRemainingResurrectionWait(DateTime.UtcNow) + 15);
        Temple(player);
        await Assert.That(player.Hp).IsEqualTo(10);
        await Assert.That(scene.Records(player).LifeEvents.SequenceEqual(
            new (ushort, int)[] { (SCOffsets.SCUnitDeathPacket, 0), (SCOffsets.SCCharacterResurrectedPacket, 10),
                (SCOffsets.SCUnitDeathPacket, 0), (SCOffsets.SCCharacterResurrectedPacket, 10) })).IsTrue();
        await Assert.That(scene.Records(player).Waits.SequenceEqual(new[] { 0, 5000 })).IsTrue();
        await Assert.That(scene.Records(player).Destinations).Count().IsEqualTo(2);
        foreach (var sent in scene.Records(player).Destinations)
            await Assert.That(Vector3.Distance(sent, TempleSite)).IsLessThan(.001f);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Temple_FirstStepUsesTheReturnPointEvenAfterAnOutstandingPortal(bool pendingPortal)
    {
        using var scene = new TempleScene(TempleSite);
        var player = scene.Player;
        Move(player, DeathSite);
        scene.Movement.AnalyzePlayerDeltaMovement(player, 1);
        var death = DeathSite;
        if (pendingPortal)
        {
            var portal = new AAEmu.Game.Models.Game.Units.Portal
            {
                OwnerId = player.Id, ObjId = 44, TemplateId = 3891, Template = new NpcTemplate { Scale = 1 },
                ModelId = 308, Hp = 1, ParentWorld = scene.World,
                TeleportPosition = new Transform(null, null, DeathSite + Vector3.UnitX) { InstanceId = scene.World.Id }
            };
            portal.Transform.Local.SetPosition(DeathSite);
            player.Portals = new CharacterPortals(player);
            scene.World.AddObject(portal);
            PortalManager.UsePortal(player, portal.ObjId, false);
            await Assert.That(player.DisabledSetPosition).IsTrue();
            death = portal.TeleportPosition.World.Position;
            await Assert.That(player.Transform.World.Position).IsEqualTo(death);
        }

        player.ReduceCurrentHp(scene.GuardAt(death), player.Hp);
        Temple(player);
        await Assert.That(player.DisabledSetPosition).IsEqualTo(pendingPortal);
        if (pendingPortal)
        {
            await Assert.That(player.Transform.World.Position).IsEqualTo(TempleSite);
            var acknowledgement = new PacketStream().Write(Helpers.ConvertLongX(death.X)).Write(Helpers.ConvertLongY(death.Y))
                .Write(death.Z).Write(new byte[16]);
            new CSTeleportEndedPacket { Connection = player.Connection }.Read(acknowledgement);
            await Assert.That(player.DisabledSetPosition).IsFalse();
        }
        Move(player, TempleSite + Vector3.UnitX);
        scene.Movement.AnalyzePlayerDeltaMovement(player, 1);

        await Assert.That(Vector3.Distance(player.Transform.World.Position, TempleSite + Vector3.UnitX)).IsLessThan(.001f);
        await Assert.That(scene.Movement.PendingCount).IsZero();
        await Assert.That(scene.MovementWarnings.Logs).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Temple_LeavesTheOldSupportBeforeApplyingWorldCoordinates(bool sticky)
    {
        using var scene = new TempleScene(TempleSite);
        var player = scene.Player;
        var deck = new Doodad { ObjId = 42, ParentWorld = scene.World };
        deck.Transform.Local.SetPosition(DeathSite);
        if (sticky)
            player.Transform.StickyParent = deck.Transform;
        else
            player.Transform.Parent = deck.Transform;
        player.ReduceCurrentHp(scene.GuardAt(DeathSite), player.Hp);

        Temple(player);

        await Assert.That(player.Transform.World.Position).IsEqualTo(TempleSite);
        await Assert.That(player.Transform.Parent).IsNull();
        await Assert.That(player.Transform.StickyParent).IsNull();
        await Assert.That(deck.Transform.Children.Contains(player.Transform)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Temple_SameLocationAndInstanceRevivalKeepTheirExistingPlacement(bool inInstance)
    {
        using var scene = new TempleScene(DeathSite, inInstance ? 17u : WorldManager.DefaultInstanceId);
        var player = scene.Player;
        if (inInstance)
        {
            var priestess = scene.GuardAt(TempleSite);
            priestess.TemplateId = 502;
            scene.World.AddObject(priestess);
        }

        player.ReduceCurrentHp(scene.GuardAt(DeathSite), player.Hp);
        Temple(player);
        var destination = scene.Records(player).Destinations.Single();
        if (inInstance)
        {
            await Assert.That(destination.X - TempleSite.X).IsBetween(1f, 2f);
            await Assert.That(destination.Y - TempleSite.Y).IsBetween(1f, 2f);
        }
        else
            await Assert.That(Vector3.Distance(destination, DeathSite)).IsLessThan(.001f);

        await Assert.That(player.Transform.World.Position).IsEqualTo(DeathSite);
        await Assert.That(player.Hp).IsEqualTo(10);
    }

    private static void Temple(Character player) =>
        new CSResurrectCharacterPacket { Connection = player.Connection }.Read(new PacketStream(new byte[] { 0 }));

    private static void Move(Character player, Vector3 position)
    {
        var stream = new PacketStream().WriteBc(player.ObjId).Write((byte)MoveTypeEnum.Unit);
        new UnitMoveType { X = position.X, Y = position.Y, Z = position.Z, DeltaMovement = new sbyte[3] }.Write(stream);
        stream.Rollback();
        var move = new CSMoveUnitPacket { Connection = player.Connection };
        move.Read(stream);
        move.Execute();
    }

    private sealed class TempleCharacter() : Character(new UnitCustomModelParams())
    {
        public override int MaxHp => 100;
        public override int MaxMp => 100;
    }

    private sealed class TempleScene : IDisposable
    {
        private readonly List<(FieldInfo Field, object Previous)> _singletons = [];
        private readonly Dictionary<Character, TempleSession> _sessions = [];
        private readonly WorldConfig _oldWorld = AppConfiguration.Instance.World;
        private readonly ItemManager _items = new(null, null, null, null, null, null);
        private readonly LoggingConfiguration _previousLogging = LogManager.Configuration;
        public MemoryTarget MovementWarnings { get; } = new() { Layout = "${message}" };
        public WorldInstance World { get; }
        public Character Player { get; }
        public SusManager Movement { get; }

        public TempleScene(Vector3 returnPoint, uint instance = 0)
        {
            var logging = new LoggingConfiguration();
            logging.AddRule(LogLevel.Warn, LogLevel.Fatal, MovementWarnings, typeof(MovementValidation).FullName);
            LogManager.Configuration = logging;
            World = new WorldInstance(new WorldTemplate
                { Name = "temple", CellX = 1, CellY = 1, ZoneKeyByRegions = new uint[16, 16] }, 0, true, instance)
                { Regions = new Region[16, 16] };
            GC.SuppressFinalize(World);
            World.MateManager = new MateManager(World);
            World.SlaveManager = new SlaveManager(World);
            var worlds = new WorldManager(null, null, null, null, null);
            SetField(worlds, "_worlds", new ConcurrentDictionary<uint, WorldInstance> { [instance] = World });
            Install(worlds);
            Movement = new SusManager(worlds);
            Install(Movement);
            AppConfiguration.Instance.World = new WorldConfig { MinimumExpLossLevel = 2 };
            Install(new ExperienceManager());
            Install(LoadData());
            Install(new PriestBuffGameData());
            Install(new PremiumGameData());
            Install(new DuelManager());
            Install(new TrialManager());
            var zones = new ZoneManager(null, null);
            SetField(zones, "_zones", new Dictionary<uint, Zone>());
            Install(zones);
            var skills = new SkillManager(null, null);
            SetField(skills, "_taggedBuffs", new Dictionary<uint, List<uint>>());
            SetField(skills, "_buffs", new Dictionary<uint, BuffTemplate>());
            Install(skills);
            SetField(_items, "_config", new ItemConfig());
            Install(_items);
            var portalZones = Mock.Of<IZoneManager>();
            portalZones.GetZoneByKey(10u).Returns(new Zone { Id = 10 });
            var portals = new PortalManager(null, worlds, portalZones.Object, null, null, null);
            SetField(portals, "_respawns", new Dictionary<uint, ReturnPortal> { [1] = new ReturnPortal
                { Id = 1, X = returnPoint.X, Y = returnPoint.Y, Z = returnPoint.Z, ZRot = 63, ZoneId = 10 } });
            SetField(portals, "_districtReturnPoints", new Dictionary<uint, DistrictReturnPoints>());
            Install(new PermissionManager(null));
            Install(portals);
            Player = MakeCharacter(1, DeathSite);
        }

        public TempleSession Records(Character player) => _sessions[player];

        public TempleCharacter MakeCharacter(uint id, Vector3 position)
        {
            var player = new TempleCharacter { Id = id, ObjId = 91000 + id, Hp = 100, Level = 2, Name = $"Temple{id}",
                ParentWorld = World, IsVisible = true, Faction = new(), VisualOptions = new() { Stp = new byte[6] } };
            CreateInventory(player, _items);
            player.Equipment.ContainerSize = 28;
            player.Appellations = new CharacterAppellations(player);
            player.Skills = new CharacterSkills(player);
            player.Abilities = new CharacterAbilities(player);
            World.AddObject(player);
            player.Transform.Local.SetPosition(position);
            player.Region = World.GetRegionByPos(position);
            player.Region.AddObject(player);
            var session = new TempleSession(player);
            player.Connection = new GameConnection(session) { ActiveChar = player };
            _sessions.Add(player, session);
            return player;
        }

        public Npc GuardAt(Vector3 position)
        {
            var guard = new Npc { ObjId = (uint)(1000 + position.X), Name = "Guard", ParentWorld = World,
                Template = new NpcTemplate { NoPenalty = true, NoExp = true }, Region = World.GetRegionByPos(position) };
            guard.Transform.Local.SetPosition(position);
            return guard;
        }

        private void Install<T>(T manager) where T : class
        {
            var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
            _singletons.Add((field, field.GetValue(null)));
            field.SetValue(null, manager);
        }

        public void Dispose()
        {
            foreach (var player in _sessions.Keys)
                MovementValidation.Reset(player.ObjId);
            AppConfiguration.Instance.World = _oldWorld;
            LogManager.Configuration = _previousLogging;
            MovementWarnings.Dispose();
            foreach (var (field, previous) in _singletons)
                field.SetValue(null, previous);
        }
    }

    private sealed class TempleSession(Character player) : ISession
    {
        private readonly List<ushort> _received = [];
        public int Count(ushort kind) => _received.Count(id => id == kind);
        public List<(ushort Kind, int Hp)> LifeEvents { get; } = [];
        public List<int> Waits { get; } = [];
        public List<uint> Created { get; } = [];
        public List<uint> Removed { get; } = [];
        public List<(uint Id, int Hp)> Points { get; } = [];
        public List<Vector3> Destinations { get; } = [];
        public List<float> ReturnHeadings { get; } = [];
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => player.Id;
        public Socket Socket => null;

        public void SendPacket(byte[] packet)
        {
            var kind = BitConverter.ToUInt16(packet, 6);
            _received.Add(kind);
            var body = new PacketStream(packet[8..]);
            switch (kind)
            {
                case SCOffsets.SCUnitStatePacket:
                    Created.Add(body.ReadBc());
                    break;
                case SCOffsets.SCUnitsRemovedPacket:
                    var count = body.ReadUInt16();
                    for (var i = 0; i < count; i++)
                        Removed.Add(body.ReadBc());
                    break;
                case SCOffsets.SCUnitPointsPacket:
                    Points.Add((body.ReadBc(), body.ReadInt32() / 100));
                    break;
                case SCOffsets.SCUnitDeathPacket:
                    body.ReadBc();
                    body.ReadByte();
                    Waits.Add(body.ReadInt32());
                    LifeEvents.Add((kind, player.Hp));
                    break;
                case SCOffsets.SCCharacterResurrectedPacket:
                    body.ReadBc();
                    var (x, y, z) = body.ReadPosition();
                    Destinations.Add(new Vector3(x, y, z));
                    ReturnHeadings.Add(body.ReadSingle());
                    LifeEvents.Add((kind, player.Hp));
                    break;
            }
        }

        public void AddAttribute(string name, object value) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }
}
