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

public record ScanResult(IReadOnlyList<string> Pages); // base64 JPEG per page

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

        try
        {
            if (req.Source != ScanSource.Flatbed)
            {
                int handling = FEEDER | (req.Source == ScanSource.Duplex ? DUPLEX : 0);
                TrySetProp(device.Properties, WIA_DPS_DOCUMENT_HANDLING_SELECT, handling);
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

            while (pages.Count < maxPages)
            {
                dynamic item = device.Items[1];
                ConfigureItem(item, req);

                dynamic image;
                try
                {
                    image = item.Transfer(FormatJpeg);
                }
                catch (COMException ex) when (ex.HResult == WIA_ERROR_PAPER_EMPTY)
                {
                    if (pages.Count == 0)
                        throw new ScanException("The document feeder is empty. Load pages and try again.", 409);
                    break; // normal end of an ADF batch
                }
                catch (COMException ex) when (ex.HResult == unchecked((int)0x80070057)) // E_INVALIDARG
                {
                    // Driver doesn't support JPEG transfer; take its default (usually BMP) and convert below.
                    image = item.Transfer();
                }

                pages.Add(Convert.ToBase64String(ToJpegBytes(image, req.JpegQuality)));

                if (req.Source == ScanSource.Flatbed) break; // one page per flatbed pass
            }
        }
        catch (COMException ex)
        {
            throw Translate(ex);
        }
        finally
        {
            Marshal.FinalReleaseComObject(device);
        }

        return new ScanResult(pages);
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
            }
        }
        throw new ScanException(string.IsNullOrEmpty(deviceId)
            ? "No scanner found. Check that it is powered on and its WIA driver is installed."
            : "The selected scanner is not available.", 404);
    }

    private static void ConfigureItem(dynamic item, ScanRequest req)
    {
        // Intent first: some drivers reset resolution when the intent changes.
        TrySetProp(item.Properties, WIA_IPS_CUR_INTENT, req.Color switch
        {
            ColorMode.Color => 1, ColorMode.Gray => 2, ColorMode.BlackWhite => 4, _ => 1
        });
        TrySetProp(item.Properties, WIA_IPS_XRES, req.Dpi);
        TrySetProp(item.Properties, WIA_IPS_YRES, req.Dpi);
        TrySetProp(item.Properties, WIA_IPS_XPOS, 0);
        TrySetProp(item.Properties, WIA_IPS_YPOS, 0);

        var (wIn, hIn) = req.PageSize switch
        {
            PageSize.A4 => (8.27, 11.69),
            PageSize.Legal => (8.5, 14.0),
            _ => (8.5, 11.0)
        };
        // Drivers clamp/refuse values beyond the glass size; failures are ignored.
        TrySetProp(item.Properties, WIA_IPS_XEXTENT, (int)Math.Round(wIn * req.Dpi));
        TrySetProp(item.Properties, WIA_IPS_YEXTENT, (int)Math.Round(hIn * req.Dpi));
    }

    private static byte[] ToJpegBytes(dynamic image, int quality)
    {
        // Some drivers ignore the requested format and return BMP/TIFF — convert (and recompress) via WIA.ImageProcess.
        dynamic process = CreateCom("WIA.ImageProcess");
        process.Filters.Add(process.FilterInfos["Convert"].FilterID);
        dynamic convert = process.Filters[1];
        SetPropByName(convert.Properties, "FormatID", FormatJpeg);
        SetPropByName(convert.Properties, "Quality", Math.Clamp(quality, 10, 100));
        dynamic jpeg = process.Apply(image);
        return (byte[])jpeg.FileData.BinaryData;
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

    private static void TrySetProp(dynamic props, int id, object value)
    {
        try
        {
            foreach (dynamic p in props)
            {
                if ((int)p.PropertyID == id) { p.Value = value; return; }
            }
        }
        catch (COMException) { /* unsupported by this driver — ignore */ }
    }

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
