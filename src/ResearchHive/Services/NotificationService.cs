using ResearchHive.Core.Configuration;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ResearchHive.Services;

/// <summary>
/// Notifies users when long-running jobs complete via taskbar flash + system sound.
/// Respects the NotificationsEnabled setting in AppSettings.
/// Only triggers when the app window is not focused (user switched away).
/// </summary>
public class NotificationService
{
    private readonly AppSettings _settings;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_ALL = 3;   // Flash caption + taskbar
    private const uint FLASHW_TIMERNOFG = 12; // Flash until foreground

    public NotificationService(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>Show a notification for a completed research job.</summary>
    public void NotifyResearchComplete(string sessionTitle, string jobSummary, int sourcesAcquired)
    {
        if (!_settings.NotificationsEnabled) return;
        if (IsAppFocused()) return;
        FlashAndSound();
    }

    /// <summary>Show a notification for a completed discovery job.</summary>
    public void NotifyDiscoveryComplete(string sessionTitle, int ideaCount)
    {
        if (!_settings.NotificationsEnabled) return;
        if (IsAppFocused()) return;
        FlashAndSound();
    }

    /// <summary>Show a notification for a completed repo scan.</summary>
    public void NotifyRepoScanComplete(string repoName)
    {
        if (!_settings.NotificationsEnabled) return;
        if (IsAppFocused()) return;
        FlashAndSound();
    }

    /// <summary>Show a generic job completion notification.</summary>
    public void NotifyJobComplete(string title, string detail)
    {
        if (!_settings.NotificationsEnabled) return;
        if (IsAppFocused()) return;
        FlashAndSound();
    }

    /// <summary>Clean up resources on app exit.</summary>
    public static void Cleanup() { }

    private static void FlashAndSound()
    {
        try
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                var window = Application.Current?.MainWindow;
                if (window == null) return;

                var helper = new WindowInteropHelper(window);
                var fi = new FLASHWINFO
                {
                    cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                    hwnd = helper.Handle,
                    dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                    uCount = 3,
                    dwTimeout = 0
                };
                FlashWindowEx(ref fi);

                // Play system notification sound
                SystemSounds.Asterisk.Play();
            });
        }
        catch { /* Notification failures should never crash the app */ }
    }

    private static bool IsAppFocused()
    {
        try
        {
            return Application.Current?.MainWindow?.IsActive == true;
        }
        catch { return false; }
    }
}
