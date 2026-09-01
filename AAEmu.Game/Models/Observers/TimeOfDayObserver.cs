using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Models.Observers;

public class TimeOfDayObserver(Character owner, float speed) : IObserver<float>
{
    public void OnCompleted()
    {
        throw new NotImplementedException();
    }

    public void OnError(Exception error)
    {
        throw new NotImplementedException();
    }

    public void OnNext(float value)
    {
        // The r208022 simple packet ignores clock corrections below 0.1 hours.
        owner.SendPacket(new SCDetailedTimeOfDayPacket(value, speed));
    }
}
