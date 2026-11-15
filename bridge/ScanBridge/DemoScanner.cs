using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ScanBridge;

/// <summary>
/// A virtual scanner that produces sample pages. It is listed only when Windows reports no real scanner,
/// so the add-in can be tried (and reviewed) on a PC without scanning hardware.
/// </summary>
public static class DemoScanner
{
    public const string Id = "scan2pdf-demo";

    public static ScannerInfo Info { get; } = new(Id, "Demo scanner (sample pages)", "Scan 2 PDF");

    public static ScanResult Scan(ScanRequest req, int maxPages)
    {
        int count = req.Source == ScanSource.Flatbed ? 1 : Math.Min(3, maxPages) * (req.Source == ScanSource.Duplex ? 2 : 1);
        int dpi = Math.Clamp(req.Dpi, 75, 300);
        var (wIn, hIn) = req.PageSize switch
        {
            PageSize.A4 => (8.27, 11.69),
            PageSize.Legal => (8.5, 14.0),
            _ => (8.5, 11.0)
        };

        var pages = new List<string>();
        for (int i = 1; i <= count; i++)
            pages.Add(Convert.ToBase64String(RenderPage(i, count, dpi, wIn, hIn, req)));

        Thread.Sleep(600); // feel like a scan
        return new ScanResult(pages);
    }

    private static byte[] RenderPage(int n, int total, int dpi, double wIn, double hIn, ScanRequest req)
    {
        int w = (int)Math.Round(wIn * dpi), h = (int)Math.Round(hIn * dpi);
        using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        bmp.SetResolution(dpi, dpi);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.White);

            float u = dpi / 100f; // 1/100 inch
            var accent = req.Color == ColorMode.Color ? Color.FromArgb(29, 78, 216) : Color.FromArgb(40, 40, 40);
            var line = req.Color == ColorMode.Color ? Color.FromArgb(203, 213, 225) : Color.FromArgb(200, 200, 200);

            using var accentBrush = new SolidBrush(accent);
            using var lineBrush = new SolidBrush(line);
            using var textBrush = new SolidBrush(Color.FromArgb(30, 41, 59));
            using var titleFont = new Font("Segoe UI", 22, FontStyle.Bold, GraphicsUnit.Point);
            using var bodyFont = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Point);

            g.FillRectangle(accentBrush, 75 * u, 75 * u, w - 150 * u, 90 * u);
            g.DrawString("Scan 2 PDF — sample page", titleFont, Brushes.White, 95 * u, 95 * u);
            g.DrawString($"Page {n} of {total}  ·  {dpi} dpi  ·  {req.Color}  ·  {req.PageSize}  ·  {req.Source}",
                bodyFont, textBrush, 75 * u, 190 * u);
            g.DrawString("This page was produced by the demo scanner because no real scanner was found on this PC.",
                bodyFont, textBrush, new RectangleF(75 * u, 225 * u, w - 150 * u, 60 * u));

            for (float y = 320 * u; y < h - 150 * u; y += 32 * u)
                g.FillRectangle(lineBrush, 75 * u, y, (w - 150 * u) * (0.6f + 0.4f * (float)((y / u * 7 % 10) / 10)), 10 * u);

            g.DrawString("© Aghapy Tech Services", bodyFont, textBrush, 75 * u, h - 100 * u);
        }

        if (req.Color == ColorMode.Gray || req.Color == ColorMode.BlackWhite)
            ToGray(bmp, threshold: req.Color == ColorMode.BlackWhite);

        using var ms = new MemoryStream();
        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(req.JpegQuality, 10, 100));
        bmp.Save(ms, jpeg, ep);
        return ms.ToArray();
    }

    private static void ToGray(Bitmap bmp, bool threshold)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            for (int y = 0; y < data.Height; y++)
            {
                int row = y * data.Stride;
                for (int x = 0; x < data.Width; x++)
                {
                    int i = row + x * 3;
                    byte v = (byte)((bytes[i] * 11 + bytes[i + 1] * 59 + bytes[i + 2] * 30) / 100);
                    if (threshold) v = v > 160 ? (byte)255 : (byte)0;
                    bytes[i] = bytes[i + 1] = bytes[i + 2] = v;
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
