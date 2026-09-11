using System.Drawing;
using System.Reflection;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Account;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Scripts.SubCommands.Doodads;
using AAEmu.Game.Scripts.SubCommands.Npcs;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

namespace AAEmu.UnitTests.Game.Utils.Scripts;

public class DeferredWorldCommandAuditTests
{
    [Test]
    [Arguments("npc-save")]
    [Arguments("doodad-save")]
    [Arguments("doodad-remove")]
    [Arguments("doodad-removes")]
    public async Task BackgroundFailure_CompletesAfterHandlerReturnAndContextDispose(string commandName)
    {
        SubCommandBase command = commandName switch
        {
            "npc-save" => new NpcSaveSubCommand(),
            "doodad-save" => new DoodadSaveSubCommand(),
            "doodad-remove" => new DoodadRemoveSubCommand(),
            _ => new DoodadRemovesSubCommand()
        };
        var store = new Store();
        using var release = new ManualResetEventSlim();
        using (var context = new CommandAuditContext(new CommandAuditEntry { ActorRole = AccountRole.Admin }, store))
        {
            command.Execute(null, "", new BlockedParameters(release), new Output());
            await Assert.That(context.IsDeferred).IsTrue();
            await Assert.That(store.Completions).IsEqualTo(0);
        }

        release.Set();
        var entry = await store.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(entry.Result).IsEqualTo("unconfirmed");
        await Assert.That(entry.Detail).Contains(nameof(InvalidOperationException));
        await Assert.That(entry.CompletedAt).IsNotNull();
        await Assert.That(store.Completions).IsEqualTo(1);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task BusyWorldSave_CompletesAsRejected(bool npc)
    {
        SubCommandBase command = npc ? new NpcSaveSubCommand() : new DoodadSaveSubCommand();
        command.GetType().GetField("_isSavingInProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(command, true);
        var store = new Store();
        using (new CommandAuditContext(new CommandAuditEntry { ActorRole = AccountRole.Admin }, store))
            command.Execute(null, "", new Dictionary<string, ParameterValue>(), new Output());

        var entry = await store.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(entry.Result).IsEqualTo("rejected");
        await Assert.That(entry.Detail).IsEqualTo("Save operation is already in progress.");
        await Assert.That(store.Completions).IsEqualTo(1);
    }

    private sealed class BlockedParameters(ManualResetEventSlim release) : Dictionary<string, ParameterValue>,
        IDictionary<string, ParameterValue>
    {
        bool IDictionary<string, ParameterValue>.TryGetValue(string key, out ParameterValue value)
        {
            if (!release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Test did not release the command.");
            throw new InvalidOperationException("Injected background failure.");
        }
    }

    private sealed class Store : ICommandAuditStore
    {
        private int _completions;
        public int Completions => Volatile.Read(ref _completions);
        public TaskCompletionSource<CommandAuditEntry> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Start(CommandAuditEntry entry) { }
        public void Complete(CommandAuditEntry entry)
        {
            Interlocked.Increment(ref _completions);
            Finished.TrySetResult(entry);
        }
    }

    private sealed class Output : IMessageOutput
    {
        public IEnumerable<string> Messages => [];
        public IEnumerable<string> ErrorMessages => [];
        public void SendMessage(string message) { }
        public void SendMessage(ChatType type, string message, Color? color = null) { }
        public void SendMessage(ICharacter target, string message) { }
    }
}
