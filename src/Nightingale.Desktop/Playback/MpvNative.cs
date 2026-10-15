using System.Runtime.InteropServices;

namespace Nightingale.Desktop.Playback;

internal static class MpvNative
{
    private const string Library = "mpv-2.dll";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint mpv_create();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_initialize(nint handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_terminate_destroy(nint handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_set_option_string(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_set_property_async(nint handle, ulong reply, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, nint value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint mpv_get_property_string(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_command_async(nint handle, ulong reply, nint arguments);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint mpv_wait_event(nint handle, double timeout);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_wakeup(nint handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_free(nint data);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint mpv_error_string(int error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_observe_property(nint handle, ulong reply, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_get_property(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out Node value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_free_node_contents(ref Node value);

    [StructLayout(LayoutKind.Sequential)] internal struct Node { public nint Data; public int Format; }
    [StructLayout(LayoutKind.Sequential)] internal struct NodeList { public int Count; public nint Values; public nint Keys; }

    [StructLayout(LayoutKind.Sequential)] internal struct Event
    {
        public int Id;
        public int Error;
        public ulong ReplyUserData;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct EndFile
    {
        public int Reason;
        public int Error;
        public long PlaylistEntryId;
        public long PlaylistInsertId;
        public int PlaylistInsertNumEntries;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct ClientMessage
    {
        public int Count;
        public nint Arguments;
    }

    internal static string Error(int code) => Marshal.PtrToStringUTF8(mpv_error_string(code)) ?? $"mpv error {code}";
}
