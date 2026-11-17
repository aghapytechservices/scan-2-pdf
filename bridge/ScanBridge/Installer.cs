using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace ScanBridge;

/// <summary>
/// Per-user install without PowerShell or admin rights:
/// copies the bridge to %LOCALAPPDATA%\Scan2PdfBridge and starts it at sign-in via
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run (works for local, Microsoft and work accounts).
/// </summary>
public static class Installer
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Scan2PdfBridge";
    private const string LegacyTaskName = "Scan 2 PDF Bridge";
    public const string ExeName = "Scan2PdfBridge.exe";
    private static readonly string[] CompanionFiles = ["LICENSE", "install.cmd", "uninstall.cmd", "install.ps1", "uninstall.ps1"];

    public static string InstallDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scan2PdfBridge");

    public static string InstalledExe => Path.Combine(InstallDir, ExeName);

    private static string CurrentExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ExeName);

    public static bool IsRunningFromInstallDir =>
        string.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'),
                      Path.GetFullPath(InstallDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    // ---------------- Start with Windows ----------------

    public static bool IsStartWithWindowsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string;
        }
    }

    public static void SetStartWithWindows(bool enabled, string? exePath = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
            key.SetValue(RunValueName, $"\"{exePath ?? CurrentExe}\" --background", RegistryValueKind.String);
        else
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        AppLog.Write($"Start with Windows: {(enabled ? "on" : "off")}");
    }

    // ---------------- Install / update ----------------

    /// <summary>
    /// Copies this exe (and its settings) into the install folder, enables start-at-sign-in,
    /// removes the old scheduled task, and returns the installed exe path.
    /// Any running copy is stopped first so its files can be replaced (this doubles as the updater).
    /// </summary>
    public static string Install()
    {
        StopOtherInstances();
        Directory.CreateDirectory(InstallDir);

        CopyWithRetry(CurrentExe, InstalledExe);
        foreach (var f in CompanionFiles)
        {
            var src = Path.Combine(AppContext.BaseDirectory, f);
            if (File.Exists(src)) File.Copy(src, Path.Combine(InstallDir, f), overwrite: true);
        }
        MergeSettings(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), Path.Combine(InstallDir, "appsettings.json"));

        SetStartWithWindows(true, InstalledExe);
        RemoveLegacyScheduledTask();
        AppLog.Write($"Installed to {InstallDir}");
        return InstalledExe;
    }

    public static void Uninstall()
    {
        SetStartWithWindows(false);
        RemoveLegacyScheduledTask();
        StopOtherInstances();
        AppLog.Write("Uninstalled");

        // Remove the install folder after this process exits (an exe can't delete itself while running).
        if (Directory.Exists(InstallDir))
        {
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{InstallDir}\"")
            { CreateNoWindow = true, UseShellExecute = false });
        }
    }

    public static void Launch(string exePath, bool background)
    {
        Process.Start(new ProcessStartInfo(exePath, background ? "--background" : "")
        {
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            UseShellExecute = false
        });
    }

    // ---------------- helpers ----------------

    private static void StopOtherInstances()
    {
        var me = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            if (p.Id == me) continue;
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
                AppLog.Write($"Stopped running bridge (pid {p.Id}) for install/update");
            }
            catch (Exception ex) { AppLog.Write($"Couldn't stop pid {p.Id}", ex); }
        }
    }

    private static void CopyWithRetry(string src, string dst)
    {
        if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) return;
        for (int attempt = 1; ; attempt++)
        {
            try { File.Copy(src, dst, overwrite: true); return; }
            catch (IOException) when (attempt < 10) { Thread.Sleep(500); }
        }
    }

    /// <summary>
    /// Keeps the user's existing settings (e.g. extra allowed origins or a changed port),
    /// but adds any allowed origins that ship with the new version.
    /// </summary>
    private static void MergeSettings(string newPath, string installedPath)
    {
        if (!File.Exists(newPath)) return;
        if (string.Equals(Path.GetFullPath(newPath), Path.GetFullPath(installedPath), StringComparison.OrdinalIgnoreCase)) return;
        if (!File.Exists(installedPath)) { File.Copy(newPath, installedPath); return; }

        try
        {
            var opts = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            var existing = JsonNode.Parse(File.ReadAllText(installedPath), documentOptions: opts)!.AsObject();
            var incoming = JsonNode.Parse(File.ReadAllText(newPath), documentOptions: opts)!.AsObject();

            var newOrigins = incoming["ScanBridge"]?["AllowedOrigins"]?.AsArray().Select(n => n!.GetValue<string>()) ?? [];
            if (existing["ScanBridge"] is not JsonObject sb) existing["ScanBridge"] = sb = new JsonObject();
            if (sb["AllowedOrigins"] is not JsonArray origins) sb["AllowedOrigins"] = origins = new JsonArray();

            var have = origins.Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = newOrigins.Where(o => have.Add(o)).ToList();
            foreach (var o in added) origins.Add(o);

            if (added.Count > 0)
            {
                File.WriteAllText(installedPath, existing.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                AppLog.Write("Settings updated, added origins: " + string.Join(", ", added));
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Couldn't merge settings; keeping the existing appsettings.json", ex);
        }
    }

    private static void RemoveLegacyScheduledTask()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Delete /TN \"{LegacyTaskName}\" /F")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            p?.WaitForExit(5000);
        }
        catch { /* not present */ }
    }
}
