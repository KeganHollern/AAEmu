using AAEmu.ContentStudio.Core.Models;
using AAEmu.ContentStudio.Core.Services;

namespace AAEmu.ContentStudio.Designer;

public sealed class DesignerWorkspace : IDisposable
{
    private readonly object _watchLock = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private long _revision;

    public DesignerWorkspace(IWebHostEnvironment environment)
    {
        ConfigurationPath = Environment.GetEnvironmentVariable("AAEMU_CONTENT_CONFIG")
            ?? Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", "Content", "content-studio.json"));
    }

    public string ConfigurationPath { get; set; }

    public event EventHandler<WorkspaceChangedEventArgs>? Changed;
    public long Revision => Interlocked.Read(ref _revision);

    public StudioConfiguration LoadConfiguration()
    {
        var configuration = new ProjectRepository().LoadConfiguration(ConfigurationPath);
        EnsureWatcher(configuration.ProjectPath);
        return configuration;
    }

    private void EnsureWatcher(string projectPath)
    {
        var directory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrWhiteSpace(directory)) return;
        directory = Path.GetFullPath(directory);
        lock (_watchLock)
        {
            if (_watcher?.Path.Equals(directory, StringComparison.OrdinalIgnoreCase) == true) return;
            _watcher?.Dispose();
            _watcher = new FileSystemWatcher(directory, "*.json")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += QueueChange;
            _watcher.Created += QueueChange;
            _watcher.Deleted += QueueChange;
            _watcher.Renamed += QueueChange;
        }
    }

    private void QueueChange(object sender, FileSystemEventArgs args)
    {
        if (Path.GetFileName(args.FullPath).StartsWith(".", StringComparison.Ordinal)) return;
        lock (_watchLock)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ => PublishChange(args.FullPath, args.ChangeType), null, 250, Timeout.Infinite);
        }
    }

    private void PublishChange(string path, WatcherChangeTypes changeType)
    {
        var revision = Interlocked.Increment(ref _revision);
        Changed?.Invoke(this, new WorkspaceChangedEventArgs(path, changeType, revision, DateTimeOffset.Now));
    }

    public void Dispose()
    {
        lock (_watchLock)
        {
            _watcher?.Dispose();
            _debounce?.Dispose();
        }
    }
}

public sealed record WorkspaceChangedEventArgs(string Path, WatcherChangeTypes ChangeType, long Revision, DateTimeOffset ChangedAt);
