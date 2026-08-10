namespace AAEmu.Game.Services.Health;

public sealed class GameHealthState
{
    private int _isReady;

    public bool IsReady => Volatile.Read(ref _isReady) != 0;

    public void MarkReady()
    {
        Volatile.Write(ref _isReady, 1);
    }

    public void MarkNotReady()
    {
        Volatile.Write(ref _isReady, 0);
    }
}
