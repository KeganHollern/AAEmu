using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Account;
using AAEmu.Game.Utils.Scripts;
using NLog;

namespace AAEmu.Game.Scripts.Commands;

[CommandPermission(GamePermission.StaffCommands)]
public class TickDoodad : ICommand
{
    public string[] CommandNames { get; set; } = ["tickdoodad", "tick_doodad"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp()
    {
        return "<objId>";
    }

    public string GetCommandHelpText()
    {
        return "Moves a doodad onto it's next Phase using <objId> inside a <radius> range.";
    }

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length < 1)
        {
            CommandManager.SendDefaultHelpText(this, messageOutput);
            return;
        }

        var radius = 30f;
        if (!uint.TryParse(args[0], out var unitId))
        {
            CommandManager.SendErrorText(this, messageOutput, $"Parse error unitId|r");
            return;
        }

        var myDoodads = WorldManager.GetAround<Doodad>(character, radius);
        var complete = CommandAuditContext.DeferResult();
        _ = TickAsync(myDoodads, unitId, messageOutput, complete);
    }

    private async Task TickAsync(IEnumerable<Doodad> doodads, uint unitId, IMessageOutput messageOutput,
        Action<string, string> complete)
    {
        try
        {
            var executions = new List<Task>();
            foreach (var doodad in doodads)
            {
                if (doodad.TemplateId == unitId && doodad.FuncTask is { } functionTask)
                {
                    CommandAuditContext.RecordTarget(0, objectId: doodad.ObjId);
                    functionTask.Cancel();
                    executions.Add(Task.Run(functionTask.ExecuteAsync));
                }
            }

            await Task.WhenAll(executions);
            CommandManager.SendNormalText(this, messageOutput,
                $"Phased {executions.Count} Doodad(s) with TemplateID {unitId} - @DOODAD_NAME({unitId})");
            complete("completed", $"Completed {executions.Count} doodad phase tasks.");
        }
        catch (Exception exception)
        {
            complete("unconfirmed", $"Doodad phase task failed: {exception.GetType().Name}");
            LogManager.GetCurrentClassLogger().Error(exception, "Doodad phase command failed");
            messageOutput.SendMessage("Doodad phase command failed. Check the server log.");
        }
    }
}
