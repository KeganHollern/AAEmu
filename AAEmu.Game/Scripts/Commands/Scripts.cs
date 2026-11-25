using System.Drawing;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Tasks;
using AAEmu.Game.Models.Account;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

[CommandPermission(GamePermission.ManageServer)]
public class Scripts : ICommand
{
    public string[] CommandNames { get; set; } = ["scripts"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp()
    {
        return "<action>";
    }

    public string GetCommandHelpText()
    {
        return "Does script related actions. Allowed <action> are: reload, reboot, save, shutdown";
    }

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length == 0)
        {
            character.SendMessage("[Scripts] Using: " + CommandManager.CommandPrefix + "scripts <action>");
            //character.SendMessage("[Scripts] Action: reload");
            return;
        }

        switch (args[0])
        {
            case "reload":
            case "reboot":
                CommandManager.Instance.Clear();
                if (ScriptCompiler.Compile())
                {
                    CommandManager.SendNormalText(this, messageOutput, "[Scripts] Reload - Success");
                }
                else
                {
                    CommandManager.SendErrorText(this, messageOutput,
                        "Reload - There were errors, please check the server logs for details !");
                }

                break;
            case "save":
                if (SaveManager.Instance.DoSave())
                {
                    CommandManager.SendNormalText(this, messageOutput, "Save - Done saving user database");
                }
                else
                {
                    CommandManager.SendErrorText(this, messageOutput,
                        "Save - Failed saving user database, was possible already in the process of saving, please check server console for details.");
                }

                break;
            case "reloadslavepoints":
                SlaveGameData.Instance.LoadSlaveAttachmentPointLocations();
                CommandManager.SendNormalText(this, messageOutput,
                    "Slave Attachment Point Locations reloaded from .json file");
                break;
            case "shutdown":
                uint minutes = 30;
                var doCancel = false;
                if (args.Length > 1)
                {
                    if (args[1].Equals("cancel", StringComparison.CurrentCultureIgnoreCase))
                    {
                        doCancel = true;
                    }
                    else if (args[1].Equals("now", StringComparison.CurrentCultureIgnoreCase))
                    {
                        minutes = 0;
                    }
                    else if (uint.TryParse(args[1], out var shutdownTimeMinutes))
                    {
                        minutes = shutdownTimeMinutes;
                    }
                }

                var when = minutes switch { 0 => "now", 1 => "in 1 minute", _ => $"in {minutes} minutes" };

                var result = SaveManager.Instance.SetShutdownTime(doCancel ? null : DateTime.UtcNow.AddMinutes(minutes));
                switch (result)
                {
                    case ShutdownScheduleResult.Scheduled:
                        CommandManager.SendNormalText(this, messageOutput,
                            $"Shutdown sequence started. The server will shut down {when}.");
                        break;
                    case ShutdownScheduleResult.Rescheduled:
                        CommandManager.SendNormalText(this, messageOutput,
                            $"Shutdown was already in progress. The server will shut down {when}.");
                        // The countdown sends its own immediate final notice.
                        if (minutes > 0)
                            WorldManager.Instance.BroadcastPacketToServer(new SCNoticeMessagePacket(3, Color.Red, 10000,
                                $"The server will shut down {when}."));
                        break;
                    case ShutdownScheduleResult.Cancelled:
                        CommandManager.SendNormalText(this, messageOutput, "Shutdown cancelled.");
                        WorldManager.Instance.BroadcastPacketToServer(new SCNoticeMessagePacket(3, Color.Aqua, 10000,
                            "The server shutdown has been cancelled!"));
                        break;
                    case ShutdownScheduleResult.NoCountdown:
                        CommandManager.SendErrorText(this, messageOutput, "No shutdown to cancel");
                        break;
                    case ShutdownScheduleResult.SaveFailed:
                        CommandManager.SendNormalText(this, messageOutput,
                            "Shutdown cancelled because there were save errors, if you want to shutdown regardless without saving, use |nn;/scripts forcedshutdown|r instead.");
                        break;
                    case ShutdownScheduleResult.Stopping:
                        CommandManager.SendErrorText(this, messageOutput, "The server is already stopping.");
                        break;
                }
                break;
            case "forcedshutdown":
                WorldManager.Instance.BroadcastPacketToServer(new SCNoticeMessagePacket(3, Color.Magenta, 15000,
                    "The server is shutting down right now!"));
                CommandManager.SendNormalText(this, messageOutput, "Shutting down immediately!");
                CommandAuditContext.CompleteBeforeExit("Forced shutdown accepted.");
                Environment.Exit(-2); // Manual Forced Shutdown (-2)
                break;
            default:
                CommandManager.SendErrorText(this, messageOutput, "Undefined action...");
                break;
        }
    }
}
