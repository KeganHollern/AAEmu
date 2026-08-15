namespace AAEmu.Login.Core.Launcher;

public interface IClientCompactProvider
{
    bool IsAvailable { get; }
    string FilePath { get; }
    ClientCompactManifestResponse Manifest { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
}
