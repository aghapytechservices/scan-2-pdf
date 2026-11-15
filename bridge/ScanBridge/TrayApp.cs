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

    private static void Run(string statusUrl, bool showStartupBalloon)
    {
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
        try { startWithWindows.Checked = Installer.IsStartWithWindowsEnabled; } catch { /* registry unavailable */ }
        startWithWindows.CheckedChanged += (_, _) =>
        {
            try { Installer.SetStartWithWindows(startWithWindows.Checked); }
            catch (Exception ex)
            {
                AppLog.Write("Couldn't change Start with Windows", ex);
                ShowError("Couldn't change the Start with Windows setting.\n\n" + ex.Message);
            }
        };
        menu.Items.Add(startWithWindows);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            var answer = MessageBox.Show(
                "Stop the Scan 2 PDF Bridge?\n\nThe Outlook add-in won't be able to scan until it's started again.",
                "Scan 2 PDF Bridge", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes) Application.ExitThread();
        });

        icon.DoubleClick += (_, _) => OpenUrl(statusUrl);
        icon.BalloonTipClicked += (_, _) => OpenUrl(statusUrl);

        if (showStartupBalloon)
        {
            icon.ShowBalloonTip(5000, "Scan 2 PDF Bridge is running",
                "It runs in the background (see the tray icon). Click here to open the status page.",
                ToolTipIcon.Info);
        }

        Application.Run(); // message loop until Exit
        icon.Visible = false;
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
