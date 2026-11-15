namespace ScanBridge;

/// <summary>Tiny append-only log in the bridge's data folder (the app has no console).</summary>
public static class AppLog
{
    public static string Folder { get; } = Path.Combine(AppPaths.DataFolder, "logs");

    public static string FilePath => Path.Combine(Folder, "bridge.log");

    private static readonly object Gate = new();

    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024) // keep it small
                    File.Move(FilePath, Path.Combine(Folder, "bridge.old.log"), overwrite: true);

                File.AppendAllText(FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{(ex is null ? "" : Environment.NewLine + ex)}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the bridge */ }
    }
}
