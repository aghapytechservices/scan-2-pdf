namespace ScanBridge;

/// <summary>Live progress of the scan in progress, for the add-in to show while it waits.</summary>
public static class ScanProgress
{
    private static readonly object Gate = new();
    private static bool _active;
    private static int _pages;
    private static DateTime _started;
    private static DateTime _lastPage;

    public static void Start()
    {
        lock (Gate) { _active = true; _pages = 0; _started = _lastPage = DateTime.UtcNow; }
    }

    /// <summary>Records the total pages scanned so far and logs how long the latest page took.</summary>
    public static void Pages(int total)
    {
        lock (Gate)
        {
            if (total <= _pages) return;
            var now = DateTime.UtcNow;
            AppLog.Write($"Page {total} received after {(now - _lastPage).TotalSeconds:0.0} s ({(now - _started).TotalSeconds:0} s total)");
            _pages = total;
            _lastPage = now;
        }
    }

    public static void Finish()
    {
        lock (Gate) { _active = false; }
    }

    public static object Snapshot()
    {
        lock (Gate)
        {
            return new
            {
                active = _active,
                pages = _pages,
                seconds = _active ? (int)(DateTime.UtcNow - _started).TotalSeconds : 0
            };
        }
    }
}
