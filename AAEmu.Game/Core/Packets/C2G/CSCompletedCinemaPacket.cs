using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSCompletedCinemaPacket() : GamePacket(CSOffsets.CSCompletedCinemaPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        // Empty struct
        Logger.Warn("CompletedCinema");

        WorldManager.ResendVisibleObjectsToCharacter(Connection.ActiveChar);
        Connection.ActiveChar.Events.OnCinemaEnded(Connection.ActiveChar, new OnCinemaEndedArgs { CinemaId = Connection.ActiveChar.CurrentlyPlayingCinemaId });

        // Clear the in-progress marker so a later cinema is not mistaken for one already playing.
        // Quest cinemas self-clear in QuestActObjCinema.OnCinemaEnded, but skill-driven ones
        // (CinemalEffect) had no owner to reset it. (aaemu-cluster#92)
        Connection.ActiveChar.CurrentlyPlayingCinemaId = 0;
    }
}
