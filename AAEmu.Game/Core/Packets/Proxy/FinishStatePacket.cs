using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;

namespace AAEmu.Game.Core.Packets.Proxy;

public class FinishStatePacket() : GamePacket(PPOffsets.FinishStatePacket, 2)
{
    private readonly bool[] _scAccountInitPacket = [false, true];
    private readonly byte[] _scLevelRestrictionInitPacket = [0, 15, 15, 15, 0, 0, 15, 0, 0, 0, 0, 0, 0, 0, 15];

    public override void Read(PacketStream stream)
    {
        var state = stream.ReadInt32();

        switch (state)
        {
            case 0:
                Connection.SendPacket(new ChangeStatePacket(1));
                // Connection.SendPacket(new SCHackGuardRetAddrsRequestPacket(false, false)); // HG_REQ? // TODO - config files
                var levelname = string.Empty;
                if (Connection.ActiveChar != null)
                {
                    levelname = ZoneManager.Instance.GetZoneByKey(Connection.ActiveChar.Transform.ZoneId)?.Name ?? "w_hanuimaru_1";
                }
                else
                {
                    levelname = "w_hanuimaru_1";
                }
                Connection.SendPacket(new SetGameTypePacket(levelname, 0, 1)); // TODO - level
                Connection.SendPacket(new SCInitialConfigPacket());

                // Web URLs for the embedded Awesomium browser (Get Credits popup,
                // wiki, web shop). The client treats these as folder bases and
                // appends its own paths, e.g. the wiki opens platformUrl + "/login".
                // Original Trion platformUrl was the purchase-credits-flow page.
                var trionWeb = AppConfiguration.Instance.TrionWeb;
                Connection.SendPacket(new SCTrionConfigPacket(
                    trionWeb.Activate,
                    trionWeb.AuthUrl,
                    trionWeb.PlatformUrl,
                    trionWeb.CommerceUrl)
                );
                Connection.SendPacket(new SCAccountInfoPacket(
                        (int)Connection.Payment.Method,
                        Connection.Payment.Location,
                        Connection.Payment.StartTime,
                        Connection.Payment.EndTime)
                );
                Connection.SendPacket(new SCChatSpamDelayPacket());
                Connection.SendPacket(new SCAccountAttributeConfigPacket(_scAccountInitPacket)); // TODO
                Connection.SendPacket(new SCLevelRestrictionConfigPacket(10, 10, 10, 10, 10, _scLevelRestrictionInitPacket)); // TODO - config files
                break;
            case 1:
                Connection.SendPacket(new ChangeStatePacket(2));
                break;
            case 2:
                Connection.SendPacket(new ChangeStatePacket(3));
                break;
            case 3:
            case 4:
            case 5:
            case 6:
                Connection.SendPacket(new ChangeStatePacket(state + 1));
                break;
            case 7:
                Connection.SendPacket(new SCUpdatePremiumPointPacket(1, 1, 1));
                break;
            default:
                Logger.Info("Unknown state: {0}", state);
                break;
        }
    }
}
