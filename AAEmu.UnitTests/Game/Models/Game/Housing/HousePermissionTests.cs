using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Expeditions;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

[NotInParallel]
public sealed class HousePermissionTests
{
    private readonly List<(FieldInfo Field, object Previous)> _singletons = [];
    private Family _family;
    private Expedition _guild;

    [Before(Test)]
    public void SetUp()
    {
        _family = new Family { Id = 30 };
        _family.Members.Add(new FamilyMember { Id = 1 });
        var families = new FamilyManager(Mock.Of<IWorldManager>().Object, Mock.Of<IChatManager>().Object,
            Mock.Of<IFamilyIdManager>().Object);
        typeof(FamilyManager).GetField("_families", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(families, new Dictionary<uint, Family> { [_family.Id] = _family });
        Install(families);
        _guild = new Expedition { Id = (FactionsEnum)50 };
        _guild.Members.Add(new ExpeditionMember { CharacterId = 1, ExpeditionId = _guild.Id });
        var guilds = new ExpeditionManager(Mock.Of<IExpeditionIdManager>().Object, Mock.Of<ITeamManager>().Object,
            Mock.Of<IWorldManager>().Object, Mock.Of<IChatManager>().Object);
        typeof(ExpeditionManager).GetField("_expeditions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(guilds, new Dictionary<FactionsEnum, Expedition> { [_guild.Id] = _guild });
        Install(guilds);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, previous) in _singletons.AsEnumerable().Reverse())
            field.SetValue(null, previous);
    }

    [Test]
    [Arguments(HousingPermission.Private)]
    [Arguments(HousingPermission.Family)]
    [Arguments(HousingPermission.Guild)]
    [Arguments(HousingPermission.Public)]
    public async Task AllowedToInteract_OfflineOwnerAccount_HasAccess(HousingPermission permission)
    {
        var house = CreateHouse(permission);
        await Assert.That(house.AllowedToInteract(new Character(null) { Id = 1, AccountId = 10 })).IsTrue();
        await Assert.That(house.AllowedToInteract(new Character(null) { Id = 2, AccountId = 10 })).IsTrue();
    }

    [Test]
    [Arguments(HousingPermission.Private)]
    [Arguments(HousingPermission.Family)]
    [Arguments(HousingPermission.Guild)]
    [Arguments((HousingPermission)255)]
    public async Task AllowedToInteract_UnrelatedCharacterWithoutMembership_IsDenied(HousingPermission permission)
    {
        await Assert.That(CreateHouse(permission).AllowedToInteract(new Character(null) { Id = 2, AccountId = 20 })).IsFalse();
    }

    [Test]
    [Arguments(0u, false)]
    [Arguments(30u, true)]
    [Arguments(31u, false)]
    public async Task AllowedToInteract_Family_ComparesOfflineOwnerMembership(uint family, bool allowed)
    {
        var player = new Character(null) { Id = 2, AccountId = 20, Family = family };
        await Assert.That(CreateHouse(HousingPermission.Family).AllowedToInteract(player)).IsEqualTo(allowed);
        _family.Members.Clear();
        await Assert.That(CreateHouse(HousingPermission.Family).AllowedToInteract(player)).IsFalse();
    }

    [Test]
    [Arguments(0u, false)]
    [Arguments(50u, true)]
    [Arguments(51u, false)]
    public async Task AllowedToInteract_Guild_ComparesOfflineOwnerMembership(uint guild, bool allowed)
    {
        var player = new Character(null)
        {
            Id = 2, AccountId = 20,
            Expedition = guild == 0 ? null : new Expedition { Id = (FactionsEnum)guild }
        };
        await Assert.That(CreateHouse(HousingPermission.Guild).AllowedToInteract(player)).IsEqualTo(allowed);
        _guild.Members.Clear();
        await Assert.That(CreateHouse(HousingPermission.Guild).AllowedToInteract(player)).IsFalse();
    }

    [Test]
    public async Task AllowedToInteract_PublicAndUnfinishedHouse_KeepPublicAccess()
    {
        var player = new Character(null) { Id = 2, AccountId = 20 };
        var house = CreateHouse(HousingPermission.Public);
        await Assert.That(house.AllowedToInteract(player)).IsTrue();
        house.Permission = HousingPermission.Private;
        house.Template.BuildSteps[0] = new HousingBuildStep();
        house.CurrentStep = 0;
        await Assert.That(house.AllowedToInteract(player)).IsTrue();
        house.CurrentStep = -1;
        house.Template = new HousingTemplate { AlwaysPublic = true, HousingBindingDoodad = [] };
        await Assert.That(house.AllowedToInteract(player)).IsTrue();
    }

    private static House CreateHouse(HousingPermission permission) => new()
    {
        Id = 42, OwnerId = 1, AccountId = 10,
        Template = new HousingTemplate { HousingBindingDoodad = [] }, CurrentStep = -1, Permission = permission
    };

    private void Install<T>(T manager) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _singletons.Add((field, field.GetValue(null)));
        field.SetValue(null, manager);
    }
}
