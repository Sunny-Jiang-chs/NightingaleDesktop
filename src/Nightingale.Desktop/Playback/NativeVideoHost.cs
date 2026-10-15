using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Nightingale.Desktop.Playback;

public enum NativeVideoPointerKind { Move, LeftDown, LeftUp, DoubleClick, RightUp, Wheel }
public sealed record NativeVideoPointerEvent(NativeVideoPointerKind Kind, int X, int Y, int Delta = 0);

/// <summary>
/// Native video surface. Coordinates are physical pixels relative to the video surface.
/// PointerInput and HandleReady execute on the WPF dispatcher. AttachPlayer bridges
/// input from mpv's own child window without subclassing another thread's window.
/// </summary>
public sealed class NativeVideoHost : HwndHost
{
    // mpv deliberately disables its video child for an embedded --wid window.
    // Mouse hit-testing therefore falls through to this host. A Win32 STATIC
    // without SS_NOTIFY reports HTTRANSPARENT and loses the click altogether;
    // own a non-transparent window class instead.
    private const string HostClass = "Nightingale.VideoHost";
    private static readonly NativeWindowProc ClassWindowProc = DefWindowProcW;
    private static readonly Lazy<ushort> HostClassAtom = new(RegisterHostClass);
    private nint _window;
    private MpvPlayer? _player;
    public event Action<nint>? HandleReady;
    public event Action<NativeVideoPointerEvent>? PointerInput;
    public event Action<string[]>? FilesDropped;
    public event Action<string>? ShortcutInput;
    public event Action<bool>? TemporarySpeedRequested;
    public event Action? TemporarySpeedCanceled;

    public NativeVideoHost() { Focusable = false; }

    public void AttachPlayer(MpvPlayer player)
    {
        if (_player is not null)
        {
            _player.PointerInput -= OnPlayerPointer;
            _player.FilesDropped -= OnPlayerFileDrop;
            _player.ShortcutInput -= OnPlayerShortcut;
            _player.TemporarySpeedRequested -= OnPlayerTemporarySpeed;
            _player.TemporarySpeedCanceled -= OnPlayerTemporarySpeedCanceled;
        }
        _player = player;
        _player.PointerInput += OnPlayerPointer;
        _player.FilesDropped += OnPlayerFileDrop;
        _player.ShortcutInput += OnPlayerShortcut;
        _player.TemporarySpeedRequested += OnPlayerTemporarySpeed;
        _player.TemporarySpeedCanceled += OnPlayerTemporarySpeedCanceled;
    }

    private void OnPlayerPointer(NativeVideoPointerEvent input)
    {
        if (Dispatcher.CheckAccess()) PointerInput?.Invoke(input);
        else Dispatcher.BeginInvoke(() => PointerInput?.Invoke(input));
    }

    private void OnPlayerFileDrop(string[] paths)
    {
        if (Dispatcher.CheckAccess()) FilesDropped?.Invoke(paths);
        else Dispatcher.BeginInvoke(() => FilesDropped?.Invoke(paths));
    }

    private void OnPlayerShortcut(string shortcut)
    {
        if (Dispatcher.CheckAccess()) ShortcutInput?.Invoke(shortcut);
        else Dispatcher.BeginInvoke(() => ShortcutInput?.Invoke(shortcut));
    }

    private void OnPlayerTemporarySpeed(bool pressed)
    {
        if (Dispatcher.CheckAccess()) TemporarySpeedRequested?.Invoke(pressed);
        else Dispatcher.BeginInvoke(() => TemporarySpeedRequested?.Invoke(pressed));
    }
    private void OnPlayerTemporarySpeedCanceled()
    {
        if (Dispatcher.CheckAccess()) TemporarySpeedCanceled?.Invoke();
        else Dispatcher.BeginInvoke(() => TemporarySpeedCanceled?.Invoke());
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        const int style = 0x40000000 | 0x10000000 | 0x02000000 | 0x04000000; // child, visible, clip children/siblings
        _ = HostClassAtom.Value;
        _window = CreateWindowExW(0, HostClass, "Nightingale video", style,
            0, 0, Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight), hwndParent.Handle,
            0, 0, 0);
        if (_window == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var window = _window;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_window == window) HandleReady?.Invoke(window);
        });
        return new HandleRef(this, _window);
    }

    protected override nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // The mpv child supplies its own mouse messages through AttachPlayer. This
        // fallback covers the empty host before the first video is opened.
        NativeVideoPointerKind? kind = msg switch
        {
            0x0200 => NativeVideoPointerKind.Move,
            0x0201 => NativeVideoPointerKind.LeftDown,
            0x0202 => NativeVideoPointerKind.LeftUp,
            0x0203 => NativeVideoPointerKind.DoubleClick,
            0x0205 => NativeVideoPointerKind.RightUp,
            0x020A => NativeVideoPointerKind.Wheel,
            _ => null
        };
        if (kind is not null)
        {
            var point = new NativePoint { X = (short)(lParam.ToInt64() & 0xffff), Y = (short)((lParam.ToInt64() >> 16) & 0xffff) };
            if (kind == NativeVideoPointerKind.Wheel) ScreenToClient(hwnd, ref point);
            var delta = kind == NativeVideoPointerKind.Wheel ? (short)((wParam.ToInt64() >> 16) & 0xffff) : 0;
            PointerInput?.Invoke(new NativeVideoPointerEvent(kind.Value, point.X, point.Y, delta));
            if (kind == NativeVideoPointerKind.LeftDown)
            {
                // Keep keyboard shortcuts in WPF when the click came through the
                // native HWND. mpv's disabled child cannot own keyboard focus.
                Window.GetWindow(this)?.Focus();
            }
        }
        if (msg == 0x0021) // WM_MOUSEACTIVATE: a video click activates our application.
        {
            handled = true;
            return 1; // MA_ACTIVATE; keep the click, including when another app was active.
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (_player is not null)
        {
            _player.PointerInput -= OnPlayerPointer;
            _player.FilesDropped -= OnPlayerFileDrop;
            _player.ShortcutInput -= OnPlayerShortcut;
            _player.TemporarySpeedRequested -= OnPlayerTemporarySpeed;
            _player.TemporarySpeedCanceled -= OnPlayerTemporarySpeedCanceled;
        }
        _player = null;
        DestroyWindow(hwnd.Handle);
        _window = 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public nint WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ClassName;
        public nint SmallIcon;
    }
    private delegate nint NativeWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    private static ushort RegisterHostClass()
    {
        var windowClass = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            Style = 0x0008, // CS_DBLCLKS: deliver WM_LBUTTONDBLCLK.
            WindowProc = Marshal.GetFunctionPointerForDelegate(ClassWindowProc),
            Instance = GetModuleHandleW(null),
            Cursor = LoadCursorW(0, 32512), // IDC_ARROW
            ClassName = HostClass
        };
        var atom = RegisterClassExW(ref windowClass);
        if (atom == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return atom;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClassEx windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadCursorW(nint instance, nint cursorName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ScreenToClient(nint hwnd, ref NativePoint point);
}
