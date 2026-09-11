using System.Drawing;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Account;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class CommandManagerTests
{
    [Test]
    [Arguments(AccountRole.NormalPlayer, false)]
    [Arguments(AccountRole.Moderator, true)]
    [Arguments(AccountRole.Admin, true)]
    public async Task Handle_StaffAlias_UsesCurrentRoleAndOneAuditRow(AccountRole role, bool allowed)
    {
        var permissions = new Permissions { Role = role };
        var audit = new AuditStore();
        var command = new StaffCommand();
        var manager = new CommandManager(permissions, audit);
        manager.Register(["tool", "alias"], command);

        manager.Handle(Actor(), "ALIAS \"two words\" x", new Output());

        await Assert.That(command.Calls).IsEqualTo(allowed ? 1 : 0);
        await Assert.That(audit.Starts).IsEqualTo(1);
        await Assert.That(audit.Completions).IsEqualTo(1);
        await Assert.That(audit.Entry.Command).IsEqualTo("tool");
        await Assert.That(audit.Entry.Arguments).IsEquivalentTo(new[] { "two words", "x" });
        await Assert.That(audit.Entry.ActorAccountId).IsEqualTo(10u);
        await Assert.That(audit.Entry.ActorCharacterId).IsEqualTo(11u);
        await Assert.That(audit.Entry.Result).IsEqualTo(allowed ? "completed" : "denied");
        await Assert.That(audit.Entry.CompletedAt).IsNotNull();
    }

    [Test]
    public async Task Handle_Demotion_TakesEffectOnNextCommand()
    {
        var permissions = new Permissions { Role = AccountRole.Admin };
        var audit = new AuditStore();
        var command = new StaffCommand();
        var manager = new CommandManager(permissions, audit);
        manager.Register("tool", command);
        manager.Handle(Actor(), "tool", new Output());
        permissions.Role = AccountRole.NormalPlayer;
        manager.Handle(Actor(), "tool", new Output());
        await Assert.That(command.Calls).IsEqualTo(1);
        await Assert.That(audit.Starts).IsEqualTo(2);
        await Assert.That(audit.Entry.Result).IsEqualTo("denied");
    }

    [Test]
    public async Task Handle_AuditUnavailable_DoesNotRunCommand()
    {
        var audit = new AuditStore { FailStart = true };
        var command = new StaffCommand();
        var manager = new CommandManager(new Permissions { Role = AccountRole.Admin }, audit);
        manager.Register("tool", command);
        manager.Handle(Actor(), "tool", new Output());
        await Assert.That(command.Calls).IsEqualTo(0);
        await Assert.That(audit.Completions).IsEqualTo(0);
    }

    [Test]
    public async Task Handle_DeferredResult_CompletesOnlyOnceAfterHandlerReturns()
    {
        var audit = new AuditStore();
        var command = new DeferredCommand();
        var manager = new CommandManager(new Permissions { Role = AccountRole.Admin }, audit);
        manager.Register("deferred", command);
        manager.Handle(Actor(), "deferred", new Output());
        await Assert.That(audit.Starts).IsEqualTo(1);
        await Assert.That(audit.Completions).IsEqualTo(0);
        command.Complete(false, "Login rejected the request.");
        command.Complete(true, "Duplicate result.");
        await Assert.That(audit.Completions).IsEqualTo(1);
        await Assert.That(audit.Entry.Result).IsEqualTo("rejected");
        await Assert.That(audit.Entry.Targets.Last().AccountId).IsEqualTo(20u);
    }

    [Test]
    [Arguments("tool \"unterminated")]
    [Arguments("tool bad\nline")]
    public async Task Handle_InvalidSyntax_DoesNotRunCommand(string text)
    {
        var audit = new AuditStore();
        var command = new StaffCommand();
        var manager = new CommandManager(new Permissions { Role = AccountRole.Admin }, audit);
        manager.Register("tool", command);
        manager.Handle(Actor(), text, new Output());
        await Assert.That(command.Calls).IsEqualTo(0);
        await Assert.That(audit.Entry.Result).IsEqualTo("rejected");
    }

    [Test]
    public async Task Registration_DuplicateAliasAndMissingPolicy_AreRejected()
    {
        var manager = new CommandManager(new Permissions(), new AuditStore());
        manager.Register(["tool", "alias"], new StaffCommand());
        await Assert.That(() => manager.Register("ALIAS", new StaffCommand())).Throws<InvalidOperationException>();
        await Assert.That(() => manager.Register("unknown", new UnclassifiedCommand())).Throws<InvalidOperationException>();
        manager.Clear();
        manager.Register(["tool", "alias"], new StaffCommand());
        await Assert.That(manager.GetCommandNameBase("ALIAS")).IsEqualTo("tool");
    }

    [Test]
    public async Task HelpAndExecution_UseCanonicalChildPolicyForEveryAlias()
    {
        var root = new RootCommand();
        var audit = new AuditStore();
        var manager = new CommandManager(new Permissions { Role = AccountRole.Moderator }, audit);
        manager.Register(["root", "r"], root);
        await Assert.That(manager.TryGetHelp(AccountRole.Moderator, ["r"], out var help)).IsTrue();
        await Assert.That(help).DoesNotContain("save");
        await Assert.That(manager.TryGetHelp(AccountRole.Moderator, ["r", "WRITE"], out _)).IsFalse();
        await Assert.That(manager.TryGetHelp(AccountRole.Admin, ["r", "WRITE"], out _)).IsTrue();
        manager.Handle(Actor(), "R WRITE", new Output());
        await Assert.That(root.Save.Calls).IsEqualTo(0);
        await Assert.That(audit.Entry.Result).IsEqualTo("denied");
    }

    [Test]
    public async Task RejectWebApiCommand_RecordsTransportAddressWithoutCharacterImpersonation()
    {
        var audit = new AuditStore();
        var manager = new CommandManager(new Permissions(), audit);
        manager.RejectWebApiCommand("192.0.2.4", "role", "{\"character\":\"Admin\"}");
        await Assert.That(audit.Starts).IsEqualTo(1);
        await Assert.That(audit.Completions).IsEqualTo(1);
        await Assert.That(audit.Entry.ActorAccountId).IsEqualTo(0u);
        await Assert.That(audit.Entry.Source).IsEqualTo("web-api");
        await Assert.That(audit.Entry.RemoteAddress).IsEqualTo("192.0.2.4");
        await Assert.That(audit.Entry.Result).IsEqualTo("denied");
    }

    private static Character Actor() => new(new UnitCustomModelParams()) { AccountId = 10, Id = 11 };
    private sealed class Permissions : IPermissionManager
    {
        public AccountRole Role { get; set; }
        public AccountRole GetRole(ICharacter character) => Role;
        public AccountRole GetRole(uint accountId) => Role;
        public bool CanUse(ICharacter character, GamePermission permission) => PermissionPolicy.Allows(Role, permission);
        public bool CanModerate(uint actorAccountId, uint targetAccountId) => false;
    }

    private sealed class AuditStore : ICommandAuditStore
    {
        public int Starts { get; private set; }
        public int Completions { get; private set; }
        public bool FailStart { get; init; }
        public CommandAuditEntry Entry { get; private set; }
        public void Start(CommandAuditEntry entry)
        {
            if (FailStart) throw new InvalidOperationException("Injected unavailable audit.");
            Starts++;
            Entry = entry;
        }
        public void Complete(CommandAuditEntry entry) { Completions++; Entry = entry; }
    }

    private sealed class Output : IMessageOutput
    {
        public IEnumerable<string> Messages => [];
        public IEnumerable<string> ErrorMessages => [];
        public void SendMessage(string message) { }
        public void SendMessage(ChatType type, string message, Color? color = null) { }
        public void SendMessage(ICharacter target, string message) { }
    }

    private class UnclassifiedCommand : ICommand
    {
        public string[] CommandNames { get; set; } = ["tool"];
        public int Calls { get; protected set; }
        public virtual void Execute(Character character, string[] args, IMessageOutput output) => Calls++;
        public string GetCommandLineHelp() => "";
        public string GetCommandHelpText() => "";
    }

    [CommandPermission(GamePermission.StaffCommands)]
    private sealed class StaffCommand : UnclassifiedCommand;

    [CommandPermission(GamePermission.StaffCommands)]
    private sealed class DeferredCommand : UnclassifiedCommand
    {
        public Action<bool, string> Complete { get; private set; }
        public override void Execute(Character character, string[] args, IMessageOutput output)
        {
            CommandAuditContext.RecordTarget(20, 21);
            Complete = CommandAuditContext.DeferCompletion();
        }
    }

    [CommandPermission(GamePermission.StaffCommands)]
    private sealed class RootCommand : SubCommandBase, ICommand
    {
        public SaveCommand Save { get; } = new();
        public string[] CommandNames { get; set; } = ["root"];
        public RootCommand() { Register(Save, "save", "write"); Description = "Tools."; }
        public void Execute(Character character, string[] args, IMessageOutput output) => PreExecute(character, "root", args, output);
        public string GetCommandLineHelp() => "";
        public string GetCommandHelpText() => "";
    }

    [CommandPermission(GamePermission.EditWorld)]
    private sealed class SaveCommand : SubCommandBase
    {
        public int Calls { get; private set; }
        public override void Execute(ICharacter character, string trigger, string[] args, IMessageOutput output) => Calls++;
    }
}
