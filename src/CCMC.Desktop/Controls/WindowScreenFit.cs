using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CCMC.Desktop.Controls;

/// <summary>
/// Keeps a window fully within the usable work area (screen minus taskbar) of whichever
/// monitor it actually opens on. WPF's own <see cref="SystemParameters.WorkArea"/> always
/// reports the PRIMARY monitor only, which is wrong the moment the app runs on a secondary
/// display - this instead resolves the real monitor via Win32's MonitorFromWindow/
/// GetMonitorInfo (user32.dll, already part of every Windows install - no NuGet package,
/// per this refinement pass's "no new dependencies" rule). Fixes "Milk Reception opens
/// partly off-screen and has to be dragged into view."
/// </summary>
public static class WindowScreenFit
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect32 rcMonitor;
        public Rect32 rcWork;
        public int dwFlags;
    }

    private const int MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    /// <summary>
    /// Call from <c>Window.SourceInitialized</c> (the earliest point a real HWND exists) so
    /// any size/position adjustment happens before the first paint - no visible on-screen
    /// "jump". Never throws: a failure here must not prevent a data-entry window from
    /// opening, it just keeps whatever size/position WPF already computed.
    /// </summary>
    public static void EnsureFitsWorkArea(Window window, double margin = 40)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                ClampToRect(window, SystemParameters.WorkArea, margin);
                return;
            }

            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            {
                ClampToRect(window, SystemParameters.WorkArea, margin);
                return;
            }

            // rcWork is in physical pixels - convert to WPF's device-independent units using
            // this window's own HWND source so the result is correct at any DPI/scaling.
            var dpiScale = HwndSource.FromHwnd(hwnd)?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
            var workArea = new Rect(
                info.rcWork.Left * dpiScale,
                info.rcWork.Top * dpiScale,
                (info.rcWork.Right - info.rcWork.Left) * dpiScale,
                (info.rcWork.Bottom - info.rcWork.Top) * dpiScale);

            ClampToRect(window, workArea, margin);
        }
        catch
        {
            // Never let a positioning helper crash a data-entry window.
        }
    }

    private static void ClampToRect(Window window, Rect workArea, double margin)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0) return;

        var maxHeight = Math.Max(window.MinHeight, workArea.Height - margin);
        var maxWidth = Math.Max(window.MinWidth, workArea.Width - margin);

        if (window.Height > maxHeight) window.Height = maxHeight;
        if (window.Width > maxWidth) window.Width = maxWidth;

        if (window.Top < workArea.Top) window.Top = workArea.Top + (margin / 2);
        if (window.Top + window.Height > workArea.Bottom) window.Top = Math.Max(workArea.Top, workArea.Bottom - window.Height);
        if (window.Left < workArea.Left) window.Left = workArea.Left + (margin / 2);
        if (window.Left + window.Width > workArea.Right) window.Left = Math.Max(workArea.Left, workArea.Right - window.Width);
    }
}
