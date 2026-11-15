using System.Runtime.InteropServices;

namespace ScanBridge;

/// <summary>
/// Where the bridge keeps writable data, and whether it runs as a Store (MSIX) package.
/// </summary>
public static class AppPaths
{
    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);

    /// <summary>True when installed from the Microsoft Store / an MSIX package.</summary>
    public static bool IsPackaged { get; } = DetectPackaged();

    /// <summary>
    /// Writable folder for logs and user settings.
    /// Packaged: the app's own LocalState folder (a real path Explorer can open).
    /// Unpackaged: %LOCALAPPDATA%\Scan2PdfBridge (also the install folder).
    /// </summary>
    public static string DataFolder { get; } = ResolveDataFolder();

    /// <summary>Optional settings that override appsettings.json (e.g. extra allowed origins).</summary>
    public static string UserSettingsFile => Path.Combine(DataFolder, "appsettings.user.json");

    private static bool DetectPackaged()
    {
        try
        {
            int length = 0;
            return GetCurrentPackageFullName(ref length, null) != APPMODEL_ERROR_NO_PACKAGE;
        }
        catch
        {
            return false; // API missing (very old Windows) → not packaged
        }
    }

    private static string ResolveDataFolder()
    {
        if (IsPackaged)
        {
            try { return Windows.Storage.ApplicationData.Current.LocalFolder.Path; }
            catch { /* fall back below */ }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scan2PdfBridge");
    }
}
