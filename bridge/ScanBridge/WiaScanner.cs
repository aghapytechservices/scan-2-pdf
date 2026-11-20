using System.Runtime.InteropServices;

namespace ScanBridge;

public record ScannerInfo(string Id, string Name, string Manufacturer);

public enum ScanSource { Flatbed, Feeder, Duplex }
public enum ColorMode { Color, Gray, BlackWhite }
public enum PageSize { Letter, Legal, A4 }

public record ScanRequest(
    string? DeviceId,
    string? DeviceName = null, // what the add-in showed; used when the ID isn't found (network scanners come and go)
    ScanSource Source = ScanSource.Flatbed,
    int Dpi = 200,
    ColorMode Color = ColorMode.Color,
    PageSize PageSize = PageSize.Letter,
    int JpegQuality = 75);

/// <param name="Pages">base64 JPEG per page</param>
/// <param name="Warning">Set when the scanner stopped with an error part-way; the pages scanned so far are still returned.</param>
/// <param name="Note">Informational, e.g. when the scanner can't confirm the feeder is empty.</param>
public record ScanResult(IReadOnlyList<string> Pages, string? Warning = null, string? Note = null);

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
        bool feeder = req.Source != ScanSource.Flatbed;
        object device = Connect(req.DeviceId);
        var pages = new List<string>();
        var step = "preparing the scanner";

        try
        {
            step = feeder ? "selecting the document feeder" : "preparing the scanner";
            PrepareDevice(device, req);
            LogDeviceLayout(device);
            object item = SelectItem(device, feeder);

            // Settings are applied once, before the first page. Re-applying them between feeder pages
            // makes some drivers reject the next transfer.
            step = "applying scan settings";
            int dpi = ConfigureItem(item, req);
            bool jpegUnsupported = false;
            string? warning = null;
            string? note = null;
            bool canDetectPaper = feeder && CanDetectFeederPaper(device);
            if (feeder) AppLog.Write(canDetectPaper ? "Feeder can detect paper." : "Feeder can't detect paper; relying on transfer results.");

            while (pages.Count < maxPages)
            {
                if (ScanProgress.StopRequested)
                {
                    AppLog.Write($"Stopped by the user after {pages.Count} page(s).");
                    note = ScanProgress.StoppedNote(pages.Count);
                    break;
                }
                object? image;
                if (feeder && pages.Count > 0)
                {
                    step = $"scanning page {pages.Count + 1}";
                    if (canDetectPaper && FeederReady(device) == false)
                    {
                        AppLog.Write("Feeder reports no more paper.");
                        break;
                    }
                    try
                    {
                        image = NextFeederPage(ref device, ref item, req, ref jpegUnsupported, out bool confirmedEmpty);
                        if (image is null && !confirmedEmpty)
                            note = $"Scanned {pages.Count} page{(pages.Count == 1 ? "" : "s")}. If any are still in the feeder, click Scan again to add them.";
                    }
                    catch (Exception ex)
                    {
                        // Keep what we have: a later page failing (jam, cover open...) shouldn't throw away the earlier ones.
                        AppLog.Write($"Scanner stopped after {pages.Count} page(s) (HRESULT 0x{ex.HResult:X8})", ex);
                        warning = StoppedWarning(ex, pages.Count);
                        break;
                    }
                }
                else
                {
                    step = "scanning";
                    image = TransferPage(item, ref jpegUnsupported);
                }

                if (image is null) // feeder empty
                {
                    if (pages.Count == 0)
                        throw new ScanException("The document feeder is empty. Load pages, check the right scanner is selected, and try again.", 409);
                    break; // normal end of a feeder batch
                }

                step = "converting the scanned page";
                pages.Add(Convert.ToBase64String(ToJpegBytes(image, req.JpegQuality)));
                AppLog.Write($"Page {pages.Count} scanned at {dpi} dpi");
                ScanProgress.Pages(pages.Count);

                if (!feeder) break; // one page per flatbed pass
            }

            AppLog.Write($"Scan complete: {pages.Count} page(s){(warning is null ? "" : " (stopped early)")}{(note is null ? "" : " (end not confirmed)")}");
            return new ScanResult(pages, warning, note);
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
            try { Marshal.FinalReleaseComObject(device); } catch { /* already released */ }
        }
    }

    private const int DETECT_FEED = 0x020;
    private static readonly int[] NextPageDelaysMs = [1500, 2000, 3500, 5000];

    private static bool CanDetectFeederPaper(object deviceObj)
    {
        dynamic device = deviceObj;
        int? caps = ReadIntProp(device.Properties, WIA_DPS_DOCUMENT_HANDLING_CAPABILITIES);
        return caps is int c && (c & DETECT_FEED) != 0;
    }

    /// <summary>
    /// Gets the next sheet from the feeder. Many scanners are busy for a moment after each sheet and
    /// refuse (E_INVALIDARG) or report "empty" if asked immediately, so wait and retry, reconnecting once.
    /// Returns null at the end of the stack; <paramref name="confirmedEmpty"/> says whether the scanner
    /// clearly reported an empty feeder (twice) or we simply ran out of retries.
    /// </summary>
    private static object? NextFeederPage(ref object device, ref object item, ScanRequest req,
        ref bool jpegUnsupported, out bool confirmedEmpty)
    {
        int emptyReports = 0;
        for (int attempt = 0; attempt < NextPageDelaysMs.Length; attempt++)
        {
            Thread.Sleep(NextPageDelaysMs[attempt]);
            if (attempt == 2)
            {
                AppLog.Write("Reconnecting to the scanner for the next page.");
                (device, item) = Reconnect(device, req, feeder: true);
            }

            try
            {
                var image = TransferPage(item, ref jpegUnsupported);
                if (image is not null)
                {
                    if (attempt > 0) AppLog.Write($"Next page arrived on attempt {attempt + 1}.");
                    confirmedEmpty = false;
                    return image;
                }
                emptyReports++;
                AppLog.Write($"Feeder reported empty (attempt {attempt + 1}).");
                if (emptyReports >= 2)
                {
                    confirmedEmpty = true;
                    return null;
                }
            }
            catch (Exception ex) when (ex.HResult == E_INVALIDARG || ex.HResult == WIA_ERROR_BUSY)
            {
                AppLog.Write($"Scanner not ready for the next page (attempt {attempt + 1}, 0x{ex.HResult:X8}).");
            }
        }

        AppLog.Write("No more pages after retries; the scanner didn't confirm the feeder is empty.");
        confirmedEmpty = false;
        return null;
    }

    private static string StoppedWarning(Exception ex, int count)
    {
        var pagesText = $"{count} page{(count == 1 ? "" : "s")}";
        return ex is COMException ce && IsKnownWiaError(ce.HResult)
            ? $"The scanner stopped after {pagesText}: {Translate(ce).Message}"
            : $"The scanner stopped after {pagesText}. Check the feeder and scan the rest.";
    }

    /// <summary>Device-level settings: feeder/flatbed selection, one page per transfer, paper size.</summary>
    private static void PrepareDevice(object deviceObj, ScanRequest req)
    {
        dynamic device = deviceObj;
        if (req.Source != ScanSource.Flatbed)
        {
            int handling = FEEDER | (req.Source == ScanSource.Duplex ? DUPLEX : 0);
            if (!TrySetProp(device.Properties, WIA_DPS_DOCUMENT_HANDLING_SELECT, handling) && !HasItemNamed(deviceObj, "feeder"))
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
    }

    /// <summary>
    /// Newer (WIA 2.0) scanners expose separate "Flatbed" and "Feeder" items; older ones have a single item.
    /// Use the one matching the chosen source when it exists.
    /// </summary>
    private static object SelectItem(object deviceObj, bool feeder)
    {
        dynamic device = deviceObj;
        string wanted = feeder ? "feeder" : "flatbed";
        try
        {
            foreach (dynamic it in device.Items)
            {
                var name = ReadProp(it.Properties, "Item Name") ?? "";
                if (name.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    AppLog.Write($"Using scanner item \"{name}\"");
                    return (object)it;
                }
            }
        }
        catch (Exception ex) { AppLog.Write("Couldn't list scanner items", ex); }
        return (object)device.Items[1];
    }

    private static bool HasItemNamed(object deviceObj, string part)
    {
        dynamic device = deviceObj;
        try
        {
            foreach (dynamic it in device.Items)
            {
                string name = ReadProp(it.Properties, "Item Name") ?? "";
                if (name.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { /* treat as not found */ }
        return false;
    }

    private static (object Device, object Item) Reconnect(object oldDevice, ScanRequest req, bool feeder)
    {
        try { Marshal.FinalReleaseComObject(oldDevice); } catch { /* ignore */ }
        object device = Connect(req.DeviceId);
        PrepareDevice(device, req);
        object item = SelectItem(device, feeder);
        ConfigureItem(item, req);
        return (device, item);
    }

    private const int WIA_DPS_DOCUMENT_HANDLING_CAPABILITIES = 3086;
    private const int WIA_DPS_DOCUMENT_HANDLING_STATUS = 3087;
    private const int FEED_READY = 0x001;

    /// <summary>True/false from the feeder's "paper ready" flag, or null if the driver doesn't report it.</summary>
    private static bool? FeederReady(object deviceObj)
    {
        dynamic device = deviceObj;
        int? status = ReadIntProp(device.Properties, WIA_DPS_DOCUMENT_HANDLING_STATUS);
        return status is null ? null : (status.Value & FEED_READY) != 0;
    }

    private static void LogDeviceLayout(object deviceObj)
    {
        dynamic device = deviceObj;
        try
        {
            var names = new List<string>();
            foreach (dynamic it in device.Items) names.Add(ReadProp(it.Properties, "Item Name") ?? "?");
            int? caps = ReadIntProp(device.Properties, WIA_DPS_DOCUMENT_HANDLING_CAPABILITIES);
            int? status = ReadIntProp(device.Properties, WIA_DPS_DOCUMENT_HANDLING_STATUS);
            AppLog.Write($"Scanner items: [{string.Join(", ", names)}]; handling capabilities: {caps?.ToString("X") ?? "n/a"}; status: {status?.ToString("X") ?? "n/a"}");
        }
        catch (Exception ex) { AppLog.Write("Couldn't read scanner layout", ex); }
    }

    /// <summary>
    /// Transfers one page. Returns null when the feeder is empty.
    /// Falls back from JPEG to the driver's default format once, and remembers that for later pages.
    /// </summary>
    private static object? TransferPage(object itemObj, ref bool jpegUnsupported)
    {
        dynamic item = itemObj;
        if (!jpegUnsupported)
        {
            try
            {
                return (object)item.Transfer(FormatJpeg);
            }
            catch (Exception ex) when (ex.HResult == WIA_ERROR_PAPER_EMPTY)
            {
                return null;
            }
            catch (Exception ex) when (ex.HResult == E_INVALIDARG)
            {
                AppLog.Write("JPEG transfer rejected by driver; trying its default format.");
                jpegUnsupported = true;
            }
        }

        try
        {
            return (object)item.Transfer();
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
            : "Windows can't see the selected scanner right now. If it's a network scanner, check it's on and awake, wait a few seconds, then try again.", 404);
    }

    /// <summary>Applies settings, fitting each to what the driver supports. Returns the resolution actually used.</summary>
    private static int ConfigureItem(object itemObj, ScanRequest req)
    {
        dynamic item = itemObj;
        dynamic props = item.Properties;

        // Intent first: some drivers reset resolution when the intent changes.
        TrySetProp(props, WIA_IPS_CUR_INTENT, req.Color switch
        {
            ColorMode.Color => 1, ColorMode.Gray => 2, ColorMode.BlackWhite => 4, _ => 1
        });

        TrySetProp(props, WIA_IPS_XRES, req.Dpi);
        int xDpi = ReadIntProp(props, WIA_IPS_XRES) ?? req.Dpi;
        // Many drivers move the vertical resolution with the horizontal one; only set it if it differs.
        if (ReadIntProp(props, WIA_IPS_YRES) is int currentY && currentY != xDpi)
            TrySetProp(props, WIA_IPS_YRES, xDpi);
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
        WIA_ERROR_PAPER_EMPTY => new ScanException("The document feeder is empty. Load pages, check the right scanner is selected, and try again.", 409),
        _ => ScanErrors.FromHResult(ex.HResult, ex.Message)
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
