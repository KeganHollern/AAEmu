using System.Net;
using System.Net.Sockets;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.StaticValues;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Core.Managers.Name;

[NotInParallel]
public sealed class PlayerNameRouteTests
{
    private NameGameData _previousData;
    private string _previousLocale;
    private static FieldInfo DataField => typeof(Singleton<NameGameData>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;

    [Before(Test)]
    public void SetUp()
    {
        _previousData = (NameGameData)DataField.GetValue(null);
        _previousLocale = AppConfiguration.Instance.DefaultLanguage;
        AppConfiguration.Instance.DefaultLanguage = "en_us";
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE allowed_name_chars (id INTEGER, char TEXT, bytes INTEGER);
            CREATE TABLE blocked_texts
                (id INTEGER, utf8str TEXT, bytes INTEGER, check_name TEXT, check_chat TEXT, partial_match TEXT);
            INSERT INTO blocked_texts VALUES (8000001, 'Admin', 5, 't', 'f', 't'), (2, 'staff', 5, 't', 'f', 'f');
            """;
        command.ExecuteNonQuery();
        var data = new NameGameData();
        data.Load(connection);
        DataField.SetValue(null, data);
    }

    [After(Test)]
    public void TearDown()
    {
        DataField.SetValue(null, _previousData);
        AppConfiguration.Instance.DefaultLanguage = _previousLocale;
    }

    [Test]
    [Arguments("Admin", false)]
    [Arguments("Administrator", false)]
    [Arguments("A", false)]
    [Arguments("Ab", true)]
    [Arguments("Éva", true)]
    [Arguments("Aaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [Arguments("Aaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [Arguments(null, false)]
    public async Task CharacterValidation_AppliesNativeLimitsAndReservedNames(string name, bool expected)
    {
        var manager = new NameManager();
        manager.Load([], [], []);

        await Assert.That(manager.ValidateCharacterName(name) == CharacterCreateError.Ok).IsEqualTo(expected);
    }

    [Test]
    [Arguments("\nKegan")]
    [Arguments("Kegan\n")]
    [Arguments("\tKegan")]
    [Arguments("Kegan\r")]
    [Arguments("Ke\0gan")]
    [Arguments("unpaired-surrogate")]
    [Arguments("Ke😀gan")]
    public async Task CharacterCreate_UnsafeRawInput_RejectsBeforeNameLookupOrRegistration(string name)
    {
        if (name == "unpaired-surrogate")
            name = "Ke\ud800gan";
        var names = Mock.Of<INameManager>();
        var ids = Mock.Of<ICharacterIdManager>();
        var items = Mock.Of<IItemManager>();
        var manager = new CharacterManager(Mock.Of<IWorldManager>().Object, Mock.Of<IAccountManager>().Object,
            names.Object, ids.Object, Mock.Of<IFactionManager>().Object, Mock.Of<ISkillManager>().Object,
            items.Object, Mock.Of<IHousingManager>().Object, Mock.Of<IFamilyManager>().Object,
            Mock.Of<IMailManager>().Object, Mock.Of<ITaskManager>().Object);
        var connection = new GameConnection(null) { AccountId = 1 };

        manager.Create(connection, name, Race.Nuian, Gender.Male, [], null,
            (AbilityType)1, (AbilityType)0, (AbilityType)0, 1);

        Mock.VerifyNoOtherCalls(names);
        Mock.VerifyNoOtherCalls(ids);
        Mock.VerifyNoOtherCalls(items);
        await Assert.That(connection.Characters.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("gALLANT", "Gallant")]
    [Arguments(" Gallant", null)]
    [Arguments("Gallant ", null)]
    [Arguments("\nGallant", null)]
    [Arguments("Gallant\n", null)]
    [Arguments("Admin", null)]
    [Arguments("A", null)]
    [Arguments("Two words", null)]
    [Arguments("Aaaaaaaaaaaaaaaaaaaaaaaaaaa", null)]
    public async Task PetRename_OnlyPublishesTheAcceptedStoredName(string requestedName, string expectedName)
    {
        var owner = new Character(null) { Id = 1, ObjId = 100 };
        var session = new RecordingSession();
        var connection = new GameConnection(session) { ActiveChar = owner };
        owner.Connection = connection;
        var mate = new Mate { ObjId = 101, TlId = 10, OwnerObjId = owner.ObjId, Name = "Oldname" };
        ObserveMate(mate, owner);
        var manager = new MateManager(null);
        manager.TrackActiveMate(owner.Id, mate);

        var result = manager.RenameMount(connection, mate.TlId, requestedName);

        await Assert.That(result != null).IsEqualTo(expectedName != null);
        await Assert.That(mate.Name).IsEqualTo(expectedName ?? "Oldname");
        await Assert.That(session.Packets.Count).IsEqualTo(expectedName == null ? 0 : 1);
        if (expectedName != null)
        {
            var packet = new PacketStream(session.Packets[0][8..]);
            await Assert.That(packet.ReadBc()).IsEqualTo(mate.ObjId);
            await Assert.That(packet.ReadString()).IsEqualTo(expectedName);
            var row = new MateDb();
            CharacterMates.CopyPersistentMateState(row, mate, DateTime.UtcNow);
            await Assert.That(row.Name).IsEqualTo(expectedName);
        }
    }

    [Test]
    public async Task PetRename_ForeignTrackedMate_PreservesItsName()
    {
        var owner = new Character(null) { Id = 1, ObjId = 100 };
        var session = new RecordingSession();
        owner.Connection = new GameConnection(session) { ActiveChar = owner };
        var mate = new Mate { ObjId = 101, TlId = 10, OwnerObjId = 999, Name = "Oldname" };
        ObserveMate(mate, owner);
        var manager = new MateManager(null);
        manager.TrackActiveMate(owner.Id, mate);

        await Assert.That(manager.RenameMount(new GameConnection(null) { ActiveChar = owner }, mate.TlId, "Gallant")).IsNull();
        await Assert.That(mate.Name).IsEqualTo("Oldname");
        await Assert.That(session.Packets.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("", false)]
    [Arguments("Admin", false)]
    [Arguments("A", false)]
    [Arguments("Two words", false)]
    [Arguments("Aaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [Arguments("Home", true)]
    [Arguments("\u017Ftaff", false)]
    public async Task HouseRename_RejectsInvalidInputBeforeDirtyState(string name, bool expected)
    {
        var manager = CreateHousingManager();
        var owner = new Character(null) { Id = 1 };
        var house = new House { Id = 42, TlId = 7, OwnerId = owner.Id, Name = "Oldname",
            Template = new HousingTemplate { HousingBindingDoodad = [] }, CurrentStep = -1 };
        ((Dictionary<uint, House>)typeof(HousingManager).GetField("_houses", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager)!).Add(house.Id, house);
        ((Dictionary<ushort, House>)typeof(HousingManager).GetField("_housesTl", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager)!).Add(house.TlId, house);
        house.IsDirty = false;

        manager.ChangeHouseName(new GameConnection(null) { ActiveChar = owner }, house.TlId, name);

        await Assert.That(house.Name).IsEqualTo(expected ? name : "Oldname");
        await Assert.That(house.IsDirty).IsEqualTo(expected);
    }

    private static HousingManager CreateHousingManager()
    {
        return new HousingManager(Mock.Of<IObjectIdManager>().Object, Mock.Of<IFactionManager>().Object,
            Mock.Of<ILocalizationManager>().Object, Mock.Of<IWorldManager>().Object, Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object, Mock.Of<IHousingIdManager>().Object, Mock.Of<IHousingTldManager>().Object,
            Mock.Of<IItemManager>().Object, Mock.Of<IMailManager>().Object, Mock.Of<INameManager>().Object,
            Mock.Of<IZoneManager>().Object, Mock.Of<IDoodadManager>().Object, Mock.Of<IUccManager>().Object);
    }

    private static void ObserveMate(Mate mate, Character observer)
    {
        var region = new Region(null, 0, 0, 0);
        typeof(Region).GetField("_neighbors", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(region, new[] { region });
        typeof(Region).GetField("_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(region, new GameObject[] { observer });
        typeof(Region).GetField("_objectsSize", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(region, 1);
        mate.Region = region;
    }

    private sealed class RecordingSession : ISession
    {
        private readonly Dictionary<string, object> _attributes = [];
        public List<byte[]> Packets { get; } = [];
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) => Packets.Add(packet.ToArray());
        public void AddAttribute(string name, object attribute) => _attributes.Add(name, attribute);
        public object GetAttribute(string name) => _attributes.GetValueOrDefault(name);
        public void ClearAttribute(string name) => _attributes.Remove(name);
        public void Close() { }
    }
}
