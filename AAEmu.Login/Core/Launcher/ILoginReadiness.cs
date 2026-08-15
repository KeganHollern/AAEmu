namespace AAEmu.Login.Core.Launcher;

public interface ILoginReadiness
{
    bool IsInitialized { get; }
    void MarkInitialized();
    void MarkUnavailable();
}

public sealed class LoginReadiness : ILoginReadiness
{
    private int _initialized;

    public bool IsInitialized => Volatile.Read(ref _initialized) == 1;

    public void MarkInitialized() => Interlocked.Exchange(ref _initialized, 1);

    public void MarkUnavailable() => Interlocked.Exchange(ref _initialized, 0);
}
