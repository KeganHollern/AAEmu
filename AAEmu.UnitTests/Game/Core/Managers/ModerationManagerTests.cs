using System.Drawing;
using System.Net;
using System.Net.Sockets;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Account;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Scripts.Commands;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.UnitTests.Game.Core.Managers;

public class ModerationManagerTests
{
    [Test]
    [Arguments(AccountRole.NormalPlayer, AccountRole.NormalPlayer, false)]
    [Arguments(AccountRole.Moderator, AccountRole.NormalPlayer, true)]
    [Arguments(AccountRole.Moderator, AccountRole.Moderator, false)]
    [Arguments(AccountRole.Moderator, AccountRole.Admin, false)]
    [Arguments(AccountRole.Admin, AccountRole.NormalPlayer, true)]
    [Arguments(AccountRole.Admin, AccountRole.Moderator, true)]
    [Arguments(AccountRole.Admin, AccountRole.Admin, true)]
    public async Task ModerateAsync_RolePair_EnforcesTargetProtection(AccountRole actorRole, AccountRole targetRole, bool allowed)
    {
        var permissions = new TestPermissions(actorRole, targetRole);
        ModerationManager manager = null;
        var requests = new List<ModerationRequest>();
        manager = CreateManager(permissions, request =>
        {
            requests.Add(request);
            manager.CompleteRequest(new ModerationResult(request.RequestId, ModerationStatus.Success,
                new ModerationState(20, 1, false, 0, true, 0)));
            return true;
        });

        var result = await manager.ModerateAsync(Actor(), Target(), ModerationAction.Mute, 0, "test reason");

        await Assert.That(result.Status == ModerationStatus.Success).IsEqualTo(allowed);
        await Assert.That(requests.Count).IsEqualTo(allowed ? 1 : 0);
    }

    [Test]
    public async Task ModerateAsync_RoleChangeBeforeAction_RechecksCurrentPermission()
    {
        var permissions = new TestPermissions(AccountRole.Admin, AccountRole.NormalPlayer);
        var sends = 0;
        var manager = CreateManager(permissions, _ => { sends++; return false; });
        permissions.ActorRole = AccountRole.NormalPlayer;

        var result = await manager.ModerateAsync(Actor(), Target(), ModerationAction.Ban, 60, "test reason");

        await Assert.That(result.Status).IsEqualTo(ModerationStatus.InvalidRequest);
        await Assert.That(sends).IsEqualTo(0);
    }

    [Test]
    public async Task ModerateAsync_LoginDoesNotReply_ReturnsUnavailableWithoutStateChange()
    {
        var manager = CreateManager(new TestPermissions(AccountRole.Admin, AccountRole.NormalPlayer), _ => true,
            timeout: TimeSpan.FromMilliseconds(10));

        var result = await manager.ModerateAsync(Actor(), Target(), ModerationAction.Mute, 60, "test reason");

        await Assert.That(result.Status).IsEqualTo(ModerationStatus.Unavailable);
        await Assert.That(manager.CanChat(20)).IsFalse();
        await Assert.That(manager.TryAdmit(20, () => throw new Exception("Unknown state admitted."))).IsFalse();
    }

    [Test]
    public async Task ModerateAsync_AuthorizedRequestCanCompleteAfterLaterRoleChange()
    {
        var permissions = new TestPermissions(AccountRole.Moderator, AccountRole.NormalPlayer);
        ModerationRequest pending = null;
        var manager = CreateManager(permissions, request => { pending = request; return true; });
        var task = manager.ModerateAsync(Actor(), Target(), ModerationAction.Mute, 0, "test reason");
        permissions.ActorRole = AccountRole.NormalPlayer;
        manager.CompleteRequest(new ModerationResult(pending.RequestId, ModerationStatus.Success,
            new ModerationState(20, 1, false, 0, true, 0)));
        await Assert.That((await task).Status).IsEqualTo(ModerationStatus.Success);
        await Assert.That(manager.CanChat(20)).IsFalse();
    }

    [Test]
    public async Task RefreshAccountAsync_UsesReadOnlyWireRequestBeforeAdmission()
    {
        ModerationManager manager = null;
        ModerationRequest observed = null;
        manager = CreateManager(new TestPermissions(AccountRole.NormalPlayer, AccountRole.NormalPlayer), request =>
        {
            observed = request;
            manager.CompleteRequest(new ModerationResult(request.RequestId, ModerationStatus.Success,
                new ModerationState(20, 12, false, 0, true, 0)));
            return true;
        });

        var refreshed = await manager.RefreshAccountAsync(20);
        var admitted = false;
        await Assert.That(manager.TryAdmit(20, () => admitted = true)).IsTrue();
        await Assert.That(refreshed && admitted).IsTrue();
        await Assert.That(observed.Action).IsEqualTo(ModerationAction.ReadState);
        await Assert.That(observed.ActorAccountId).IsEqualTo(0u);
        await Assert.That(observed.ActorCharacterId).IsEqualTo(0u);
        await Assert.That(observed.Reason).IsEqualTo(string.Empty);
        await Assert.That(observed.RequestId).IsGreaterThan(0ul);
        await Assert.That(manager.CanChat(20)).IsFalse();
    }

    [Test]
    public async Task ApplyState_Ban_EvictsEveryTargetSessionAndBlocksAdmission()
    {
        var first = Connection(20, 1);
        var second = Connection(20, 2);
        var other = Connection(30, 3);
        var kicked = new List<uint>();
        var manager = CreateManager(new TestPermissions(AccountRole.Admin, AccountRole.NormalPlayer), _ => false,
            [first, second, other], (connection, _) => { kicked.Add(connection.Id); connection.Shutdown(); });

        manager.ApplyState(new ModerationState(20, 2, true, 0, false, 0));

        await Assert.That(kicked).IsEquivalentTo(new uint[] { 1, 2 });
        await Assert.That(first.IsClosed && second.IsClosed).IsTrue();
        await Assert.That(other.IsClosed).IsFalse();
        await Assert.That(manager.TryAdmit(20, () => throw new Exception("Banned account admitted."))).IsFalse();
    }

    [Test]
    public async Task ApplyState_StaleReadAfterBroadcast_CannotRestoreChatOrAdmission()
    {
        var manager = CreateManager(new TestPermissions(AccountRole.Admin, AccountRole.NormalPlayer), _ => false);
        manager.ApplyState(new ModerationState(20, 10, true, 0, true, 0));
        manager.ApplyState(new ModerationState(20, 9, false, 0, false, 0));

        await Assert.That(manager.CanChat(20)).IsFalse();
        await Assert.That(manager.TryAdmit(20, () => { })).IsFalse();

        manager.ApplyState(new ModerationState(20, 11, false, 0, false, 0));
        await Assert.That(manager.CanChat(20)).IsTrue();
        await Assert.That(manager.TryAdmit(20, () => { })).IsTrue();
    }

    [Test]
    public async Task State_ExpiryAndPermanent_EnforcesExactBoundary()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1000);
        var timed = new ModerationState(20, 1, true, 1001, true, 1001);
        var permanent = timed with { BanUntil = 0, MuteUntil = 0 };

        await Assert.That(timed.IsBanned(now) && timed.IsMuted(now)).IsTrue();
        await Assert.That(timed.IsBanned(now.AddSeconds(1)) || timed.IsMuted(now.AddSeconds(1))).IsFalse();
        await Assert.That(permanent.IsBanned(now.AddYears(20)) && permanent.IsMuted(now.AddYears(20))).IsTrue();
    }

    [Test]
    public async Task CompleteRequest_WrongTarget_DoesNotCompleteOrChangeState()
    {
        ModerationManager manager = null;
        manager = CreateManager(new TestPermissions(AccountRole.Admin, AccountRole.NormalPlayer), request =>
        {
            manager.CompleteRequest(new ModerationResult(request.RequestId, ModerationStatus.Success,
                new ModerationState(30, 1, false, 0, false, 0)));
            return true;
        }, timeout: TimeSpan.FromMilliseconds(10));

        var result = await manager.ModerateAsync(Actor(), Target(), ModerationAction.Unmute, 0, "test reason");

        await Assert.That(result.Status).IsEqualTo(ModerationStatus.Unavailable);
        await Assert.That(manager.CanChat(30)).IsFalse();
    }

    [Test]
    [Arguments(ModerationAction.Ban)]
    [Arguments(ModerationAction.Unban)]
    [Arguments(ModerationAction.Mute)]
    [Arguments(ModerationAction.Unmute)]
    public async Task Command_Completion_FollowsCommittedLoginResult(ModerationAction action)
    {
        var command = MakeCommand(action);
        var manager = new CommandManagerStub();
        var output = new TestOutput();
        var completions = new List<string>();
        var task = command.CompleteAsync(Actor(), Target(), 0, "test reason", output, manager,
            (outcome, _) => completions.Add(outcome));
        await Assert.That(task.IsCompleted).IsFalse();
        await Assert.That(completions.Count).IsEqualTo(0);
        manager.Completion.SetResult(new ModerationResult(1, ModerationStatus.Success,
            new ModerationState(20, 1, false, 0, false, 0)));
        await task;
        await Assert.That(manager.Action).IsEqualTo(action);
        await Assert.That(completions).IsEquivalentTo(new[] { "completed" });
        await Assert.That(output.Messages.Single()).Contains("completed");
    }

    [Test]
    public async Task Command_PendingAndDeferredAudit_UsesSameRowForFailure()
    {
        var command = new Ban();
        var manager = new CommandManagerStub();
        var output = new TestOutput();
        var audit = new TestAuditStore();
        var entry = new CommandAuditEntry();
        using (var context = new CommandAuditContext(entry, audit))
        {
            command.ExecuteCore(Actor(), ["account:20", "30m", "test reason"], output, manager);
            await Assert.That(context.IsDeferred).IsTrue();
            await Assert.That(entry.Targets.Single().AccountId).IsEqualTo(20u);
            await Assert.That(audit.Completions).IsEqualTo(0);
            await Assert.That(output.Messages.First()).Contains("pending");
        }
        manager.Completion.SetResult(new ModerationResult(1, ModerationStatus.Unavailable,
            new ModerationState(20, 0, false, 0, false, 0)));
        await audit.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(audit.Completions).IsEqualTo(1);
        await Assert.That(entry.Result).IsEqualTo("unconfirmed");
        await Assert.That(entry.Detail).Contains("Unavailable");
    }

    [Test]
    [Arguments("permanent", 0ul, true)]
    [Arguments("30m", 1800ul, true)]
    [Arguments("2h", 7200ul, true)]
    [Arguments("7d", 604800ul, true)]
    [Arguments("0s", 0ul, false)]
    [Arguments("-1h", 0ul, false)]
    [Arguments("30", 0ul, false)]
    [Arguments("18446744073709551615d", 0ul, false)]
    public async Task Command_Duration_UsesExplicitBoundedUnits(string text, ulong expected, bool valid)
    {
        await Assert.That(ModerationCommand.TryParseDuration(text, out var seconds)).IsEqualTo(valid);
        if (valid)
            await Assert.That(seconds).IsEqualTo(expected);
    }

    [Test]
    public async Task KickCommand_UsesModerationServiceAndResolvedTarget()
    {
        var manager = new CommandManagerStub();
        var output = new TestOutput();
        new Kick().ExecuteCore(Actor(), ["account:20", "test reason"], output, manager);
        await Assert.That(manager.KickedAccount).IsEqualTo(20u);
        await Assert.That(output.Messages.Single()).Contains("disconnected");
    }

    private static ModerationCommand MakeCommand(ModerationAction action) => action switch
    {
        ModerationAction.Ban => new Ban(),
        ModerationAction.Unban => new Unban(),
        ModerationAction.Mute => new Mute(),
        ModerationAction.Unmute => new Unmute(),
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private static Character Actor() => new(new UnitCustomModelParams()) { AccountId = 10, Id = 11 };
    private static ModerationTarget Target() => new(20, 21, "target");
    private static GameConnection Connection(uint account, uint session)
    {
        var connection = new GameConnection(new TestSession(session));
        connection.TryAuthenticate(account);
        return connection;
    }

    private static ModerationManager CreateManager(IPermissionManager permissions, Func<ModerationRequest, bool> send,
        IReadOnlyList<GameConnection> connections = null, Action<GameConnection, string> kick = null, TimeSpan? timeout = null)
        => new(permissions, send, () => connections ?? [], kick ?? ((_, _) => { }), TimeProvider.System,
            timeout ?? TimeSpan.FromSeconds(1));

    private sealed class TestPermissions(AccountRole actor, AccountRole target) : IPermissionManager
    {
        public AccountRole ActorRole { get; set; } = actor;
        public AccountRole GetRole(ICharacter character) => GetRole(character.AccountId);
        public AccountRole GetRole(uint accountId) => accountId == 10 ? ActorRole : target;
        public bool CanUse(ICharacter character, GamePermission permission) => PermissionPolicy.Allows(GetRole(character), permission);
        public bool CanModerate(uint actorAccountId, uint targetAccountId)
            => PermissionPolicy.CanModerate(GetRole(actorAccountId), GetRole(targetAccountId));
    }

    private sealed class TestSession(uint id) : ISession
    {
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => id;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) { }
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }

    private sealed class TestOutput : IMessageOutput
    {
        private readonly List<string> _messages = [];
        public IEnumerable<string> Messages => _messages;
        public IEnumerable<string> ErrorMessages => [];
        public void SendMessage(string message) => _messages.Add(message);
        public void SendMessage(ChatType chatType, string message, Color? color = null) => _messages.Add(message);
        public void SendMessage(ICharacter target, string message) => _messages.Add(message);
    }

    private sealed class TestAuditStore : ICommandAuditStore
    {
        public int Completions { get; private set; }
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Start(CommandAuditEntry entry) { }
        public void Complete(CommandAuditEntry entry) { Completions++; Completed.TrySetResult(); }
    }

    private sealed class CommandManagerStub : IModerationManager
    {
        public TaskCompletionSource<ModerationResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ModerationAction Action { get; private set; }
        public uint KickedAccount { get; private set; }
        public Task<ModerationResult> ModerateAsync(Character actor, ModerationTarget target, ModerationAction action,
            ulong durationSeconds, string reason) { Action = action; return Completion.Task; }
        public Task<bool> RefreshAccountAsync(uint accountId) => Task.FromResult(true);
        public Task RefreshLiveAccountsAsync() => Task.CompletedTask;
        public bool TryAdmit(uint accountId, Action admit) { admit(); return true; }
        public bool CanChat(uint accountId) => true;
        public bool TryKick(Character actor, ModerationTarget target, string reason, out string error)
        { KickedAccount = target.AccountId; error = string.Empty; return true; }
        public void CompleteRequest(ModerationResult result) { }
        public void ApplyState(ModerationState state) { }
        public bool TryResolveTarget(string value, out ModerationTarget target) { target = Target(); return true; }
    }
}
