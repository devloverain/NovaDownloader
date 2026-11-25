using System.Runtime.InteropServices;
using System.Windows;

namespace NovaDownloader.UI.Services;

/// <summary>
/// Positions browser-triggered dialogs on the monitor where the user's active browser/cursor is located.
/// </summary>
public static class WindowPositionHelper
{
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    public static System.Windows.Forms.Screen CaptureActiveMonitor()
    {
        var hwnd = GetForegroundWindow();
        return hwnd != 0
            ? System.Windows.Forms.Screen.FromHandle(hwnd)
            : System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
    }

    public static bool IsWindowActuallyVisible(Window? window)
        => window != null && window.IsVisible && window.WindowState != WindowState.Minimized;

    public static void CenterOnActiveMonitor(Window window)
        => CenterOnScreen(window, CaptureActiveMonitor());

    public static void CenterOnScreen(Window window, System.Windows.Forms.Screen? screen)
    {
        var target = screen ?? CaptureActiveMonitor();
        var workArea = target.WorkingArea;
        window.WindowStartupLocation = WindowStartupLocation.Manual;

        void OnLoaded(object? sender, RoutedEventArgs e)
        {
            window.Loaded -= OnLoaded;

            var source = PresentationSource.FromVisual(window);
            double scaleX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
            double scaleY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

            var areaLeft = workArea.Left * scaleX;
            var areaTop = workArea.Top * scaleY;
            var areaWidth = workArea.Width * scaleX;
            var areaHeight = workArea.Height * scaleY;

            var w = window.ActualWidth > 0 ? window.ActualWidth : (double.IsNaN(window.Width) ? 500 : window.Width);
            var h = window.ActualHeight > 0 ? window.ActualHeight : (double.IsNaN(window.Height) ? 300 : window.Height);

            window.Left = areaLeft + Math.Max(0, (areaWidth - w) / 2.0);
            window.Top = areaTop + Math.Max(0, (areaHeight - h) / 2.0);
            window.Activate();
        }

        window.Loaded += OnLoaded;
    }
}
