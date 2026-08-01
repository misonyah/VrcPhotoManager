using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace VrcPhotoManager.Views;

/// <summary>
/// Shared chrome/dismissal behavior for the app's secondary (non-modal, no Owner - see
/// MainWindow.xaml.cs.OpenTagFaces for why) windows - hiding just the minimize/maximize
/// buttons and closing on click-outside aren't things WPF's Window exposes directly, so both
/// live here instead of being duplicated per window.
/// </summary>
internal static class DialogWindowBehavior
{
    private const int GWL_STYLE = -16;
    private const int WS_MINIMIZEBOX = 0x20000;
    private const int WS_MAXIMIZEBOX = 0x10000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    /// <summary>
    /// WPF's ResizeMode/WindowStyle can't hide just minimize+maximize while keeping a normal
    /// title bar and close button - "NoResize" still shows both buttons (merely disabled), and
    /// "ToolWindow" changes the whole chrome (no icon, smaller title bar). Clearing the two
    /// Win32 style bits directly is the only way to get an ordinary-looking dialog with just a
    /// close button. Must wait for SourceInitialized - the HWND doesn't exist at construction.
    /// </summary>
    public static void HideMinimizeAndMaximizeButtons(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            int style = GetWindowLong(hwnd, GWL_STYLE);
            SetWindowLong(hwnd, GWL_STYLE, style & ~WS_MINIMIZEBOX & ~WS_MAXIMIZEBOX);
        };
    }

    /// <summary>
    /// Closes the window when it loses activation (e.g. the user clicks the main window behind
    /// it) - WPF has no native "click outside to dismiss" for a real Window, only for Popup, so
    /// Deactivated is the practical equivalent. Requires the window to have no Owner: Win32
    /// always keeps an owned window above its owner regardless of which one is focused, so
    /// clicking "outside" would just get shoved behind the still-owned dialog instead of
    /// bringing it forward - see MainWindow.xaml.cs.OpenTagFaces for the concrete symptom this
    /// caused. `stillOpenGuard`, if given, skips the close when it returns true - needed by
    /// windows (like Tag Faces) that open their own internal Popup, since a transparent
    /// Popup's HWND stealing keyboard focus can itself trigger Deactivated even though the
    /// user didn't click away at all.
    ///
    /// Guards against re-entrancy: ANY close - including a normal click on the X button, not
    /// just this method's own Close() call - deactivates the window as part of tearing down,
    /// which fires this same Deactivated handler while the window is already mid-close. Calling
    /// Close() again at that point throws "Cannot set Visibility or call Show, ShowDialog,
    /// Close... while a Window is closing" (found via a real crash report - clicking X alone
    /// was enough to trigger it, since Deactivated doesn't know the close already started
    /// elsewhere). Hooking Closing - which fires the instant ANY close begins, before the
    /// teardown-triggered Deactivated - is what actually distinguishes "already closing,
    /// ignore" from "still fully open, this is a genuine click-away." A flag set from inside
    /// the Deactivated handler itself (the previous, insufficient fix) can't make that
    /// distinction because it only catches re-entry through this same handler.
    /// </summary>
    public static void CloseOnDeactivated(Window window, Func<bool>? stillOpenGuard = null)
    {
        bool closing = false;
        window.Closing += (_, _) => closing = true;
        window.Deactivated += (_, _) =>
        {
            if (closing) return;
            if (stillOpenGuard?.Invoke() == true) return;
            closing = true;
            window.Close();
        };
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// Centers the window on the mouse cursor instead of the OS/WPF default (screen center, or
    /// wherever Windows happens to cascade new windows) - these are quick, often-reopened
    /// utility windows, so keeping them where the user's attention already is saves eye/mouse
    /// travel. GetCursorPos is used instead of System.Windows.Forms.Cursor to avoid pulling in
    /// a WinForms reference for one P/Invoke call.
    ///
    /// Runs at Loaded, not SourceInitialized: true centering needs the window's final rendered
    /// size (ActualWidth/ActualHeight), which for a SizeToContent window (like AboutWindow)
    /// isn't known until at least one layout pass has run - Loaded is the first point that's
    /// guaranteed. VisualTreeHelper.GetDpi also needs a live HwndSource to know which monitor's
    /// DPI scale applies, and screen-pixel cursor coordinates must be converted to WPF's
    /// device-independent units or the window lands in the wrong place on any non-100% display.
    /// </summary>
    public static void OpenNearCursor(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Loaded += (_, _) =>
        {
            if (!GetCursorPos(out POINT cursor)) return;
            var dpi = VisualTreeHelper.GetDpi(window);
            double cursorLeft = cursor.X / dpi.DpiScaleX;
            double cursorTop = cursor.Y / dpi.DpiScaleY;

            double left = cursorLeft - window.ActualWidth / 2;
            double top = cursorTop - window.ActualHeight / 2;

            // Basic on-primary-screen clamping - keeps the window from opening partly off any
            // edge when the cursor is near it. Doesn't account for secondary monitors with
            // different work areas; good enough for the common case.
            double maxLeft = Math.Max(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right - window.ActualWidth);
            double maxTop = Math.Max(SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Bottom - window.ActualHeight);
            window.Left = Math.Clamp(left, SystemParameters.WorkArea.Left, maxLeft);
            window.Top = Math.Clamp(top, SystemParameters.WorkArea.Top, maxTop);
        };
    }
}
