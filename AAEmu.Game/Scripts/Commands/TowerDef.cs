using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.TowerDefense;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

public class TowerDef : ICommand
{
    public string[] CommandNames { get; set; } = ["towerdef", "tower_def"];

    public void OnLoad() => CommandManager.Instance.Register(CommandNames, this);

    public string GetCommandLineHelp() =>
        "<list|start|next|end> [event-key|tower-def-id] [site-key|reason]";

    public string GetCommandHelpText() =>
        "Controls the authoritative tower-defense runtime. Event keys are shown by 'tower_def list'.";

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length == 0)
        {
            CommandManager.SendDefaultHelpText(this, messageOutput);
            return;
        }

        var manager = TowerDefenseManager.Instance;
        switch (args[0].ToLowerInvariant())
        {
            case "list":
            {
                foreach (var line in manager.GetEventDiagnostics())
                    CommandManager.SendNormalText(this, messageOutput, line);
                var active = manager.GetActiveOccurrences();
                if (active.Count == 0)
                {
                    CommandManager.SendNormalText(this, messageOutput, "No tower-defense occurrences are active.");
                    return;
                }
                foreach (var occurrence in active.OrderBy(value => value.Manifest.Key))
                {
                    var objectives = occurrence.Objectives.Count == 0
                        ? "none"
                        : string.Join(", ", occurrence.Objectives.Values.Select(value =>
                            $"npc {value.TargetId}: {value.Current}/{value.Required}"));
                    CommandManager.SendNormalText(this, messageOutput,
                        $"{occurrence.Manifest.Key} def={occurrence.Definition.Id} site={occurrence.Site.Key} " +
                        $"state={occurrence.Status} step={occurrence.CurrentStepOrdinal} objectives=[{objectives}] " +
                        $"deadline={occurrence.HardDeadlineUtc:O}");
                }
                break;
            }
            case "start":
                if (args.Length < 2)
                {
                    CommandManager.SendErrorText(this, messageOutput,
                        "Usage: tower_def start <event-key|tower-def-id> [site-key]");
                    return;
                }
                var siteKey = args.Length > 2 ? args[2] : null;
                SendResult(messageOutput,
                    manager.StartManual(args[1], siteKey, out var startMessage), startMessage);
                break;
            case "next":
                RunWithTarget(args, messageOutput, manager.AdvanceManual);
                break;
            case "end":
                if (args.Length < 2)
                {
                    CommandManager.SendErrorText(this, messageOutput,
                        "Usage: tower_def end <event-key|tower-def-id> [reason]");
                    return;
                }
                var reason = args.Length > 2 ? string.Join('_', args.Skip(2)) : "gm_cancelled";
                SendResult(messageOutput, manager.EndManual(args[1], reason, out var endMessage), endMessage);
                break;
            default:
                CommandManager.SendErrorText(this, messageOutput, $"Unknown tower-defense action '{args[0]}'.");
                break;
        }
    }

    private delegate bool TargetAction(string target, out string message);

    private void RunWithTarget(string[] args, IMessageOutput output, TargetAction action)
    {
        if (args.Length < 2)
        {
            CommandManager.SendErrorText(this, output, "An event key or tower-def ID is required.");
            return;
        }
        SendResult(output, action(args[1], out var message), message);
    }

    private void SendResult(IMessageOutput output, bool success, string message)
    {
        if (success)
            CommandManager.SendNormalText(this, output, message);
        else
            CommandManager.SendErrorText(this, output, message);
    }
}
