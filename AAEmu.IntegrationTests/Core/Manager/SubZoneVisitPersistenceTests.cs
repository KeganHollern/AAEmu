using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Game.World.Zones;

using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class SubZoneVisitPersistenceTests
{
    [Fact]
    public void NotifySubZone_ForgedRemoteHint_SaveAndReloadKeepsOnlyRealVisit()
    {
        const uint OwnerId = 4_200_524;
        using var scope = new SubZoneScope(OwnerId);
        try
        {
            scope.Owner.Portals.NotifySubZone(20);
            Save(scope.Owner.Portals);
            var reloaded = Reload(OwnerId);
            Assert.Equal(new uint[] { 100, 101 }, reloaded.DistrictPortals.Keys.Order().ToArray());
            Assert.Null(reloaded.GetPortalInfo(200));

            scope.Owner.Transform.Local.SetPosition(20005, 30005, 10);
            scope.Owner.Portals.NotifySubZone(20);
            scope.Owner.Portals.NotifySubZone(20);
            Save(scope.Owner.Portals);
            reloaded = Reload(OwnerId);
            Assert.Equal(new uint[] { 100, 101, 200 }, reloaded.DistrictPortals.Keys.Order().ToArray());
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM portal_visited_district WHERE owner=@owner";
            command.Parameters.AddWithValue("@owner", OwnerId);
            Assert.Equal(2L, Convert.ToInt64(command.ExecuteScalar()));
        }
        finally
        {
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM portal_visited_district WHERE owner=@owner";
            command.Parameters.AddWithValue("@owner", OwnerId);
            command.ExecuteNonQuery();
        }
    }

    private static void Save(CharacterPortals portals)
    {
        using var connection = MySQL.CreateConnection();
        using var transaction = connection.BeginTransaction();
        portals.Save(connection, transaction);
        transaction.Commit();
    }

    private static CharacterPortals Reload(uint id)
    {
        var owner = new Character(null) { Id = id, Faction = new SystemFaction() };
        var portals = new CharacterPortals(owner);
        using var connection = MySQL.CreateConnection();
        portals.Load(connection);
        return portals;
    }

    private sealed class SubZoneScope : IDisposable
    {
        private readonly List<(FieldInfo Field, object Previous)> _fields = [];
        public Character Owner { get; }
        public WorldInstance World { get; }
        

        public SubZoneScope(uint ownerId)
        {
            var manager = new SubZoneManager(Mock.Of<IWorldManager>(), Mock.Of<IZoneManager>());
            Replace(typeof(Singleton<SubZoneManager>), "s_instance", manager);
            var portals = new PortalManager(Mock.Of<ILocalizationManager>(), Mock.Of<IWorldManager>(),
                Mock.Of<IZoneManager>(), Mock.Of<INpcManager>(), Mock.Of<IObjectIdManager>(),
                Mock.Of<ITaskManager>());
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
            Owner = new Character(null) { Id = ownerId, Name = "SubZone", Faction = new SystemFaction() };
            Owner.Portals = new CharacterPortals(Owner);
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
