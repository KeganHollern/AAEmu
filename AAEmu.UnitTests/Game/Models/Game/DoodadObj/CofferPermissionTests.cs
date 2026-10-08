using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Exceptions;
using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Expeditions;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.DoodadObj;

[NotInParallel]
public sealed class CofferPermissionTests
{
    private readonly Dictionary<FieldInfo, object> _previousInstances = [];
    private CharacterMock _owner;
    private CharacterMock _visitor;
    private RecordingCoffer _coffer;
    private Family _family;
    private Expedition _guild;

    [Before(Test)]
    public void SetUp()
    {
        var worlds = new WorldManager(null, null, null, null, null);
        Install(worlds);
        var world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1);
        var instances = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worlds)!;
        instances[1] = world;
        var names = new NameManager();
        SetField(names, "_characterAccounts", new Dictionary<uint, uint> { [1] = 10, [2] = 20 });
        Install(names);
        Install(new LocalizationManager());
        _family = new Family { Id = 30 };
        _family.Members.Add(new FamilyMember { Id = 1 });
        var families = new FamilyManager(Mock.Of<IWorldManager>().Object, Mock.Of<IChatManager>().Object,
            Mock.Of<IFamilyIdManager>().Object);
        SetField(families, "_families", new Dictionary<uint, Family> { [30] = _family });
        Install(families);
        _guild = new Expedition { Id = (FactionsEnum)50 };
        _guild.Members.Add(new ExpeditionMember { CharacterId = 1, ExpeditionId = _guild.Id });
        var guilds = new ExpeditionManager(Mock.Of<IExpeditionIdManager>().Object, Mock.Of<ITeamManager>().Object,
            Mock.Of<IWorldManager>().Object, Mock.Of<IChatManager>().Object);
        SetField(guilds, "_expeditions", new Dictionary<FactionsEnum, Expedition> { [_guild.Id] = _guild });
        Install(guilds);
        _owner = new CharacterMock
        {
            Id = 1, ObjId = 1, AccountId = 10, ParentWorld = world,
            Family = _family.Id, Expedition = _guild, Connection = new GameConnection(null)
        };
        _owner.Connection.ActiveChar = _owner;
        _visitor = new CharacterMock { Id = 2, AccountId = 20 };
        _coffer = new RecordingCoffer
        {
            ObjId = 0xabcdef, OwnerId = _owner.Id, ParentWorld = world,
            Template = new DoodadCofferTemplate(),
            ItemContainer = new CofferContainer(_owner.Id, false) { CofferType = ChestType.Chest }
        };
        world.AddObject(_coffer);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, previous) in _previousInstances.Reverse())
            field.SetValue(null, previous);
    }

    [Test]
    [Arguments(HousingPermission.Private)]
    [Arguments(HousingPermission.Guild)]
    [Arguments(HousingPermission.Public)]
    [Arguments(HousingPermission.Family)]
    public async Task Read_EachNativePermission_ChangesDoodadDataAndBroadcastsExactResponse(HousingPermission permission)
    {
        _coffer.SetData(-1);
        var packet = new CSChangeDoodadDataPacket { Connection = _owner.Connection };
        byte[] nativeBody = [0xef, 0xcd, 0xab, (byte)permission, 0, 0, 0];
        var body = new PacketStream(nativeBody);

        packet.Read(body);

        await Assert.That(packet.TypeId).IsEqualTo((ushort)0xeb);
        await Assert.That(packet.Level).IsEqualTo((byte)1);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
        await Assert.That(_coffer.Data).IsEqualTo((int)permission);
        await Assert.That(_coffer.Packets).HasSingleItem();
        var response = _coffer.Packets[0];
        await Assert.That(response.TypeId).IsEqualTo((ushort)0x10e);
        await Assert.That(response.Level).IsEqualTo((byte)1);
        var responseBody = new PacketStream(response.Write(new PacketStream()).GetBytes());
        await Assert.That(responseBody.ReadBc()).IsEqualTo(_coffer.ObjId);
        await Assert.That(responseBody.ReadInt32()).IsEqualTo((int)permission);
        await Assert.That(responseBody.LeftBytes).IsEqualTo(0);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(4)]
    [Arguments(255)]
    [Arguments(256)]
    [Arguments(257)]
    [Arguments(258)]
    [Arguments(259)]
    [Arguments(int.MinValue)]
    [Arguments(int.MaxValue)]
    public async Task ChangeData_InvalidPermission_PreservesCurrentPermissionAndPublishesNothing(int permission)
    {
        _coffer.SetData((int)HousingPermission.Family);

        await Assert.That(DoodadManager.ChangeDoodadData(_owner, _coffer, permission)).IsFalse();
        await Assert.That(_coffer.Data).IsEqualTo((int)HousingPermission.Family);
        await Assert.That(_coffer.Packets).IsEmpty();
    }

    [Test]
    [Arguments(HousingPermission.Private)]
    [Arguments(HousingPermission.Guild)]
    [Arguments(HousingPermission.Public)]
    [Arguments(HousingPermission.Family)]
    public async Task ChangeData_NonOwnerCannotChangeAnyPermission(HousingPermission permission)
    {
        _coffer.SetData(-1);
        // The permission dialog belongs to the owner character, even for an account-shared coffer.
        _visitor.AccountId = _owner.AccountId;
        await Assert.That(DoodadManager.ChangeDoodadData(_visitor, _coffer, (int)permission)).IsFalse();
        await Assert.That(_coffer.Data).IsEqualTo(-1);
        await Assert.That(_coffer.Packets).IsEmpty();
    }

    [Test]
    [Arguments(HousingPermission.Family)]
    [Arguments(HousingPermission.Guild)]
    public async Task ChangeData_MissingMembership_PreservesCurrentPermission(HousingPermission permission)
    {
        _owner.Family = 0;
        _owner.Expedition = null;
        await Assert.That(DoodadManager.ChangeDoodadData(_owner, _coffer, (int)permission)).IsFalse();
        await Assert.That(_coffer.Data).IsEqualTo((int)HousingPermission.Private);
        await Assert.That(_coffer.Packets).IsEmpty();
    }

    [Test]
    public async Task Read_EveryTruncatedBody_DoesNotChangeThePermission()
    {
        byte[] nativeBody = [0xef, 0xcd, 0xab, 2, 0, 0, 0];
        for (var length = 0; length < nativeBody.Length; length++)
        {
            var packet = new CSChangeDoodadDataPacket { Connection = _owner.Connection };
            await Assert.That(() => packet.Read(new PacketStream(nativeBody[..length]))).Throws<MarshalException>();
            await Assert.That(_coffer.Data).IsEqualTo((int)HousingPermission.Private);
            await Assert.That(_coffer.Packets).IsEmpty();
        }
    }

    [Test]
    public async Task Read_UnknownObject_DoesNotChangeThePermission()
    {
        new CSChangeDoodadDataPacket { Connection = _owner.Connection }
            .Read(new PacketStream([0, 0, 0, 2, 0, 0, 0]));
        await Assert.That(_coffer.Data).IsEqualTo((int)HousingPermission.Private);
        await Assert.That(_coffer.Packets).IsEmpty();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(4)]
    [Arguments(255)]
    [Arguments(256)]
    [Arguments(257)]
    [Arguments(258)]
    [Arguments(259)]
    public async Task AllowedToInteract_InvalidStoredPermission_DeniesAccessWithoutByteWrapping(int permission)
    {
        _visitor.AccountId = _owner.AccountId;
        _visitor.Family = _family.Id;
        _visitor.Expedition = _guild;
        _coffer.SetData(permission);
        await Assert.That(_coffer.AllowedToInteract(_visitor)).IsFalse();
    }

    [Test]
    public async Task AllowedToInteract_ChangeFromPublicToPrivate_RevokesAnOpenVisitor()
    {
        _coffer.SetData((int)HousingPermission.Public);
        _coffer.OpenedBy = _visitor;
        await Assert.That(_coffer.AllowedToInteract(_visitor)).IsTrue();
        await Assert.That(DoodadManager.ChangeDoodadData(_owner, _coffer, (int)HousingPermission.Private)).IsTrue();
        await Assert.That(_coffer.AllowedToInteract(_visitor)).IsFalse();
        await Assert.That(_coffer.AllowedToInteract(_owner)).IsTrue();
    }

    [Test]
    public async Task AllowedToInteract_PrivateNormalAndOtherworldlyCoffers_KeepTheirAccountAndCharacterRules()
    {
        _visitor.AccountId = _owner.AccountId;
        await Assert.That(_coffer.AllowedToInteract(_visitor)).IsTrue();
        _coffer.ItemContainer.CofferType = ChestType.Otherworldly;
        await Assert.That(_coffer.AllowedToInteract(_visitor)).IsFalse();
        await Assert.That(_coffer.AllowedToInteract(_owner)).IsTrue();
    }

    [Test]
    [Arguments(HousingPermission.Family)]
    [Arguments(HousingPermission.Guild)]
    public async Task AllowedToInteract_OfflineOwner_UsesCurrentSavedMembership(HousingPermission permission)
    {
        _coffer.SetData((int)permission);
        await Assert.That(_coffer.AllowedToInteract(_visitor)).IsFalse();
        _visitor.Family = _family.Id;
        _visitor.Expedition = _guild;
        await Assert.That(_coffer.AllowedToInteract(_visitor)).IsTrue();
        _family.Members.Clear();
        _guild.Members.Clear();
        await Assert.That(_coffer.AllowedToInteract(_visitor)).IsFalse();
    }

    [Test]
    public async Task Use_PermissionDialogFunction_DoesNotChangeDataOrAdvancePhase()
    {
        _coffer.SetData((int)HousingPermission.Public);
        _coffer.ToNextPhase = true;
        new DoodadFuncCofferPerm().Use(_owner, _coffer, 22691, -1);
        await Assert.That(_coffer.Data).IsEqualTo((int)HousingPermission.Public);
        await Assert.That(_coffer.ToNextPhase).IsFalse();
        await Assert.That(_coffer.Packets).IsEmpty();
    }

    private void Install<T>(T manager) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousInstances.TryAdd(field, field.GetValue(null));
        field.SetValue(null, manager);
    }

    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class RecordingCoffer : DoodadCoffer
    {
        public List<GamePacket> Packets { get; } = [];
        public override void BroadcastPacket(GamePacket packet, bool self) => Packets.Add(packet);
    }
}
