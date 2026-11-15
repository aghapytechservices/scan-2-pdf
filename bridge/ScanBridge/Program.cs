using System.Text.Json.Serialization;
using ScanBridge;

var builder = WebApplication.CreateBuilder(args);

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
// 2) Private/Local Network Access: Chromium preflights public→loopback requests and requires this opt-in header.
app.Use(async (ctx, next) =>
{
    var origin = ctx.Request.Headers.Origin.ToString();
    if (!string.IsNullOrEmpty(origin) && !allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    if (ctx.Request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
        ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
    await next();
});

app.UseCors();

// One scan at a time — scanners don't share.
var scanLock = new SemaphoreSlim(1, 1);

app.MapGet("/health", () => Results.Ok(new { ok = true, version = "1.0.0" }));

app.MapGet("/scanners", async () =>
{
    try { return Results.Ok(await StaRunner.Run(WiaScanner.ListScanners)); }
    catch (ScanException ex) { return Results.Problem(ex.Message, statusCode: ex.StatusCode); }
});

app.MapPost("/scan", async (ScanRequest req, ILogger<Program> log) =>
{
    if (req.Dpi is < 75 or > 600)
        return Results.Problem("DPI must be between 75 and 600.", statusCode: 400);

    if (!await scanLock.WaitAsync(0))
        return Results.Problem("A scan is already in progress.", statusCode: 409);

    try
    {
        log.LogInformation("Scan started: {Source} {Dpi}dpi {Color}", req.Source, req.Dpi, req.Color);
        var result = await StaRunner.Run(() => WiaScanner.Scan(req, maxPages));
        log.LogInformation("Scan finished: {Pages} page(s)", result.Pages.Count);
        return Results.Ok(result);
    }
    catch (ScanException ex)
    {
        return Results.Problem(ex.Message, statusCode: ex.StatusCode);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Scan failed");
        return Results.Problem("Unexpected scanner error: " + ex.Message, statusCode: 500);
    }
    finally
    {
        scanLock.Release();
    }
});

app.Run();
