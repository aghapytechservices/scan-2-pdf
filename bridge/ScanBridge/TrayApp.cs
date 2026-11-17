using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace ScanBridge;

/// <summary>
/// System-tray icon so users can see the bridge is running, open the status page, or exit.
/// Runs a WinForms message loop on its own STA thread.
/// </summary>
public static class TrayApp
{
    public static Task RunAsync(string statusUrl, bool showStartupBalloon)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Run(statusUrl, showStartupBalloon);
            }
            catch (Exception ex)
            {
                AppLog.Write("Tray icon failed", ex);
            }
            finally
            {
                done.TrySetResult();
            }
        })
        { Name = "Tray-STA", IsBackground = false };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static Form? _host;

    /// <summary>
    /// Shows the Microsoft Store rating dialog (Store edition). Safe to call from any thread:
    /// it runs on the tray thread, which owns the window the dialog is parented to.
    /// </summary>
    public static Task<string> RequestStoreReviewAsync()
    {
        var host = _host;
        if (host is null || !StoreReview.IsAvailable) return Task.FromResult("unavailable");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeginInvoke(new Action(async () =>
        {
            try
            {
                host.Activate();
                tcs.TrySetResult(await StoreReview.RequestAsync(host.Handle));
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }));
        return tcs.Task;
    }

    private static void Run(string statusUrl, bool showStartupBalloon)
    {
        // Invisible window: parent for the Store dialog, and a target to run UI work on this thread.
        _host = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(1, 1),
            Opacity = 0
        };
        _host.Show();
        _host.Hide();

        using var icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Scan 2 PDF Bridge — running",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };

        var menu = icon.ContextMenuStrip;
        menu.Items.Add(new ToolStripMenuItem($"Scan 2 PDF Bridge v{Version}") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem($"Listening on {new Uri(statusUrl).Authority}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open status page", null, (_, _) => OpenUrl(statusUrl));
        menu.Items.Add("Open log folder", null, (_, _) => OpenFolder(AppLog.Folder));
        var startWithWindows = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        try { startWithWindows.Checked = StartupManager.IsEnabled(); } catch { /* unavailable */ }
        var syncing = false;
        startWithWindows.CheckedChanged += (_, _) =>
        {
            if (syncing) return;
            try
            {
                var problem = StartupManager.SetEnabled(startWithWindows.Checked);
                if (problem is not null) ShowInfo(problem);
            }
            catch (Exception ex)
            {
                AppLog.Write("Couldn't change Start with Windows", ex);
                ShowError("Couldn't change the Start with Windows setting.\n\n" + ex.Message);
            }
            // Reflect what Windows actually did (e.g. blocked by the user's Startup settings).
            syncing = true;
            try { startWithWindows.Checked = StartupManager.IsEnabled(); } catch { }
            syncing = false;
        };
        menu.Items.Add(startWithWindows);

        // ---- Updates ----
        menu.Items.Add(new ToolStripSeparator());
        var installUpdate = new ToolStripMenuItem("Install update") { Visible = false, Font = new Font(menu.Font, FontStyle.Bold) };
        installUpdate.Click += (_, _) => { if (Updater.Available is { } r) InstallUpdate(icon, r); };
        menu.Items.Add(installUpdate);
        menu.Items.Add("Check for updates", null, async (_, _) =>
        {
            if (!Updater.IsAvailable) { OpenUrl(Updater.StorePage); return; } // Store edition: the Store updates it
            await CheckForUpdates(icon, manual: true);
        });
        if (Updater.IsAvailable)
        {
            var autoCheck = new ToolStripMenuItem("Check for updates automatically")
            {
                CheckOnClick = true,
                Checked = UpdateState.Load().AutoCheck
            };
            autoCheck.CheckedChanged += (_, _) =>
            {
                var st = UpdateState.Load();
                st.AutoCheck = autoCheck.Checked;
                st.Save();
            };
            menu.Items.Add(autoCheck);
        }
        menu.Opening += (_, _) =>
        {
            installUpdate.Visible = Updater.Available is not null;
            if (Updater.Available is { } r) installUpdate.Text = $"Install update v{r.Display}";
        };
        if (StoreReview.IsAvailable)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Rate in Microsoft Store", null, async (_, _) =>
            {
                try { await StoreReview.RequestAsync(_host!.Handle); }
                catch (Exception ex) { AppLog.Write("Rate menu failed", ex); }
            });
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            var answer = MessageBox.Show(
                "Stop the Scan 2 PDF Bridge?\n\nThe Outlook add-in won't be able to scan until it's started again.",
                "Scan 2 PDF Bridge", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes) Application.ExitThread();
        });

        icon.DoubleClick += (_, _) => OpenUrl(statusUrl);
        icon.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonAction ?? (() => OpenUrl(statusUrl));
            _balloonAction = null;
            action();
        };

        if (!AnnounceFinishedUpdate(icon) && showStartupBalloon)
        {
            Balloon(icon, "Scan 2 PDF Bridge is running",
                "It runs in the background (see the tray icon). Click here to open the status page.", null);
        }

        // Standalone edition: look for a new release shortly after start, then about once a day.
        // (An hourly tick that checks the last-check time copes with sleep and long uptimes.)
        using var updateTimer = new System.Windows.Forms.Timer { Interval = (int)Updater.FirstCheckDelay.TotalMilliseconds };
        if (Updater.IsAvailable)
        {
            updateTimer.Tick += async (_, _) =>
            {
                updateTimer.Interval = (int)TimeSpan.FromHours(1).TotalMilliseconds;
                var st = UpdateState.Load();
                if (!st.AutoCheck) return;
                var due = !_checkedThisSession || st.LastCheckUtc is not { } last || DateTime.UtcNow - last >= Updater.CheckInterval;
                if (!due) return;
                _checkedThisSession = true;
                await CheckForUpdates(icon, manual: false);
            };
            updateTimer.Start();
        }

        Application.Run(); // message loop until Exit
        icon.Visible = false;
    }

    // ---------------- updates ----------------

    private static Action? _balloonAction;
    private static bool _checkedThisSession;
    private static string? _notifiedVersion;
    private static bool _updating;

    private static void Balloon(NotifyIcon icon, string title, string text, Action? onClick, ToolTipIcon kind = ToolTipIcon.Info)
    {
        _balloonAction = onClick;
        icon.ShowBalloonTip(5000, title, text, kind);
    }

    private static async Task CheckForUpdates(NotifyIcon icon, bool manual)
    {
        Updater.ReleaseInfo? release;
        try
        {
            release = await Updater.CheckAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write("Update check failed", ex);
            if (manual)
            {
                var open = MessageBox.Show(
                    "Couldn't check for updates. Check your internet connection and try again.\n\n" +
                    $"{ex.Message}\n\nOpen the downloads page instead?",
                    "Scan 2 PDF Bridge", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (open == DialogResult.Yes) OpenUrl(Updater.ReleasesPage);
            }
            return;
        }

        if (release is null)
        {
            if (manual) ShowInfo($"You have the latest version (v{Version}).");
            return;
        }

        if (manual)
        {
            OfferUpdate(icon, release);
            return;
        }

        // Automatic check: one quiet notification per version, unless the user skipped it.
        if (UpdateState.Load().SkippedVersion == release.Display || _notifiedVersion == release.Display) return;
        _notifiedVersion = release.Display;
        Balloon(icon, $"Scan 2 PDF Bridge v{release.Display} is available",
            $"You have v{Version}. Click here to install it (takes a few seconds).",
            () => OfferUpdate(icon, release));
    }

    private static void OfferUpdate(NotifyIcon icon, Updater.ReleaseInfo release)
    {
        var answer = MessageBox.Show(
            $"Scan 2 PDF Bridge v{release.Display} is available. You have v{Version}.\n\n" +
            "Install it now? The bridge restarts in a few seconds; your settings are kept.\n\n" +
            "Yes = install now    No = skip this version    Cancel = remind me later",
            "Scan 2 PDF Bridge", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

        if (answer == DialogResult.Yes) InstallUpdate(icon, release);
        else if (answer == DialogResult.No)
        {
            var st = UpdateState.Load();
            st.SkippedVersion = release.Display;
            st.Save();
        }
    }

    private static async void InstallUpdate(NotifyIcon icon, Updater.ReleaseInfo release)
    {
        if (_updating) return;
        if (ScanProgress.IsActive)
        {
            ShowInfo("A scan is in progress. Install the update when it's finished.");
            return;
        }

        _updating = true;
        try
        {
            icon.Text = "Scan 2 PDF Bridge — updating…";
            Balloon(icon, $"Updating to v{release.Display}…", "Downloading the update. The bridge restarts when it's ready.", null);

            var st = UpdateState.Load();
            st.PendingVersion = release.Display;
            st.SkippedVersion = null;
            st.Save();

            await Updater.DownloadAndStartInstallAsync(release);
            Application.ExitThread(); // the new copy waits for this process to exit, then installs and restarts
        }
        catch (Exception ex)
        {
            AppLog.Write("Update failed", ex);
            var st = UpdateState.Load();
            st.PendingVersion = null;
            st.Save();
            icon.Text = "Scan 2 PDF Bridge — running";
            var open = MessageBox.Show(
                $"Couldn't install the update.\n\n{ex.Message}\n\nOpen the downloads page to update by hand?",
                "Scan 2 PDF Bridge", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (open == DialogResult.Yes) OpenUrl(release.PageUrl);
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>After an update, the new copy says so once. Returns true if it showed that notification.</summary>
    private static bool AnnounceFinishedUpdate(NotifyIcon icon)
    {
        var st = UpdateState.Load();
        if (st.PendingVersion is null) return false;
        var updated = st.PendingVersion == Version;
        if (!updated) AppLog.Write($"Update to {st.PendingVersion} didn't complete; still running {Version}");
        st.PendingVersion = null;
        st.Save();
        if (!updated) return false;

        AppLog.Write($"Updated to {Version}");
        Balloon(icon, $"Scan 2 PDF Bridge updated to v{Version}",
            "Click here to see what's new.", () => OpenUrl(Updater.ReleasesPage));
        return true;
    }

    public static string Version =>
        typeof(TrayApp).Assembly.GetName().Version?.ToString(3) ?? "1.0";

    private static Icon LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (path is not null && Icon.ExtractAssociatedIcon(path) is { } ico) return ico;
        }
        catch { /* fall through */ }
        return SystemIcons.Application;
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Write("Couldn't open " + url, ex); }
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { AppLog.Write("Couldn't open " + folder, ex); }
    }

    public static void ShowInfo(string message) =>
        MessageBox.Show(message, "Scan 2 PDF Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);

    /// <summary>Yes → true, No → false, Cancel/close → null.</summary>
    public static bool? Ask(string message) =>
        MessageBox.Show(message, "Scan 2 PDF Bridge", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question) switch
        {
            DialogResult.Yes => true,
            DialogResult.No => false,
            _ => null
        };

    public static void ShowError(string message) =>
        MessageBox.Show(message, "Scan 2 PDF Bridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
