namespace ScanBridge;

/// <summary>Live progress of the scan in progress, for the add-in to show while it waits.</summary>
public static class ScanProgress
{
    private static readonly object Gate = new();
    private static bool _active;
    private static int _pages;
    private static DateTime _started;
    private static DateTime _lastPage;
    private static CancellationTokenSource _stop = new();

    /// <summary>Signalled when the user presses Stop in the add-in.</summary>
    public static CancellationToken StopToken { get { lock (Gate) return _stop.Token; } }
    public static bool StopRequested => StopToken.IsCancellationRequested;

    /// <summary>Asks the scan in progress to stop. Returns false if no scan is running.</summary>
    public static bool RequestStop()
    {
        CancellationTokenSource cts;
        lock (Gate)
        {
            if (!_active || _stop.IsCancellationRequested) return _active;
            cts = _stop;
        }
        AppLog.Write("Stop requested by the user.");
        cts.Cancel(); // outside the lock: cancellation callbacks may report progress
        return true;
    }

    /// <summary>The note returned with the pages kept after Stop.</summary>
    public static string StoppedNote(int pages) => pages == 0
        ? "Scan stopped."
        : $"Scan stopped. Kept the {pages} page{(pages == 1 ? "" : "s")} scanned before you pressed Stop.";

    public static bool IsActive { get { lock (Gate) return _active; } }

    public static void Start()
    {
        lock (Gate)
        {
            _active = true; _pages = 0; _started = _lastPage = DateTime.UtcNow;
            _stop.Dispose();
            _stop = new CancellationTokenSource();
        }
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
                stopping = _active && _stop.IsCancellationRequested,
                pages = _pages,
                seconds = _active ? (int)(DateTime.UtcNow - _started).TotalSeconds : 0
            };
        }
    }
}
