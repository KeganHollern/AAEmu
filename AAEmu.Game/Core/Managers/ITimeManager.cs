using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Game.TowerDefs;

namespace AAEmu.Game.Core.Managers;

public interface ITimeManager : IObservable<float>
{
    event Action<WorldClockTick> WorldClockChanged;
    float GetTime { get; }
    float ClientSpeed { get; }
    WorldClockSnapshot GetSnapshot();
    IDisposable Subscribe(GameConnection connection, IObserver<float> observer);
    void Start();
    void Stop();
    float Get();
    bool Set(float hours);
    void OnTimeOfDayChange(float newTime, float oldTime);
}
