using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using ScanBridge;

const string DefaultUrl = "http://127.0.0.1:52801";

// Command-line switches (stripped before ASP.NET parses the rest):
//   --background  started at sign-in: no notification, and exit quietly if already running
//   --install     install/update for this user and start at sign-in (add --quiet to skip prompts)
//   --uninstall   remove start-at-sign-in and the installed files
string[] ownSwitches = ["--background", "--install", "--uninstall", "--quiet"];
bool Has(string sw) => args.Contains(sw, StringComparer.OrdinalIgnoreCase);
var background = Has("--background") || StartupManager.LaunchedAtStartup();
var installRequested = Has("--install");
var uninstallRequested = Has("--uninstall");
var quiet = Has("--quiet");
args = args.Where(a => !ownSwitches.Contains(a, StringComparer.OrdinalIgnoreCase)).ToArray();

// ---- Install / update / uninstall (before taking the single-instance lock) ----
try
{
    if (uninstallRequested)
    {
        Installer.Uninstall();
        if (!quiet) TrayApp.ShowInfo("Scan 2 PDF Bridge has been removed from this PC.");
        return;
    }

    // The Store (MSIX) edition is installed and updated by the Store, so the self-installer is plain-exe only.
    if (!AppPaths.IsPackaged && (installRequested || (!background && !Installer.IsRunningFromInstallDir)))
    {
        var proceed = installRequested;
        if (!installRequested)
        {
            var updating = File.Exists(Installer.InstalledExe);
            var answer = TrayApp.Ask(updating
                ? $"Update the installed Scan 2 PDF Bridge to v{TrayApp.Version}?\n\nThe running copy will restart."
                : "Install Scan 2 PDF Bridge for this Windows user?\n\n" +
                  "It will start automatically when you sign in, so the Outlook add-in can always reach your scanner.\n\n" +
                  "Yes = install    No = just run it this time");
            if (answer is null) return;   // Cancel
            proceed = answer.Value;
        }

        if (proceed)
        {
            var exe = Installer.Install();
            Installer.Launch(exe, background: quiet);
            return;
        }
    }
}
catch (Exception ex)
{
    AppLog.Write("Install/uninstall failed", ex);
    TrayApp.ShowError($"Couldn't install Scan 2 PDF Bridge.\n\n{ex.Message}\n\nLog: {AppLog.FilePath}");
    return;
}

// ---- Single instance: a second launch just opens the status page of the running one ----
using var mutex = new Mutex(initiallyOwned: true, @"Local\Scan2PdfBridge", out var isFirstInstance);

// Always read appsettings.json next to the exe, regardless of the working directory.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// User overrides (e.g. extra allowed origins, another port) live in a writable folder,
// because the Store edition's install folder is read-only.
builder.Configuration.AddJsonFile(AppPaths.UserSettingsFile, optional: true, reloadOnChange: false);

if (!builder.Configuration.GetSection("Kestrel:Endpoints").Exists())
    builder.WebHost.UseUrls(DefaultUrl);

var listenUrl = (builder.Configuration["Kestrel:Endpoints:Http:Url"] ?? DefaultUrl).TrimEnd('/');
var statusUrl = listenUrl.Replace("://0.0.0.0", "://127.0.0.1").Replace("://*", "://127.0.0.1") + "/";

if (!isFirstInstance)
{
    if (!background) TrayApp.OpenUrl(statusUrl); // at sign-in, a duplicate start just exits
    return;
}

// Built-in origins come from appsettings.json; users add their own via ExtraAllowedOrigins
// (arrays from two config files merge by index, so a separate key avoids overwriting the defaults).
var allowedOrigins = (builder.Configuration.GetSection("ScanBridge:AllowedOrigins").Get<string[]>() ?? [])
    .Concat(builder.Configuration.GetSection("ScanBridge:ExtraAllowedOrigins").Get<string[]>() ?? [])
    .Select(o => o.Trim().TrimEnd('/'))
    .Where(o => o.Length > 0)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();
var maxPages = builder.Configuration.GetValue("ScanBridge:MaxPages", 100);
// "auto" (default): feeder scans use Windows' modern scanner API, falling back to WIA. "wia": always use WIA.
var feederEngine = (builder.Configuration["ScanBridge:FeederEngine"] ?? "auto").Trim().ToLowerInvariant();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .WithMethods("GET", "POST")));

var app = builder.Build();

// 1) Hard origin check: CORS alone only hides responses; this rejects requests from unknown web pages outright.
//    (Opening the status page directly in a browser sends no Origin header, so it's allowed.)
// 2) Private/Local Network Access: Chromium preflights public→loopback requests and requires this opt-in header.
app.Use(async (ctx, next) =>
{
    var origin = ctx.Request.Headers.Origin.ToString();
    if (!string.IsNullOrEmpty(origin) && !allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
    {
        AppLog.Write($"Rejected request from origin {origin} — add it to ScanBridge:AllowedOrigins if it's your add-in.");
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    if (ctx.Request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
        ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
    await next();
});

app.UseCors();

// Real scanners, or the demo scanner when Windows reports none.
async Task<IReadOnlyList<ScannerInfo>> ListScannersAsync()
{
    var real = await StaRunner.Run(WiaScanner.ListScanners);
    return real.Count > 0 ? real : [DemoScanner.Info];
}

// One scan at a time — scanners don't share.
var scanLock = new SemaphoreSlim(1, 1);

app.MapGet("/", async () =>
{
    string scannersHtml;
    try
    {
        var scanners = await ListScannersAsync();
        scannersHtml = scanners.Count == 0
            ? "<p class='warn'>No scanners found. Check the scanner is on, connected, and shows up in the Windows Scan app.</p>"
            : "<ul>" + string.Concat(scanners.Select(s =>
                $"<li>{WebUtility.HtmlEncode(s.Name)} <span class='muted'>{WebUtility.HtmlEncode(s.Manufacturer)}</span></li>")) + "</ul>";
    }
    catch (Exception ex)
    {
        scannersHtml = $"<p class='warn'>Couldn't list scanners: {WebUtility.HtmlEncode(ex.Message)}</p>";
    }

    var origins = allowedOrigins.Length == 0
        ? "<p class='warn'>None configured: the add-in can't connect. Add its web address to <code>ScanBridge:ExtraAllowedOrigins</code> in your overrides file (below) and restart.</p>"
        : "<ul>" + string.Concat(allowedOrigins.Select(o => $"<li><code>{WebUtility.HtmlEncode(o)}</code></li>")) + "</ul>";

    var html = $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>Scan 2 PDF Bridge</title>
        <style>
          body{font-family:"Segoe UI",system-ui,sans-serif;max-width:640px;margin:40px auto;padding:0 16px;color:#1e293b}
          h1{font-size:20px;margin:0 0 4px} .ok{color:#047857;font-weight:600} .warn{color:#b45309}
          .muted{color:#64748b} h2{font-size:15px;margin-top:24px} code{background:#f1f5f9;padding:1px 4px;border-radius:4px}
          footer{margin-top:32px;font-size:12px;color:#94a3b8}
        </style></head><body>
        <h1>Scan 2 PDF Bridge <span class="muted">v{{TrayApp.Version}}</span></h1>
        <p class="ok">● Running on {{WebUtility.HtmlEncode(listenUrl)}}</p>
        <h2>Scanners</h2>{{scannersHtml}}
        <h2>Allowed add-in origins</h2>{{origins}}
        <h2>Files</h2>
        <p class="muted">Edition: {{(AppPaths.IsPackaged ? "Microsoft Store" : "Standalone")}}<br>
        Settings: <code>{{WebUtility.HtmlEncode(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))}}</code><br>
        Your overrides (optional): <code>{{WebUtility.HtmlEncode(AppPaths.UserSettingsFile)}}</code><br>
        Log: <code>{{WebUtility.HtmlEncode(AppLog.FilePath)}}</code></p>
        <footer>© 2026 Aghapy Tech Services</footer>
        </body></html>
        """;
    return Results.Content(html, "text/html", Encoding.UTF8);
});

app.MapGet("/health", () => Results.Ok(new
{
    ok = true,
    version = TrayApp.Version,
    edition = AppPaths.IsPackaged ? "store" : "standalone"
}));

// Store edition: show the Microsoft Store rating dialog on this PC (asked for from the add-in's feedback card).
app.MapPost("/store-review", async () =>
{
    if (!StoreReview.IsAvailable)
        return Results.Problem("Store reviews are only available in the Microsoft Store edition.", statusCode: 404);
    var status = await TrayApp.RequestStoreReviewAsync();
    return Results.Ok(new { status });
});

// Live progress of the current scan (pages so far, elapsed seconds), polled by the add-in.
app.MapGet("/scan/progress", () => Results.Ok(ScanProgress.Snapshot()));

app.MapGet("/scanners", async () =>
{
    try { return Results.Ok(await ListScannersAsync()); }
    catch (ScanException ex) { return Results.Problem(ex.Message, statusCode: ex.StatusCode); }
});

app.MapPost("/scan", async (ScanRequest req) =>
{
    if (req.Dpi is < 75 or > 600)
        return Results.Problem("DPI must be between 75 and 600.", statusCode: 400);

    if (!await scanLock.WaitAsync(0))
        return Results.Problem("A scan is already in progress.", statusCode: 409);

    try
    {
        AppLog.Write($"Scan started: {req.Source} {req.Dpi}dpi {req.Color}");
        ScanProgress.Start();
        ScanResult? result = null;
        if (req.DeviceId == DemoScanner.Id)
        {
            result = await Task.Run(() => DemoScanner.Scan(req, maxPages));
        }
        else if (req.Source != ScanSource.Flatbed && feederEngine != "wia")
        {
            // Feeder: Windows' modern scanner API returns every page of the batch (the older WIA automation
            // library only returns the first page with some drivers). Falls back to WIA if it can't be used.
            try
            {
                var scanners = await StaRunner.Run(WiaScanner.ListScanners);
                var name = scanners.FirstOrDefault(x => x.Id == req.DeviceId)?.Name ?? (scanners.Count == 1 ? scanners[0].Name : null);
                result = await WinRtScanner.TryScanFeederAsync(req, name, maxPages);
            }
            catch (ScanException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Write("Modern scanner API failed; falling back to WIA.", ex);
            }
        }
        result ??= await StaRunner.Run(() => WiaScanner.Scan(req, maxPages));
        AppLog.Write($"Scan finished: {result.Pages.Count} page(s)");
        return Results.Ok(result);
    }
    catch (ScanException ex)
    {
        AppLog.Write("Scan error: " + ex.Message);
        return Results.Problem(ex.Message, statusCode: ex.StatusCode);
    }
    catch (Exception ex)
    {
        AppLog.Write("Scan failed", ex);
        return Results.Problem("Unexpected scanner error: " + ex.Message, statusCode: 500);
    }
    finally
    {
        ScanProgress.Finish();
        scanLock.Release();
    }
});

// ---- Start the server, then show the tray icon until the user exits ----
try
{
    await app.StartAsync();
    AppLog.Write($"Started v{TrayApp.Version} on {listenUrl}");
}
catch (IOException ex)
{
    AppLog.Write("Startup failed", ex);
    TrayApp.ShowError(
        $"Couldn't start on {listenUrl}.\n\nAnother program is probably using that port. " +
        "Change Kestrel:Endpoints:Http:Url in appsettings.json (and the add-in's bridge URL under Advanced), then try again.\n\n" +
        $"Details: {ex.Message}");
    return;
}
catch (Exception ex)
{
    AppLog.Write("Startup failed", ex);
    TrayApp.ShowError($"Scan 2 PDF Bridge couldn't start.\n\n{ex.Message}\n\nLog: {AppLog.FilePath}");
    return;
}

StartupManager.EnsureDefaultOn();
await TrayApp.RunAsync(statusUrl, showStartupBalloon: !background);

AppLog.Write("Stopping (exit from tray)");
await app.StopAsync(TimeSpan.FromSeconds(5));
