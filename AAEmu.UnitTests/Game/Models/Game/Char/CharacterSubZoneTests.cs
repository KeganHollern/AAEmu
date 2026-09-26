using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

[NotInParallel]
public sealed class CharacterSubZoneTests
{
    [Test]
    [Arguments(20u)]
    [Arguments(uint.MaxValue)]
    [Arguments(0u)]
    public async Task NotifyPacket_ForgedOrZeroHint_DerivesRealSubzoneAndUnlocksOnlyThatDestination(uint hint)
    {
        using var scope = new SubZoneScope();
        var owner = scope.Owner;
        var packet = new CSNotifySubZonePacket { Connection = owner.Connection };
        packet.Read(new PacketStream().Write(hint));
        packet.Read(new PacketStream().Write(hint));

        await Assert.That(owner.SubZoneId).IsEqualTo(10u);
        await Assert.That(owner.Portals.DistrictPortals.Keys).IsEquivalentTo(new uint[] { 100, 101 });
        scope.Session.SendPacket(Any<byte[]>()).WasCalled(Times.Once);
    }

    [Test]
    public async Task NotifyPacket_GenuineEntry_UnlocksNewDestinationOnce()
    {
        using var scope = new SubZoneScope();
        scope.Owner.Portals.NotifySubZone(10);
        scope.Owner.Transform.Local.SetPosition(20005, 30005, 10);
        scope.Owner.Portals.NotifySubZone(20);
        scope.Owner.Portals.NotifySubZone(20);

        await Assert.That(scope.Owner.SubZoneId).IsEqualTo(20u);
        await Assert.That(scope.Owner.Portals.DistrictPortals.Keys).IsEquivalentTo(new uint[] { 100, 101, 200 });
        scope.Session.SendPacket(Any<byte[]>()).WasCalled(Times.Exactly(2));
    }

    [Test]
    [Arguments("outside")]
    [Arguments("other-world")]
    [Arguments("missing-world")]
    [Arguments("stale-instance")]
    [Arguments("nan")]
    [Arguments("infinite-height")]
    public async Task NotifyPacket_InvalidPosition_DoesNotUnlockAnyDestination(string scenario)
    {
        using var scope = new SubZoneScope();
        switch (scenario)
        {
            case "outside": scope.Owner.Transform.Local.SetPosition(50000, 50000, 0); break;
            case "other-world": scope.SetWorld(new WorldInstance(new WorldTemplate { Id = 2 }, 0, true, 42)); break;
            case "missing-world": scope.SetWorld(null); break;
            case "stale-instance": scope.SetWorld(new WorldInstance(scope.World.Template, 0, true, 43)); break;
            case "nan": scope.Owner.Transform.Local.SetPosition(float.NaN, 5, 0); break;
            case "infinite-height": scope.Owner.Transform.Local.SetPosition(5, 5, float.PositiveInfinity); break;
        }
        scope.Owner.Portals.NotifySubZone(20);

        await Assert.That(scope.Owner.Portals.DistrictPortals).IsEmpty();
        scope.Session.SendPacket(Any<byte[]>()).WasCalled(Times.Never);
    }

    [Test]
    public async Task NotifyPacket_OutsideAuthoredHeight_DoesNotUnlockRecall()
    {
        using var scope = new SubZoneScope();
        scope.World.Template.SubZones[1][0].Height = 5;
        scope.Owner.Portals.NotifySubZone(10);
        await Assert.That(scope.Owner.SubZoneId).IsEqualTo(0u);
        await Assert.That(scope.Owner.Portals.DistrictPortals).IsEmpty();
        scope.Owner.Transform.Local.SetHeight(5);
        scope.Owner.Portals.NotifySubZone(10);
        await Assert.That(scope.Owner.SubZoneId).IsEqualTo(10u);
        await Assert.That(scope.Owner.Portals.DistrictPortals.Count).IsEqualTo(2);
    }

    [Test]
    public async Task NotifyPacket_OverlappingAreas_AcceptsOnlyAContainingHint()
    {
        using var scope = new SubZoneScope();
        scope.World.Template.SubZones[1].Add(new Area
        {
            Id = 30, Points = [new(0, 0, 0), new(10, 0, 0), new(10, 10, 0), new(0, 10, 0)]
        });
        scope.Owner.Portals.NotifySubZone(30);
        await Assert.That(scope.Owner.SubZoneId).IsEqualTo(30u);
        await Assert.That(scope.Owner.Portals.DistrictPortals).IsEmpty();
        scope.Owner.Portals.NotifySubZone(20);
        await Assert.That(scope.Owner.SubZoneId).IsEqualTo(30u);
        scope.Owner.Portals.NotifySubZone(10);
        await Assert.That(scope.Owner.SubZoneId).IsEqualTo(10u);
        await Assert.That(scope.Owner.Portals.DistrictPortals.Count).IsEqualTo(2);
    }

    private sealed class SubZoneScope : IDisposable
    {
        private readonly List<(FieldInfo Field, object Previous)> _fields = [];
        public Character Owner { get; }
        public WorldInstance World { get; }
        public Mock<ISession> Session { get; } = Mock.Of<ISession>();

        public SubZoneScope()
        {
            var manager = new SubZoneManager(Mock.Of<IWorldManager>().Object, Mock.Of<IZoneManager>().Object);
            Replace(typeof(Singleton<SubZoneManager>), "s_instance", manager);
            var portals = new PortalManager(Mock.Of<ILocalizationManager>().Object, Mock.Of<IWorldManager>().Object,
                Mock.Of<IZoneManager>().Object, Mock.Of<INpcManager>().Object, Mock.Of<IObjectIdManager>().Object,
                Mock.Of<ITaskManager>().Object);
            typeof(PortalManager).GetField("_recalls", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(portals,
                new Dictionary<uint, List<Portal>>
                {
                    [10] = [new() { Id = 100, SubZoneId = 10, Name = "near" }, new() { Id = 101, SubZoneId = 10, Name = "near alternate" }],
                    [20] = [new() { Id = 200, SubZoneId = 20, Name = "far" }]
                });
            typeof(PortalManager).GetField("_districtReturnPoints", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(portals, new Dictionary<uint, DistrictReturnPoints>());
            Replace(typeof(Singleton<PortalManager>), "s_instance", portals);
            var ids = new VisitedSubZoneIdManager();
            ids.Initialize(true);
            Replace(typeof(VisitedSubZoneIdManager), "_instance", ids);
            var template = new WorldTemplate { Id = 1 };
            template.SubZones[1] =
            [
                new Area { Id = 10, Points = [new(0, 0, 0), new(10, 0, 0), new(10, 10, 0), new(0, 10, 0)] },
                new Area { Id = 20, Points = [new(20000, 30000, 0), new(20010, 30000, 0), new(20010, 30010, 0), new(20000, 30010, 0)] }
            ];
            World = new WorldInstance(template, 0, true, 42);
            Owner = new CharacterMock { Id = 524, Name = "SubZone", Faction = new SystemFaction() };
            Owner.Portals = new CharacterPortals(Owner);
            Owner.Connection = new GameConnection(Session.Object) { ActiveChar = Owner };
            typeof(Transform).GetField("_instanceId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Owner.Transform, 42u);
            SetWorld(World);
            Owner.Transform.Local.SetPosition(5, 5, 10);
        }

        public void SetWorld(WorldInstance world) => typeof(GameObject)
            .GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Owner, world);

        private void Replace(Type type, string name, object value)
        {
            var field = type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;
            _fields.Add((field, field.GetValue(null)));
            field.SetValue(null, value);
        }

        public void Dispose()
        {
            foreach (var (field, previous) in _fields.AsEnumerable().Reverse())
                field.SetValue(null, previous);
        }
    }
}
