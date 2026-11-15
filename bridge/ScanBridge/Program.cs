using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using ScanBridge;

const string DefaultUrl = "http://127.0.0.1:52801";

// "--background" = started by the logon task: no startup balloon. Strip it so ASP.NET config doesn't parse it.
var background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
args = args.Where(a => !a.Equals("--background", StringComparison.OrdinalIgnoreCase)).ToArray();

// ---- Single instance: a second launch just opens the status page of the running one ----
using var mutex = new Mutex(initiallyOwned: true, @"Local\Scan2PdfBridge", out var isFirstInstance);

// Always read appsettings.json next to the exe, regardless of the working directory.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

if (!builder.Configuration.GetSection("Kestrel:Endpoints").Exists())
    builder.WebHost.UseUrls(DefaultUrl);

var listenUrl = (builder.Configuration["Kestrel:Endpoints:Http:Url"] ?? DefaultUrl).TrimEnd('/');
var statusUrl = listenUrl.Replace("://0.0.0.0", "://127.0.0.1").Replace("://*", "://127.0.0.1") + "/";

if (!isFirstInstance)
{
    TrayApp.OpenUrl(statusUrl);
    return;
}

var allowedOrigins = builder.Configuration.GetSection("ScanBridge:AllowedOrigins").Get<string[]>() ?? [];
var maxPages = builder.Configuration.GetValue("ScanBridge:MaxPages", 100);

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
        ? "<p class='warn'>None configured: the add-in can't connect. Add its web address to <code>ScanBridge:AllowedOrigins</code> in appsettings.json and restart.</p>"
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
        <p class="muted">Settings: <code>{{WebUtility.HtmlEncode(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))}}</code><br>
        Log: <code>{{WebUtility.HtmlEncode(AppLog.FilePath)}}</code></p>
        <footer>© 2026 Aghapy Tech Services</footer>
        </body></html>
        """;
    return Results.Content(html, "text/html", Encoding.UTF8);
});

app.MapGet("/health", () => Results.Ok(new { ok = true, version = TrayApp.Version }));

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
        var result = req.DeviceId == DemoScanner.Id
            ? await Task.Run(() => DemoScanner.Scan(req, maxPages))
            : await StaRunner.Run(() => WiaScanner.Scan(req, maxPages));
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

await TrayApp.RunAsync(statusUrl, showStartupBalloon: !background);

AppLog.Write("Stopping (exit from tray)");
await app.StopAsync(TimeSpan.FromSeconds(5));
