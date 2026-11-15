namespace ScanBridge;

/// <summary>
/// WIA automation is a COM API and is most reliable on a single-threaded apartment (STA) thread.
/// Each scan job runs on its own dedicated STA thread.
/// </summary>
public static class StaRunner
{
    public static Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = "WIA-STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
