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

        var cfg = scanner.FeederConfiguration;

        // Output format: JPEG if the scanner can produce it; otherwise a lossless format we convert below.
        foreach (var format in new[] { ImageScannerFormat.Jpeg, ImageScannerFormat.Png, ImageScannerFormat.DeviceIndependentBitmap, ImageScannerFormat.Tiff })
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

        if (req.Source == ScanSource.Duplex)
        {
            if (!cfg.CanScanDuplex)
                throw new ScanException("This scanner doesn't support double-sided feeder scanning. Choose Feeder or Flatbed.", 400);
            cfg.Duplex = true;
        }
        else if (cfg.CanScanDuplex)
        {
            cfg.Duplex = false;
        }

        cfg.MaxNumberOfPages = (uint)Math.Max(0, maxPages); // 0 would mean "all"; we cap at maxPages

        var actual = cfg.ActualResolution;
        AppLog.Write($"Modern scanner API: \"{info.Name}\", feeder, {cfg.Format}, {cfg.ColorMode}, {actual.DpiX:0} dpi, duplex {cfg.Duplex}.");

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
                result = await operation;
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80210003)) // WIA_ERROR_PAPER_EMPTY
            {
                throw new ScanException("The document feeder is empty. Load pages and try again.", 409);
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80210002)) // WIA_ERROR_PAPER_JAM
            {
                throw new ScanException("Paper jam. Clear the scanner and try again.", 409);
            }

            var files = result.ScannedFiles;
            if (files.Count == 0)
                throw new ScanException("The document feeder is empty. Load pages and try again.", 409);

            var pages = new List<string>(files.Count);
            foreach (var f in files)
                pages.Add(Convert.ToBase64String(ToJpeg(await File.ReadAllBytesAsync(f.Path), req.JpegQuality)));

            AppLog.Write($"Modern scanner API: {pages.Count} page(s) scanned.");
            return new ScanResult(pages);
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
        }
        return devices.Count == 1 ? devices[0] : null;
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
