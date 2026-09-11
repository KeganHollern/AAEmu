using System.Reflection;
using AAEmu.Game.Models.Account;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Scripts.Commands;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.UnitTests.Game.Utils.Scripts;

public class CommandPermissionMetadataTests
{
    private static Type[] CommandTypes => typeof(Fly).Assembly.GetTypes()
        .Where(type => !type.IsAbstract && typeof(ICommand).IsAssignableFrom(type) &&
                       type.Namespace == typeof(Fly).Namespace).ToArray();

    private static ICommand Command(string name) => (ICommand)Activator.CreateInstance(
        CommandTypes.Single(type => type.Name == name), nonPublic: true);

    [Test]
    public async Task EveryRoot_HasExplicitPolicyAndUniqueAliases()
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in CommandTypes)
        {
            var command = Command(type.Name);
            await Assert.That(type.GetCustomAttribute<CommandPermissionAttribute>()).IsNotNull();
            await Assert.That(command.CommandNames.Length > 0).IsTrue();
            foreach (var alias in command.CommandNames)
                await Assert.That(aliases.Add(alias)).IsTrue();
        }
    }

    [Test]
    public async Task NormalPlayer_OnlyGetsTheThreeReadOnlyRootsInHelp()
    {
        var permitted = CommandTypes.Where(type =>
            type.GetCustomAttribute<CommandPermissionAttribute>()!.ShowInHelp &&
            CommandPermissions.Allows(AccountRole.NormalPlayer, Command(type.Name), []))
            .Select(type => type.Name).ToArray();

        await Assert.That(permitted).IsEquivalentTo(new[] { nameof(Help), nameof(Online), nameof(GetPosition) });
    }

    [Test]
    public async Task RemoteShop_IsHiddenGameplayTransportForEveryRole()
    {
        var command = new RemoteShop();
        var declaration = CommandPermissions.GetDeclaration(command);
        await Assert.That(declaration.ShowInHelp).IsFalse();
        await Assert.That(declaration.Permission).IsEqualTo(GamePermission.PlayerGameplay);
        foreach (var role in Enum.GetValues<AccountRole>())
        {
            await Assert.That(CommandPermissions.Allows(role, command, ["honor"])).IsTrue();
            await Assert.That(CommandPermissions.Allows(role, command, ["buy", "vocation", "1:1"])).IsTrue();
        }
        await Assert.That(CommandPermissions.Allows(AccountRole.NormalPlayer, new Fly(), [])).IsFalse();
        await Assert.That(CommandPermissions.Allows(AccountRole.NormalPlayer, new DoodadCmd(), ["save", "all"])).IsFalse();
    }

    [Test]
    [Arguments(nameof(Scripts))]
    [Arguments(nameof(ReloadConfigs))]
    [Arguments(nameof(ReloadAuction))]
    [Arguments(nameof(WorldCmd))]
    [Arguments(nameof(TimeCmd))]
    [Arguments(nameof(GodMode))]
    [Arguments(nameof(SetTradePackMailDelay))]
    [Arguments(nameof(Snow))]
    [Arguments(nameof(MoveAll))]
    [Arguments(nameof(Nwrite))]
    [Arguments(nameof(DeSpawnAll))]
    [Arguments(nameof(SendPacket))]
    public async Task GlobalPersistentAndRawRoots_AreAdminOnly(string name)
    {
        var command = Command(name);
        await Assert.That(CommandPermissions.Allows(AccountRole.Moderator, command, [])).IsFalse();
        await Assert.That(CommandPermissions.Allows(AccountRole.Admin, command, [])).IsTrue();
    }

    [Test]
    [Arguments(nameof(Sphere), "add", "list")]
    [Arguments(nameof(Sphere), "remove", "quest")]
    [Arguments(nameof(ShipBarrierCmd), "reset", "status")]
    [Arguments(nameof(WaterDebugCmd), "reload", "info")]
    [Arguments(nameof(WaterDebugCmd), "reloadinfo", "setprobe")]
    [Arguments(nameof(TestAI), "load_path", "list")]
    [Arguments(nameof(TestAI), "clear_path_cache", "info")]
    [Arguments(nameof(TestAI), "clear_cache", "set_behavior")]
    [Arguments(nameof(InGameCashShop), "on", "list")]
    [Arguments(nameof(InGameCashShop), "off", "list")]
    [Arguments(nameof(InGameCashShop), "reload", "list")]
    [Arguments(nameof(TowerDef), "start", "list")]
    [Arguments(nameof(TowerDef), "next", "list")]
    [Arguments(nameof(TowerDef), "end", "list")]
    [Arguments(nameof(MoveTo), "save", "rec")]
    [Arguments(nameof(MoveTo), "go", "run")]
    [Arguments(nameof(MoveTo), "back", "stop")]
    [Arguments(nameof(TestHouse), "forcedemo", "settaxpaid")]
    [Arguments(nameof(TestHouse), "setdemosoon", "setforsale")]
    public async Task MixedRoots_ProtectAdminActionsWithoutRemovingStaffActions(string name, string adminAction,
        string staffAction)
    {
        var command = Command(name);
        await Assert.That(CommandPermissions.Allows(AccountRole.Moderator, command, [adminAction])).IsFalse();
        await Assert.That(CommandPermissions.Allows(AccountRole.Moderator, command, [adminAction.ToUpperInvariant()])).IsFalse();
        await Assert.That(CommandPermissions.Allows(AccountRole.Admin, command, [adminAction])).IsTrue();
        await Assert.That(CommandPermissions.Allows(AccountRole.Moderator, command, [staffAction])).IsTrue();
    }

    [Test]
    [Arguments(nameof(NpcCmd), "save", "z")]
    [Arguments(nameof(DoodadCmd), "save", "phase")]
    [Arguments(nameof(DoodadCmd), "remove", "removes")]
    [Arguments(nameof(SlaveCmd), "save", "info")]
    [Arguments(nameof(FeatureCmd), "set", "check")]
    [Arguments(nameof(FeatureCmd), "s", "c")]
    public async Task ChildPermissions_UseTheResolvedChildForEveryAlias(string name, string adminChild, string staffChild)
    {
        var command = Command(name);
        await Assert.That(CommandPermissions.Allows(AccountRole.Moderator, command, [adminChild.ToUpperInvariant(), "all"])).IsFalse();
        await Assert.That(CommandPermissions.Allows(AccountRole.Admin, command, [adminChild, "all"])).IsTrue();
        await Assert.That(CommandPermissions.Allows(AccountRole.Moderator, command, [staffChild.ToUpperInvariant()])).IsTrue();
    }

    [Test]
    public async Task ZoneState_ListIsStaffButEveryWriteArgumentIsAdminOnly()
    {
        var command = new TestZoneState();
        await Assert.That(CommandPermissions.Allows(AccountRole.Moderator, command, [])).IsTrue();
        foreach (var action in new[] { "0", "7", "Peace", "invalid", "" })
        {
            await Assert.That(CommandPermissions.Allows(AccountRole.Moderator, command, [action])).IsFalse();
            await Assert.That(CommandPermissions.Allows(AccountRole.Admin, command, [action])).IsTrue();
        }
    }

    [Test]
    [Arguments(nameof(Fly))]
    [Arguments(nameof(Teleport))]
    [Arguments(nameof(BuildHouse))]
    [Arguments(nameof(HouseBindingMove))]
    [Arguments(nameof(DoodadLocationCmd))]
    public async Task PlayerTargetSupport_RemainsSharedByModeratorAndAdmin(string name)
    {
        var command = Command(name);
        await Assert.That(CommandPermissions.Allows(AccountRole.NormalPlayer, command, [])).IsFalse();
        await Assert.That(CommandPermissions.Allows(AccountRole.Moderator, command, [])).IsTrue();
        await Assert.That(CommandPermissions.Allows(AccountRole.Admin, command, [])).IsTrue();
    }

    [Test]
    public async Task TestTransfer_IsDisabledForEveryRole()
    {
        foreach (var role in Enum.GetValues<AccountRole>())
            await Assert.That(CommandPermissions.Allows(role, new TestTransfer(), [])).IsFalse();
    }

    [Test]
    public async Task CorrectedAliasesAndGlobalHelp_MatchTheirActualCommands()
    {
        await Assert.That(new AddGold().CommandNames).IsEquivalentTo(new[] { "addgold", "add_gold" });
        await Assert.That(new GoldCmd().CommandNames).IsEquivalentTo(new[] { "gold" });
        await Assert.That(new CrimeCmd().CommandNames).IsEquivalentTo(new[] { "crime" });
        await Assert.That(new FeatureCmd().CommandNames).IsEquivalentTo(new[] { "feature", "fset", "fs" });
        await Assert.That(new GodMode().GetCommandHelpText()).Contains("global World.GodMode");
    }
}
