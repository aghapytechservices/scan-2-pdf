using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScanBridge;

/// <summary>
/// Update checks for the standalone edition (the Store edition is updated by the Microsoft Store).
/// Looks at the latest GitHub release, and installs it by running the new exe's own installer
/// (<c>--install --quiet</c>), which replaces the installed copy and restarts the bridge.
/// </summary>
public static class Updater
{
    private const string Repo = "aghapytechservices/scan-2-pdf";
    private const string AssetName = "Scan2PdfBridge-win-x64.zip";
    private const string LatestReleaseApi = $"https://api.github.com/repos/{Repo}/releases/latest";
    private const string DownloadPrefix = $"https://github.com/{Repo}/releases/download/";
    public const string ReleasesPage = $"https://github.com/{Repo}/releases/latest";
    public const string StorePage = "ms-windows-store://pdp/?productid=9NNN9ZTWZ9TZ";

    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    public static bool IsAvailable => !AppPaths.IsPackaged;

    /// <summary>The newest release found by the last check, if it's newer than this copy.</summary>
    public static ReleaseInfo? Available { get; private set; }

    public sealed record ReleaseInfo(Version Version, string Tag, string PageUrl, string ZipUrl, string? Sha256)
    {
        public string Display => Version.ToString(3);
    }

    public static Version Current { get; } =
        typeof(Updater).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : new Version(1, 0, 0);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Scan2PdfBridge", Current.ToString(3)));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>Asks GitHub for the latest release. Returns it when it's newer than this copy, else null.</summary>
    public static async Task<ReleaseInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var release = await Http.GetFromJsonAsync<GitHubRelease>(LatestReleaseApi, timeout.Token)
                      ?? throw new InvalidOperationException("GitHub returned no release.");

        var state = UpdateState.Load();
        state.LastCheckUtc = DateTime.UtcNow;
        state.Save();

        if (release.Draft || release.Prerelease || !TryParseVersion(release.TagName, out var version))
            return Available = null;

        var asset = release.Assets?.FirstOrDefault(a => string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));
        if (asset?.BrowserDownloadUrl is not { } url || !url.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Write($"Update check: release {release.TagName} has no {AssetName}");
            return Available = null;
        }

        var sha = asset.Digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? d[7..] : null;
        var info = new ReleaseInfo(version, release.TagName!, release.HtmlUrl ?? ReleasesPage, url, sha);
        AppLog.Write($"Update check: latest {info.Display}, running {Current.ToString(3)}");
        return Available = version > Current ? info : null;
    }

    /// <summary>
    /// Downloads the release, checks it, and starts its installer. The new copy waits for this process
    /// to exit, so the caller must exit right after this returns.
    /// </summary>
    public static async Task DownloadAndStartInstallAsync(ReleaseInfo release, IProgress<string>? progress = null)
    {
        var work = Path.Combine(Path.GetTempPath(), $"Scan2PdfBridge-update-{release.Display}");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        var zipPath = Path.Combine(work, AssetName);

        progress?.Report("Downloading…");
        await using (var src = await Http.GetStreamAsync(release.ZipUrl))
        await using (var dst = File.Create(zipPath))
            await src.CopyToAsync(dst);

        if (release.Sha256 is { } expected)
        {
            progress?.Report("Checking the download…");
            await using var f = File.OpenRead(zipPath);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(f));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The download is damaged (its checksum doesn't match). Please try again.");
        }

        progress?.Report("Unpacking…");
        var extractDir = Path.Combine(work, "files");
        ZipFile.ExtractToDirectory(zipPath, extractDir);
        var newExe = Directory.EnumerateFiles(extractDir, Installer.ExeName, SearchOption.AllDirectories).FirstOrDefault()
                     ?? throw new FileNotFoundException($"{Installer.ExeName} wasn't found in the download.");

        CheckSameSigner(newExe);

        AppLog.Write($"Starting update to {release.Display} from {newExe}");
        Process.Start(new ProcessStartInfo(newExe, $"--install --quiet --wait-pid {Environment.ProcessId}")
        {
            WorkingDirectory = Path.GetDirectoryName(newExe)!,
            UseShellExecute = false
        });
    }

    /// <summary>
    /// If this copy is code-signed, the update must be signed by the same publisher.
    /// (Unsigned builds skip this; they rely on HTTPS from GitHub and the release checksum.)
    /// </summary>
    private static void CheckSameSigner(string newExe)
    {
        var mine = SignerOf(Environment.ProcessPath);
        if (mine is null) return;
        var theirs = SignerOf(newExe);
        if (!string.Equals(mine, theirs, StringComparison.Ordinal))
            throw new InvalidDataException($"The update isn't signed by {mine}, so it wasn't installed.");
    }

    private static string? SignerOf(string? path)
    {
        if (path is null) return null;
        try
        {
#pragma warning disable SYSLIB0057 // reading the Authenticode certificate, not loading a key
            using var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return cert.Subject;
        }
        catch { return null; } // not signed
    }

    /// <summary>Called by the new copy (<c>--wait-pid</c>) so the old one has exited before files are replaced.</summary>
    public static void WaitForExit(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.WaitForExit(15000)) AppLog.Write($"Old bridge (pid {pid}) didn't exit in time; it will be stopped");
        }
        catch (ArgumentException) { /* already gone */ }
        catch (Exception ex) { AppLog.Write($"Waiting for pid {pid} failed", ex); }
    }

    private static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var s = tag.Trim().TrimStart('v', 'V');
        var dash = s.IndexOfAny(['-', '+']);
        if (dash >= 0) s = s[..dash];
        if (!Version.TryParse(s, out var v)) return false;
        version = new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        return true;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}

/// <summary>Update preferences and memory, kept in the data folder.</summary>
public sealed class UpdateState
{
    public bool AutoCheck { get; set; } = true;
    public string? SkippedVersion { get; set; }
    public DateTime? LastCheckUtc { get; set; }
    /// <summary>Set just before an update is installed, so the new copy can say "Updated to vX".</summary>
    public string? PendingVersion { get; set; }

    private static string FilePath => Path.Combine(AppPaths.DataFolder, "update-state.json");
    private static readonly object Gate = new();

    public static UpdateState Load()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(FilePath)) ?? new();
            }
            catch (Exception ex) { AppLog.Write("Couldn't read update settings", ex); }
            return new();
        }
    }

    public void Save()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataFolder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AppLog.Write("Couldn't save update settings", ex); }
        }
    }
}
