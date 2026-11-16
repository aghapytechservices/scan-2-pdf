using System.Diagnostics;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace ScanBridge;

/// <summary>
/// Microsoft Store rating for the Store (MSIX) edition: shows the Store's own "Rate and review" dialog,
/// falling back to the app's review page in the Store app.
/// </summary>
public static class StoreReview
{
    public static bool IsAvailable => AppPaths.IsPackaged;

    /// <summary>Must be called on the UI (tray) thread with a window handle to parent the dialog.</summary>
    public static async Task<string> RequestAsync(IntPtr ownerHwnd)
    {
        if (!IsAvailable) return "unavailable";
        try
        {
            var context = StoreContext.GetDefault();
            WinRT.Interop.InitializeWithWindow.Initialize(context, ownerHwnd);
            var result = await context.RequestRateAndReviewAppAsync();
            AppLog.Write($"Store rating dialog: {result.Status} (updated: {result.WasUpdated})");
            if (result.Status is StoreRateAndReviewStatus.Succeeded or StoreRateAndReviewStatus.CanceledByUser)
                return result.Status.ToString();
        }
        catch (Exception ex)
        {
            AppLog.Write("Store rating dialog failed; opening the Store review page instead", ex);
        }

        return OpenReviewPage() ? "OpenedStorePage" : "Error";
    }

    /// <summary>Opens the review page for this package in the Microsoft Store app.</summary>
    public static bool OpenReviewPage()
    {
        try
        {
            var pfn = Package.Current.Id.FamilyName;
            Process.Start(new ProcessStartInfo($"ms-windows-store://review/?PFN={Uri.EscapeDataString(pfn)}") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write("Couldn't open the Store review page", ex);
            return false;
        }
    }
}
