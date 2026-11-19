namespace ScanBridge;

/// <summary>Turns scanner and network error codes into messages people can act on.</summary>
public static class ScanErrors
{
    public static ScanException FromHResult(int hr, string? detail = null) => (uint)hr switch
    {
        0x80210003 => new ScanException("The document feeder is empty. Load pages, check the right scanner is selected, and try again.", 409),
        0x80210002 => new ScanException("Paper jam. Clear the scanner and try again.", 409),
        0x80210006 => new ScanException("The scanner is busy. Try again in a moment.", 409),
        0x80210005 => new ScanException("The scanner is offline.", 503),
        0x80210016 => new ScanException("The scanner cover is open.", 409),
        0x80000019 => new ScanException("The scanner was still finishing the last scan. Wait a few seconds and try again.", 409),
        // Network scanners (WSD / eSCL) reached over the network
        0x80072EE2 => new ScanException("The scanner didn't respond in time. It may be asleep or off the network: wake it up (or wait for it to finish warming up) and try again.", 504),
        0x80072EFD or 0x80072EFE or 0x80072F78 or 0x80072EFF =>
            new ScanException("Couldn't reach the scanner over the network. Check it's on and connected to the same network as this PC, then try again.", 503),
        0x8007045A or 0x80070079 =>
            new ScanException("The scanner stopped responding. Try again; if it keeps happening, restart the scanner.", 504),
        _ => new ScanException(string.IsNullOrWhiteSpace(detail) ? $"Scanner error 0x{hr:X8}." : $"Scanner error 0x{hr:X8}: {detail}")
    };
}
