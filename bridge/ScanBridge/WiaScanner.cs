using System.Runtime.InteropServices;

namespace ScanBridge;

public record ScannerInfo(string Id, string Name, string Manufacturer);

public enum ScanSource { Flatbed, Feeder, Duplex }
public enum ColorMode { Color, Gray, BlackWhite }
public enum PageSize { Letter, Legal, A4 }

public record ScanRequest(
    string? DeviceId,
    ScanSource Source = ScanSource.Flatbed,
    int Dpi = 200,
    ColorMode Color = ColorMode.Color,
    PageSize PageSize = PageSize.Letter,
    int JpegQuality = 75);

/// <param name="Pages">base64 JPEG per page</param>
/// <param name="Warning">Set when the scanner stopped part-way; the pages scanned so far are still returned.</param>
public record ScanResult(IReadOnlyList<string> Pages, string? Warning = null);

public class ScanException(string message, int statusCode = 500) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Thin wrapper over Windows Image Acquisition (WIA 2.0 automation, wiaaut.dll — ships with Windows).
/// Uses late-bound COM (dynamic) so no interop assembly / COMReference is needed.
/// All methods must be called on an STA thread (see <see cref="StaRunner"/>).
/// </summary>
public static class WiaScanner
{
    // WIA constants
    private const int ScannerDeviceType = 1;
    private const string FormatJpeg = "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}";

    // Device properties
    private const int WIA_DPS_DOCUMENT_HANDLING_SELECT = 3088;
    private const int WIA_DPS_PAGES = 3096;
    private const int WIA_DPS_PAGE_SIZE = 3097;

    // Item properties
    private const int WIA_IPS_CUR_INTENT = 6146;
    private const int WIA_IPS_XRES = 6147;
    private const int WIA_IPS_YRES = 6148;
    private const int WIA_IPS_XPOS = 6149;
    private const int WIA_IPS_YPOS = 6150;
    private const int WIA_IPS_XEXTENT = 6151;
    private const int WIA_IPS_YEXTENT = 6152;

    // Document handling flags
    private const int FEEDER = 0x001, FLATBED = 0x002, DUPLEX = 0x004;

    // HRESULTs
    private const int WIA_ERROR_PAPER_EMPTY = unchecked((int)0x80210003);
    private const int WIA_ERROR_PAPER_JAM = unchecked((int)0x80210002);
    private const int WIA_ERROR_BUSY = unchecked((int)0x80210006);
    private const int WIA_ERROR_OFFLINE = unchecked((int)0x80210005);
    private const int WIA_ERROR_COVER_OPEN = unchecked((int)0x80210016);
    private const int WIA_S_NO_DEVICE_AVAILABLE = unchecked((int)0x80210015);
    private const int E_INVALIDARG = unchecked((int)0x80070057); // surfaces as ArgumentException, not COMException

    private static dynamic CreateCom(string progId)
    {
        var type = Type.GetTypeFromProgID(progId)
                   ?? throw new ScanException($"COM class '{progId}' not found. Is WIA available on this PC?");
        return Activator.CreateInstance(type)!;
    }

    public static IReadOnlyList<ScannerInfo> ListScanners()
    {
        var result = new List<ScannerInfo>();
        dynamic manager = CreateCom("WIA.DeviceManager");
        foreach (dynamic info in manager.DeviceInfos)
        {
            if ((int)info.Type != ScannerDeviceType) continue;
            result.Add(new ScannerInfo(
                (string)info.DeviceID,
                ReadProp(info.Properties, "Name") ?? "Scanner",
                ReadProp(info.Properties, "Manufacturer") ?? ""));
        }
        return result;
    }

    public static ScanResult Scan(ScanRequest req, int maxPages)
    {
        dynamic device = Connect(req.DeviceId);
        var pages = new List<string>();
        var step = "preparing the scanner";

        try
        {
            if (req.Source != ScanSource.Flatbed)
            {
                step = "selecting the document feeder";
                int handling = FEEDER | (req.Source == ScanSource.Duplex ? DUPLEX : 0);
                if (!TrySetProp(device.Properties, WIA_DPS_DOCUMENT_HANDLING_SELECT, handling))
                    throw new ScanException(req.Source == ScanSource.Duplex
                        ? "This scanner doesn't support double-sided feeder scanning. Choose Feeder or Flatbed."
                        : "This scanner doesn't have a document feeder. Choose Flatbed.", 400);
                TrySetProp(device.Properties, WIA_DPS_PAGES, 1);
                TrySetProp(device.Properties, WIA_DPS_PAGE_SIZE, req.PageSize switch
                {
                    PageSize.A4 => 0, PageSize.Letter => 1, PageSize.Legal => 4, _ => 1
                });
            }
            else
            {
                TrySetProp(device.Properties, WIA_DPS_DOCUMENT_HANDLING_SELECT, FLATBED);
            }

            // Settings are applied once, before the first page. Re-applying them between feeder pages
            // makes some drivers reject the next transfer (E_INVALIDARG).
            step = "applying scan settings";
            dynamic item = device.Items[1];
            int dpi = ConfigureItem(item, req);
            bool jpegUnsupported = false;
            string? warning = null;

            while (pages.Count < maxPages)
            {
                step = pages.Count == 0 ? "scanning" : $"scanning page {pages.Count + 1}";
                dynamic? image;
                try
                {
                    image = TransferPage(item, ref jpegUnsupported);
                }
                catch (Exception ex) when (pages.Count > 0)
                {
                    // Keep what we have: a later page failing shouldn't throw away the earlier ones.
                    AppLog.Write($"Scanner stopped after {pages.Count} page(s) (HRESULT 0x{ex.HResult:X8})", ex);
                    warning = ex is COMException ce && IsKnownWiaError(ce.HResult)
                        ? $"The scanner stopped after {pages.Count} page{(pages.Count == 1 ? "" : "s")}: {Translate(ce).Message}"
                        : $"The scanner stopped after {pages.Count} page{(pages.Count == 1 ? "" : "s")}. Check the feeder and scan the rest.";
                    break;
                }

                if (image is null) // feeder empty
                {
                    if (pages.Count == 0)
                        throw new ScanException("The document feeder is empty. Load pages and try again.", 409);
                    break; // normal end of a feeder batch
                }

                step = "converting the scanned page";
                pages.Add(Convert.ToBase64String(ToJpegBytes(image, req.JpegQuality)));
                AppLog.Write($"Page {pages.Count} scanned at {dpi} dpi");

                if (req.Source == ScanSource.Flatbed) break; // one page per flatbed pass
            }

            AppLog.Write($"Scan complete: {pages.Count} page(s){(warning is null ? "" : " (stopped early)")}");
            return new ScanResult(pages, warning);
        }
        catch (ScanException)
        {
            throw;
        }
        catch (COMException ex) when (IsKnownWiaError(ex.HResult))
        {
            throw Translate(ex);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Scan failed while {step} (HRESULT 0x{ex.HResult:X8})", ex);
            throw new ScanException($"The scanner reported an error while {step}: {ex.Message} (0x{ex.HResult:X8})");
        }
        finally
        {
            Marshal.FinalReleaseComObject(device);
        }
    }

    /// <summary>
    /// Transfers one page. Returns null when the feeder is empty, however the driver reports it.
    /// Falls back from JPEG to the driver's default format once, and remembers that for later pages.
    /// </summary>
    private static dynamic? TransferPage(dynamic item, ref bool jpegUnsupported)
    {
        if (!jpegUnsupported)
        {
            try
            {
                return item.Transfer(FormatJpeg);
            }
            catch (Exception ex) when (ex.HResult == WIA_ERROR_PAPER_EMPTY)
            {
                return null;
            }
            catch (Exception ex) when (ex.HResult == E_INVALIDARG)
            {
                // Either the driver can't deliver JPEG, or (some drivers) this is how it signals an empty feeder.
                AppLog.Write("JPEG transfer rejected by driver; trying its default format.");
                jpegUnsupported = true;
            }
        }

        try
        {
            return item.Transfer();
        }
        catch (Exception ex) when (ex.HResult == WIA_ERROR_PAPER_EMPTY)
        {
            return null;
        }
    }

    private static dynamic Connect(string? deviceId)
    {
        dynamic manager = CreateCom("WIA.DeviceManager");
        foreach (dynamic info in manager.DeviceInfos)
        {
            if ((int)info.Type != ScannerDeviceType) continue;
            if (string.IsNullOrEmpty(deviceId) || (string)info.DeviceID == deviceId)
            {
                try { return info.Connect(); }
                catch (COMException ex) { throw Translate(ex); }
                catch (Exception ex) { throw new ScanException($"Couldn't connect to the scanner: {ex.Message}"); }
            }
        }
        throw new ScanException(string.IsNullOrEmpty(deviceId)
            ? "No scanner found. Check that it is powered on and its WIA driver is installed."
            : "The selected scanner is not available.", 404);
    }

    /// <summary>Applies settings, fitting each to what the driver supports. Returns the resolution actually used.</summary>
    private static int ConfigureItem(dynamic item, ScanRequest req)
    {
        dynamic props = item.Properties;

        // Intent first: some drivers reset resolution when the intent changes.
        TrySetProp(props, WIA_IPS_CUR_INTENT, req.Color switch
        {
            ColorMode.Color => 1, ColorMode.Gray => 2, ColorMode.BlackWhite => 4, _ => 1
        });

        TrySetProp(props, WIA_IPS_XRES, req.Dpi);
        TrySetProp(props, WIA_IPS_YRES, req.Dpi);
        int xDpi = ReadIntProp(props, WIA_IPS_XRES) ?? req.Dpi;
        int yDpi = ReadIntProp(props, WIA_IPS_YRES) ?? xDpi;

        TrySetProp(props, WIA_IPS_XPOS, 0);
        TrySetProp(props, WIA_IPS_YPOS, 0);

        var (wIn, hIn) = req.PageSize switch
        {
            PageSize.A4 => (8.27, 11.69),
            PageSize.Legal => (8.5, 14.0),
            _ => (8.5, 11.0)
        };
        // Extents are in pixels at the current resolution; values beyond the glass are clamped to the driver's max.
        TrySetProp(props, WIA_IPS_XEXTENT, (int)Math.Round(wIn * xDpi));
        TrySetProp(props, WIA_IPS_YEXTENT, (int)Math.Round(hIn * yDpi));

        return xDpi;
    }

    private static byte[] ToJpegBytes(dynamic image, int quality)
    {
        // Some drivers ignore the requested format and return BMP/TIFF — convert (and recompress) via WIA.ImageProcess.
        try
        {
            dynamic process = CreateCom("WIA.ImageProcess");
            process.Filters.Add(process.FilterInfos["Convert"].FilterID);
            dynamic convert = process.Filters[1];
            SetPropByName(convert.Properties, "FormatID", FormatJpeg);
            SetPropByName(convert.Properties, "Quality", Math.Clamp(quality, 10, 100));
            dynamic jpeg = process.Apply(image);
            return (byte[])jpeg.FileData.BinaryData;
        }
        catch (Exception ex) when (IsJpeg(image))
        {
            AppLog.Write("JPEG recompression failed; using the scanner's JPEG as-is.", ex);
            return (byte[])image.FileData.BinaryData;
        }
    }

    private static ScanException Translate(COMException ex) => ex.HResult switch
    {
        WIA_ERROR_PAPER_JAM => new ScanException("Paper jam. Clear the scanner and try again.", 409),
        WIA_ERROR_BUSY => new ScanException("The scanner is busy. Try again in a moment.", 409),
        WIA_ERROR_OFFLINE => new ScanException("The scanner is offline.", 503),
        WIA_ERROR_COVER_OPEN => new ScanException("The scanner cover is open.", 409),
        WIA_S_NO_DEVICE_AVAILABLE => new ScanException("No scanner is available.", 404),
        WIA_ERROR_PAPER_EMPTY => new ScanException("The document feeder is empty.", 409),
        _ => new ScanException($"Scanner error 0x{ex.HResult:X8}: {ex.Message}")
    };

    // ---- property helpers (iterate instead of indexing: most robust with late-bound WIA) ----

    /// <summary>
    /// Sets a WIA property, first fitting the value to the driver's allowed range or list.
    /// Never throws: returns false (and logs) if the property is missing, read-only or rejected.
    /// </summary>
    private static bool TrySetProp(dynamic props, int id, int value)
    {
        object? found = FindProp(props, id);
        if (found is null) return false;
        dynamic prop = found;

        int fitted = value;
        try { fitted = FitToCapabilities(prop, value); }
        catch (Exception ex) { AppLog.Write($"Couldn't read limits of property {id}: {ex.Message}"); }

        try
        {
            if ((bool)prop.IsReadOnly) return false;
            prop.Value = fitted;
            if (fitted != value) AppLog.Write($"Property {id}: requested {value}, scanner supports {fitted}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Scanner rejected property {id} = {fitted}: {ex.Message} (0x{ex.HResult:X8})");
            return false;
        }
    }

    private static int FitToCapabilities(dynamic prop, int value)
    {
        switch ((int)prop.SubType)
        {
            case 1: // range
            {
                int min = Convert.ToInt32(prop.SubTypeMin), max = Convert.ToInt32(prop.SubTypeMax);
                int step = Math.Max(1, Convert.ToInt32(prop.SubTypeStep));
                int v = Math.Clamp(value, min, max);
                int snapped = min + (int)Math.Round((v - min) / (double)step) * step;
                return Math.Min(snapped, max);
            }
            case 2: // list
            {
                int? best = null;
                foreach (object o in prop.SubTypeValues)
                {
                    int candidate = Convert.ToInt32(o);
                    if (best is null || Math.Abs(candidate - value) < Math.Abs(best.Value - value)) best = candidate;
                }
                return best ?? value;
            }
            default: // unspecified / flags
                return value;
        }
    }

    private static object? FindProp(dynamic props, int id)
    {
        try
        {
            foreach (dynamic p in props)
                if ((int)p.PropertyID == id) return (object)p;
        }
        catch (Exception ex) { AppLog.Write($"Couldn't enumerate properties: {ex.Message}"); }
        return null;
    }

    private static int? ReadIntProp(dynamic props, int id)
    {
        try
        {
            object? p = FindProp(props, id);
            if (p is null) return null;
            return Convert.ToInt32(((dynamic)p).Value);
        }
        catch { return null; }
    }

    private static bool IsJpeg(dynamic image)
    {
        try { return string.Equals((string)image.FormatID, FormatJpeg, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static bool IsKnownWiaError(int hr) =>
        hr is WIA_ERROR_PAPER_JAM or WIA_ERROR_BUSY or WIA_ERROR_OFFLINE or WIA_ERROR_COVER_OPEN
            or WIA_S_NO_DEVICE_AVAILABLE or WIA_ERROR_PAPER_EMPTY;

    private static void SetPropByName(dynamic props, string name, object value)
    {
        foreach (dynamic p in props)
        {
            if ((string)p.Name == name) { p.Value = value; return; }
        }
    }

    private static string? ReadProp(dynamic props, string name)
    {
        foreach (dynamic p in props)
        {
            if ((string)p.Name == name) return Convert.ToString(p.Value);
        }
        return null;
    }
}
