using System.Drawing;
using System.Drawing.Imaging;
using Windows.Devices.Enumeration;
using Windows.Devices.Scanners;
using Windows.Graphics.Printing;
using Windows.Storage;

namespace ScanBridge;

/// <summary>
/// Feeder scanning through Windows' modern scanner API (Windows.Devices.Scanners), the same one the
/// Windows Scan app uses. Unlike the older WIA automation library, it returns every page of a feeder
/// batch, including scanners whose drivers pull the whole stack through in one go.
/// </summary>
public static class WinRtScanner
{
    /// <summary>
    /// Scans the feeder. Returns null if this API can't find or use the scanner (caller then falls back to WIA).
    /// Throws <see cref="ScanException"/> for problems the user should see (e.g. empty feeder).
    /// </summary>
    public static async Task<ScanResult?> TryScanFeederAsync(ScanRequest req, string? scannerName, int maxPages)
    {
        await CoolDownAsync();

        var devices = await DeviceInformation.FindAllAsync(ImageScanner.GetDeviceSelector());
        if (devices.Count == 0)
        {
            AppLog.Write("Modern scanner API: no scanners found.");
            return null;
        }

        var info = Match(devices, scannerName);
        if (info is null)
        {
            AppLog.Write($"Modern scanner API: couldn't match \"{scannerName}\" among [{string.Join(", ", devices.Select(d => d.Name))}].");
            return null;
        }

        var scanner = await ImageScanner.FromIdAsync(info.Id);
        if (scanner is null || !scanner.IsScanSourceSupported(ImageScannerScanSource.Feeder))
        {
            AppLog.Write($"Modern scanner API: \"{info.Name}\" has no feeder through this API.");
            return null;
        }

        if (req.Source == ScanSource.Duplex && !scanner.FeederConfiguration.CanScanDuplex)
            throw new ScanException("This scanner doesn't support double-sided feeder scanning. Choose Feeder or Flatbed.", 400);

        // First try with the requested settings. Some drivers (seen with an HP OfficeJet over the network)
        // answer "feeder empty" straight away when they dislike a setting such as paper size, page limit or
        // JPEG output. If that happens within a few seconds, try once more with the driver's own defaults.
        var started = DateTime.UtcNow;
        var first = await ScanOnceAsync(scanner, info.Name, req, maxPages, useDriverDefaults: false);
        if (first.Pages is { Count: > 0 } pages1) return Done(pages1);

        var quick = DateTime.UtcNow - started < TimeSpan.FromSeconds(5);
        AppLog.Write($"Modern scanner API: no pages ({first.Why}) after {(DateTime.UtcNow - started).TotalSeconds:0.0} s.");
        if (!quick) throw new ScanException("The document feeder is empty. Load pages and try again.", 409);

        await Task.Delay(1500);
        var again = await ImageScanner.FromIdAsync(info.Id) ?? scanner;
        var second = await ScanOnceAsync(again, info.Name, req, maxPages, useDriverDefaults: true);
        if (second.Pages is { Count: > 0 } pages2) return Done(pages2);

        AppLog.Write($"Modern scanner API: still no pages with the driver's defaults ({second.Why}).");
        throw new ScanException(
            "The scanner says its document feeder is empty. Check the pages are pushed in until the feeder grabs them " +
            "(most printers show \"Document loaded\"), then try again. If it still fails, try the same scan in the Windows Scan app.", 409);

        static ScanResult Done(List<string> pages)
        {
            AppLog.Write($"Modern scanner API: {pages.Count} page(s) scanned.");
            return new ScanResult(pages);
        }
    }

    private record Attempt(List<string>? Pages, string Why);

    /// <summary>One feeder scan. Returns the pages, or none with the reason (empty feeder, or zero files).</summary>
    private static async Task<Attempt> ScanOnceAsync(ImageScanner scanner, string name, ScanRequest req, int maxPages, bool useDriverDefaults)
    {
        var cfg = scanner.FeederConfiguration;

        // Output format: with our settings prefer JPEG (smaller); with driver defaults prefer lossless formats,
        // which every driver handles (we convert to JPEG ourselves).
        var formats = useDriverDefaults
            ? new[] { ImageScannerFormat.Png, ImageScannerFormat.DeviceIndependentBitmap, ImageScannerFormat.Tiff, ImageScannerFormat.Jpeg }
            : new[] { ImageScannerFormat.Jpeg, ImageScannerFormat.Png, ImageScannerFormat.DeviceIndependentBitmap, ImageScannerFormat.Tiff };
        foreach (var format in formats)
        {
            if (cfg.IsFormatSupported(format)) { cfg.Format = format; break; }
        }

        var mode = req.Color switch
        {
            ColorMode.Gray => ImageScannerColorMode.Grayscale,
            ColorMode.BlackWhite => ImageScannerColorMode.Monochrome,
            _ => ImageScannerColorMode.Color
        };
        if (cfg.IsColorModeSupported(mode)) cfg.ColorMode = mode;

        float dpi = Math.Clamp(req.Dpi, cfg.MinResolution.DpiX, cfg.MaxResolution.DpiX);
        cfg.DesiredResolution = new ImageScannerResolution { DpiX = dpi, DpiY = dpi };

        if (!useDriverDefaults)
        {
            var media = req.PageSize switch
            {
                PageSize.A4 => PrintMediaSize.IsoA4,
                PageSize.Legal => PrintMediaSize.NorthAmericaLegal,
                _ => PrintMediaSize.NorthAmericaLetter
            };
            if (cfg.IsPageSizeSupported(media, PrintOrientation.Portrait))
            {
                cfg.PageSize = media;
                cfg.PageOrientation = PrintOrientation.Portrait;
            }
            cfg.MaxNumberOfPages = (uint)Math.Max(0, maxPages); // 0 would mean "all"
        }
        else if (cfg.CanAutoDetectPageSize)
        {
            cfg.AutoDetectPageSize = true;
        }

        if (cfg.CanScanDuplex) cfg.Duplex = req.Source == ScanSource.Duplex;

        AppLog.Write($"Modern scanner API: \"{name}\", feeder{(useDriverDefaults ? " (driver defaults)" : "")}, {cfg.Format}, {cfg.ColorMode}, " +
                     $"{cfg.ActualResolution.DpiX:0} dpi, duplex {cfg.Duplex}, page size {(useDriverDefaults ? (cfg.AutoDetectPageSize ? "auto" : "default") : cfg.PageSize.ToString())}, " +
                     $"max pages {cfg.MaxNumberOfPages}, auto-detect {(cfg.CanAutoDetectPageSize ? "yes" : "no")}.");

        var dir = Path.Combine(Path.GetTempPath(), "Scan2PdfBridge", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(dir);
            ImageScannerScanResult result;
            try
            {
                var operation = scanner.ScanFilesToFolderAsync(ImageScannerScanSource.Feeder, folder);
                operation.Progress = (_, pagesSoFar) => ScanProgress.Pages((int)pagesSoFar);
                result = await WaitAsync(operation);
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80210003)) // WIA_ERROR_PAPER_EMPTY
            {
                return new Attempt(null, "driver reported WIA_ERROR_PAPER_EMPTY 0x80210003");
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80210002)) // WIA_ERROR_PAPER_JAM
            {
                throw new ScanException("Paper jam. Clear the scanner and try again.", 409);
            }
            catch (Exception ex) when (ex is not ScanException)
            {
                // The scan was started, so the scanner may already be pulling paper: don't start a second
                // scan through WIA (that only reports "busy" and loses the page). Report it instead.
                AppLog.Write("Modern scanner API: scan failed after it started.", ex);
                throw ScanErrors.FromHResult(ex.HResult, ex.Message);
            }
            finally
            {
                _lastFinished = DateTime.UtcNow;
            }

            var files = result.ScannedFiles;
            if (files.Count == 0) return new Attempt(null, "scan completed with 0 files");

            var pages = new List<string>(files.Count);
            foreach (var f in files)
                pages.Add(Convert.ToBase64String(ToJpeg(await File.ReadAllBytesAsync(f.Path), req.JpegQuality)));
            return new Attempt(pages, "ok");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp files; ignore */ }
        }
    }

    private static DeviceInformation? Match(IReadOnlyList<DeviceInformation> devices, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var exact = devices.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
            var partial = devices.FirstOrDefault(d =>
                d.Name.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(d.Name, StringComparison.OrdinalIgnoreCase));
            if (partial is not null) return partial;

            // The two APIs often name the same scanner differently, e.g. WIA "HP799700 (HP OfficeJet Pro 6970)"
            // vs "HP-KIRO (HP OfficeJet Pro 6970)": the network name differs, the model doesn't. Match on the
            // model-like parts (letters and digits, 6+ characters), preferring a single unambiguous match.
            var wanted = NameParts(name);
            var byModel = devices.Where(d => NameParts(d.Name).Overlaps(wanted)).ToList();
            if (byModel.Count > 0)
            {
                if (byModel.Count > 1) AppLog.Write($"Modern scanner API: \"{name}\" matches {byModel.Count} entries; using \"{byModel[0].Name}\".");
                return byModel[0];
            }
        }
        return devices.Count == 1 ? devices[0] : null;
    }

    /// <summary>"NPIB969C9 (HP LaserJet MFP M234sdw)" → {"npib969c9", "hp laserjet mfp m234sdw"}: the parts that identify a model.</summary>
    internal static HashSet<string> NameParts(string name)
    {
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in name.Split(['(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries))
        {
            var p = raw.Trim();
            // A model name has a space or a digit and some length; skip bare network names like "HP-KIRO".
            if (p.Length >= 6 && p.Any(char.IsDigit) && p.Contains(' ')) parts.Add(p);
        }
        return parts;
    }

    // ---------- waiting on the scan ----------

    private static DateTime _lastFinished = DateTime.MinValue;

    /// <summary>
    /// Many scanners (and Windows' scanner service) need a moment after a feeder scan before they accept the
    /// next one; starting too soon fails with "operation not started". Wait out a short gap instead.
    /// </summary>
    private static async Task CoolDownAsync()
    {
        var gap = TimeSpan.FromSeconds(3) - (DateTime.UtcNow - _lastFinished);
        if (gap > TimeSpan.Zero)
        {
            AppLog.Write($"Modern scanner API: waiting {gap.TotalSeconds:0.0} s for the scanner to settle after the last scan.");
            await Task.Delay(gap);
        }
    }

    /// <summary>
    /// Waits for the scan through its Completed callback. Awaiting the operation directly asks for its status
    /// right away, which the scanner service can refuse with E_ASYNC_OPERATION_NOT_STARTED (0x80000019) even
    /// though the scan then goes ahead, so the pages were lost.
    /// </summary>
    private static async Task<ImageScannerScanResult> WaitAsync(Windows.Foundation.IAsyncOperationWithProgress<ImageScannerScanResult, uint> op)
    {
        var done = new TaskCompletionSource<ImageScannerScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Windows.Foundation.AsyncOperationWithProgressCompletedHandler<ImageScannerScanResult, uint> handler = (o, status) =>
        {
            try
            {
                switch (status)
                {
                    case Windows.Foundation.AsyncStatus.Completed: done.TrySetResult(o.GetResults()); break;
                    case Windows.Foundation.AsyncStatus.Canceled: done.TrySetCanceled(); break;
                    case Windows.Foundation.AsyncStatus.Error:
                        var hr = o.ErrorCode?.HResult ?? unchecked((int)0x80004005);
                        done.TrySetException(System.Runtime.InteropServices.Marshal.GetExceptionForHR(hr) ?? new Exception($"Scan failed (0x{hr:X8})."));
                        break;
                }
            }
            catch (Exception ex) { done.TrySetException(ex); }
        };

        // Attaching the handler can itself hit "not started yet" for a moment: retry briefly.
        for (int attempt = 0; ; attempt++)
        {
            try { op.Completed = handler; break; }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80000019) && attempt < 40)
            {
                await Task.Delay(250);
            }
        }
        return await done.Task;
    }

    /// <summary>Re-encodes any scanned image (JPEG/PNG/BMP/TIFF) as JPEG at the requested quality.</summary>
    private static byte[] ToJpeg(byte[] input, int quality)
    {
        using var inStream = new MemoryStream(input);
        using var image = Image.FromStream(inStream);
        using var outStream = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 10, 100));
        image.Save(outStream, encoder, parameters);
        return outStream.ToArray();
    }
}
