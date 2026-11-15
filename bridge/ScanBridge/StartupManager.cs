using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;

namespace ScanBridge;

/// <summary>
/// "Start with Windows" for both editions:
/// Store (MSIX) → the package's StartupTask (shown in Settings → Apps → Startup);
/// plain exe → HKCU\...\Run via <see cref="Installer"/>.
/// </summary>
public static class StartupManager
{
    public const string TaskId = "Scan2PdfBridgeStartup"; // must match AppxManifest.xml
    private static string OptOutMarker => Path.Combine(AppPaths.DataFolder, "startup-optout");

    /// <summary>True if Windows started us at sign-in.</summary>
    public static bool LaunchedAtStartup()
    {
        if (!AppPaths.IsPackaged) return false;
        try { return AppInstance.GetActivatedEventArgs()?.Kind == ActivationKind.StartupTask; }
        catch { return false; }
    }

    public static bool IsEnabled()
    {
        if (!AppPaths.IsPackaged) return Installer.IsStartWithWindowsEnabled;
        return RunSync(async () =>
        {
            var task = await StartupTask.GetAsync(TaskId);
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        });
    }

    /// <summary>Turns start-at-sign-in on or off. Returns a message for the user if Windows blocked the change.</summary>
    public static string? SetEnabled(bool enabled)
    {
        if (!AppPaths.IsPackaged)
        {
            Installer.SetStartWithWindows(enabled);
            return null;
        }

        return RunSync<string?>(async () =>
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (enabled)
            {
                TryDelete(OptOutMarker);
                var state = await task.RequestEnableAsync();
                AppLog.Write($"Startup task: {state}");
                return state switch
                {
                    StartupTaskState.DisabledByUser =>
                        "Windows has start-up turned off for Scan 2 PDF Bridge.\n\nTurn it on in Settings → Apps → Startup.",
                    StartupTaskState.DisabledByPolicy =>
                        "Start-up apps are turned off by your organization's policy.",
                    _ => null
                };
            }

            task.Disable();
            Directory.CreateDirectory(AppPaths.DataFolder);
            File.WriteAllText(OptOutMarker, "Start with Windows turned off from the tray menu.");
            AppLog.Write("Startup task: disabled by user from tray");
            return null;
        });
    }

    /// <summary>
    /// Store edition: on first run, turn start-at-sign-in on (that's what the bridge is for),
    /// unless the user has turned it off before.
    /// </summary>
    public static void EnsureDefaultOn()
    {
        if (!AppPaths.IsPackaged || File.Exists(OptOutMarker)) return;
        try
        {
            RunSync(async () =>
            {
                var task = await StartupTask.GetAsync(TaskId);
                if (task.State == StartupTaskState.Disabled)
                    AppLog.Write($"Startup task enabled on first run: {await task.RequestEnableAsync()}");
                return true;
            });
        }
        catch (Exception ex) { AppLog.Write("Couldn't enable startup task", ex); }
    }

    // WinRT async calls must not block a UI thread's synchronization context, so run them on the thread pool.
    private static T RunSync<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
