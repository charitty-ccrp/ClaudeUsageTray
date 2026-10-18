using ClaudeUsageTray.Utilities;

namespace ClaudeUsageTray.Services;

/// <summary>
/// Watches files and calls back at most once per <c>delay</c>: the first change starts a timer and every change
/// until it fires is folded into that one callback. Never throws if the folder is missing; it simply stays idle.
/// Callbacks run on a thread-pool thread.
/// </summary>
public sealed class DebouncedFileWatcher : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly System.Threading.Timer _timer;
    private readonly TimeSpan _delay;
    private int _pending;

    public DebouncedFileWatcher(string directory, string filter, bool includeSubdirectories, TimeSpan delay, Action onChanged)
    {
        _delay = delay;
        _timer = new System.Threading.Timer(_ =>
        {
            Interlocked.Exchange(ref _pending, 0);
            try { onChanged(); }
            catch (Exception ex) { Logger.Warn($"File watcher callback failed: {ex.GetType().Name}"); }
        });

        try
        {
            if (!Directory.Exists(directory)) return;
            _watcher = new FileSystemWatcher(directory, filter)
            {
                IncludeSubdirectories = includeSubdirectories,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Changed += (_, _) => Signal();
            _watcher.Created += (_, _) => Signal();
            _watcher.Renamed += (_, _) => Signal();
            _watcher.Error += (_, e) => Logger.Warn($"File watcher error: {e.GetException().GetType().Name}");
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Logger.Warn($"Could not watch {filter}: {ex.GetType().Name}");
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    public bool IsWatching => _watcher is not null;

    /// <summary>Same as a file change; public so tests can drive it without touching the disk.</summary>
    public void Signal()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 0)
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _timer.Dispose();
    }
}
