using System.Runtime.InteropServices;

namespace Nightingale.Desktop.Platform;

internal static class InputTiming
{
    public static int DoubleClickMilliseconds => (int)GetDoubleClickTime();

    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
}
