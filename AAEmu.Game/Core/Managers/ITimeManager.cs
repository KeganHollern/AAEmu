using AAEmu.Game.Core.Network.Connections;

namespace AAEmu.Game.Core.Managers;

public interface ITimeManager : IObservable<float>
{
    float GetTime { get; }
    float ClientSpeed { get; }
    IDisposable Subscribe(GameConnection connection, IObserver<float> observer);
    void Start();
    void Stop();
    float Get();
    bool Set(float hours);
    void OnTimeOfDayChange(float newTime, float oldTime);
}
