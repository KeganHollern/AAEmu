using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

public class PingPosition : ICommand
{
    public string[] CommandNames { get; set; } = ["pingpos", "ping_pos", "pingposition"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp()
    {
        return "";
    }

    public string GetCommandHelpText()
    {
        return "Displays information about your pinged position. (map marker)";
    }

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (character.LocalPingPosition.X == 0f && character.LocalPingPosition.Y == 0f)
        {
            CommandManager.SendErrorText(this, messageOutput,
                "Make sure you marked a location on the map WHILE IN A PARTY OR RAID, using this command.\n" +
                "If required, you can use the /soloparty command to make a party of just yourself.|r");
        }
        else
        {
            var surface = CommandSurfaceResult.Resolve(character.ParentWorld.Template,
                character.LocalPingPosition.AsPositionVector());
            CommandManager.SendNormalText(this, messageOutput, BuildReport(surface));
        }
    }

    internal static string BuildReport(CommandSurfaceResult surface) =>
        $"|cFFFFFFFFX:{CommandSurfaceResult.Format(surface.QueryPosition.X)} " +
        $"Y:{CommandSurfaceResult.Format(surface.QueryPosition.Y)} " +
        $"referenceZ:{CommandSurfaceResult.Format(surface.QueryPosition.Z)} {surface.FormatHeights()}|r";
}
