using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Nightingale.Desktop;

internal sealed class FullscreenControlsWindow : Window
{
    internal const double BarHeight = 106;
    public PlayerControls Controls { get; } = new();
    public FullscreenControlsWindow()
    {
        SetResourceReference(StyleProperty, "AppWindowStyle");
        Title = "夜莺 · 全屏控制";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Height = BarHeight;
        Background = new SolidColorBrush(Color.FromRgb(24, 29, 35));
        Content = Controls;
    }
}

internal static class DesktopGeometry
{
    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public RectI Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out RectI rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    public static void FillMonitor(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor)) return;
        var r = monitor.Monitor;
        SetWindowPos(handle, 0, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, 0x0004 | 0x0020);
    }
    public static void PositionControls(Window parent, Window controls)
    {
        var parentHandle = new WindowInteropHelper(parent).Handle;
        var controlHandle = new WindowInteropHelper(controls).Handle;
        if (controlHandle == 0 || !GetWindowRect(parentHandle, out var r)) return;
        var height = (int)Math.Round(FullscreenControlsWindow.BarHeight * Math.Max(96u, GetDpiForWindow(parentHandle)) / 96.0);
        SetWindowPos(controlHandle, 0, r.Left, r.Bottom - height, r.Right - r.Left, height, 0x0004 | 0x0010);
    }
}
