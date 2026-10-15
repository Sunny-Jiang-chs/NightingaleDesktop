using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Nightingale.Desktop.Playback;

public sealed record MpvTrack(long Id, string Type, string Title, string Language, bool Selected, bool AlbumArt = false);
public sealed class MpvFileEventArgs(string path, long generation, long playlistEntryId) : EventArgs
{
    public string Path { get; } = path;
    public long Generation { get; } = generation;
    public long PlaylistEntryId { get; } = playlistEntryId;
}

/// <summary>
/// Owns a libmpv client. Commands and property writes are asynchronous. Events are posted
/// to the SynchronizationContext captured by the constructor (normally WPF's dispatcher).
/// Without a context, handlers run on the event thread. Dispose schedules native teardown
/// on that thread rather than blocking the UI while mpv destroys its child window.
/// </summary>
public sealed class MpvPlayer : IDisposable
{
    private readonly object _gate = new();
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly Thread _events;
    private nint _handle;
    private bool _disposed;
    private ulong _requestId;
    private string? _lastDropTimestamp;
    private readonly Dictionary<ulong, string> _requests = new();
    private readonly Dictionary<ulong, TaskCompletionSource> _completions = new();
    private readonly Dictionary<ulong, LoadRequest> _loadRequests = new();
    private readonly Dictionary<long, MpvFileEventArgs> _entryLoads = new();
    private long _loadGeneration;
    private long _loadedGeneration;
    private long _activeEntryId = -1;
    private sealed record LoadRequest(string Path, long Generation);

    public event EventHandler? FileLoaded;
    public event EventHandler? PlaybackEnded;
    public event EventHandler<MpvFileEventArgs>? FileLoadedDetailed;
    public event EventHandler<MpvFileEventArgs>? PlaybackEndedDetailed;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<string>? NonFatalErrorOccurred;
    public event Action<NativeVideoPointerEvent>? PointerInput;
    public event Action<string[]>? FilesDropped;
    public event Action<string>? ShortcutInput;
    public event Action<bool>? TemporarySpeedRequested;
    public event Action? TemporarySpeedCanceled;
    public string? CurrentPath { get; private set; }
    public string? RequestedPath { get; private set; }
    public long LoadGeneration { get { lock (_gate) return _loadGeneration; } }
    public bool Paused => GetFlag("pause");
    public double Position => GetDouble("time-pos");
    public double Duration => GetDouble("duration");
    public string SubtitleText => GetString("sub-text") ?? "";
    public string Version => GetString("mpv-version") ?? "unknown";

    public MpvPlayer(nint windowHandle)
    {
        if (windowHandle == 0) throw new ArgumentException("视频窗口尚未创建。", nameof(windowHandle));
        _handle = MpvNative.mpv_create();
        if (_handle == 0) throw new InvalidOperationException("无法创建 libmpv 播放器。");
        try
        {
            Option("config", "no");
            Option("load-scripts", "no");
            Option("terminal", "no");
            Option("osc", "no");
            Option("input-default-bindings", "no");
            Option("input-vo-keyboard", "yes");
            Option("native-keyrepeat", "no"); // Preserve explicit key down/up for held shortcuts.
            Option("input-cursor", "yes");
            Option("input-doubleclick-time", Platform.InputTiming.DoubleClickMilliseconds.ToString(CultureInfo.InvariantCulture));
            // The embedded surface belongs to our window. mpv's standalone-window
            // dragging can consume a left-button release before our click handler.
            Option("window-dragging", "no");
            Option("input-terminal", "no");
            Option("input-media-keys", "no");
            Option("input-builtin-drag-and-drop", "no");
            Option("drag-and-drop", "no");
            Option("cursor-autohide", "no");
            Option("wid", windowHandle.ToInt64().ToString(CultureInfo.InvariantCulture));
            Option("idle", "yes");
            Option("keep-open", "no");
            Option("hwdec", "auto-safe");
            Option("vo", "gpu-next,gpu");
            Option("gpu-api", "d3d11");
            Option("gpu-context", "d3d11");
            Option("volume-max", "100");
            Option("sub-auto", "no");
            Option("sub-font", "Microsoft YaHei UI");
            Option("sub-font-size", "40");
            Option("sub-border-size", "2");
            Option("sub-shadow-offset", "1");
            Option("osd-level", "0");
            Check(MpvNative.mpv_initialize(_handle), "初始化播放内核");
            Check(MpvNative.mpv_observe_property(_handle, 1, "mouse-pos", 0), "订阅鼠标事件");
            Check(MpvNative.mpv_observe_property(_handle, 2, "dropped-files", 0), "订阅文件拖放");
        }
        catch
        {
            MpvNative.mpv_terminate_destroy(_handle);
            _handle = 0;
            throw;
        }
        _events = new Thread(EventLoop) { Name = "Nightingale mpv events", IsBackground = true };
        _events.Start();
        // The embedded video owns a native child window. Bind only the actions the host
        // understands and forward them via the documented mpv client-message API.
        Command("define-section", "nightingale-pointer", string.Join('\n',
            "MBTN_LEFT script-binding nightingale-left",
            "MBTN_LEFT_DBL script-message nightingale-pointer double-click",
            "MBTN_RIGHT script-message nightingale-pointer right-up",
            "WHEEL_UP script-message nightingale-pointer wheel-up",
            "WHEEL_DOWN script-message nightingale-pointer wheel-down",
            "SPACE script-message nightingale-shortcut Space",
            "f script-message nightingale-shortcut F",
            "F script-message nightingale-shortcut F",
            "F11 script-message nightingale-shortcut F11",
            "ESC script-message nightingale-shortcut Escape",
            "LEFT script-message nightingale-shortcut Left",
            "RIGHT script-binding nightingale-fast-forward",
            "Ctrl+LEFT script-message nightingale-shortcut Ctrl+Left",
            "Ctrl+RIGHT script-message nightingale-shortcut Ctrl+Right",
            "Shift+LEFT script-message nightingale-shortcut Shift+Left",
            "Shift+RIGHT script-message nightingale-shortcut Shift+Right",
            "UP script-message nightingale-shortcut Up",
            "DOWN script-message nightingale-shortcut Down",
            "m script-message nightingale-shortcut M",
            "M script-message nightingale-shortcut M",
            "l script-message nightingale-shortcut L",
            "L script-message nightingale-shortcut L",
            "HOME script-message nightingale-shortcut Home",
            "[ script-message nightingale-shortcut OemOpenBrackets",
            "] script-message nightingale-shortcut OemCloseBrackets",
            "Ctrl+o script-message nightingale-shortcut Ctrl+O",
            "Ctrl+O script-message nightingale-shortcut Ctrl+Shift+O"), "force");
        Command("enable-section", "nightingale-pointer", "allow-hide-cursor");
    }

    public void Load(string path, double start = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequestedPath = Path.GetFullPath(path);
            var request = new LoadRequest(RequestedPath, ++_loadGeneration);
            foreach (var old in _entryLoads.Where(pair => pair.Value.Generation < _loadGeneration - 8).Select(pair => pair.Key).ToArray())
                _entryLoads.Remove(old);
            SetProperty("pause", "no");
            QueueCommand(["loadfile", RequestedPath, "replace", "-1", $"start={Number(Math.Max(0, start))}"], null, request);
        }
    }

    public void Play() => SetProperty("pause", "no");
    public void Pause() => SetProperty("pause", "yes");
    public void TogglePause() => Command("cycle", "pause");
    public void Stop()
    {
        lock (_gate)
        {
            ++_loadGeneration;
            RequestedPath = null;
            Command("stop");
        }
    }
    public void SeekAbsolute(double seconds) => Command("seek", Number(Math.Max(0, seconds)), "absolute+exact");
    public void SeekRelative(double delta) => Command("seek", Number(delta), "relative+exact");
    public void SetVolume(double volume) => SetProperty("volume", Number(Math.Clamp(volume, 0, 100)));
    public void SetMute(bool mute) => SetProperty("mute", mute ? "yes" : "no");
    public void SetSpeed(double speed) => SetProperty("speed", Number(Math.Clamp(speed, 0.25, 4)));
    public void SetGain(double db)
    {
        db = Math.Clamp(db, -12, 24);
        // FFmpeg's lookahead limiter follows amplification; level=false prevents its
        // automatic make-up gain from undoing the chosen output ceiling.
        SetProperty("af", Math.Abs(db) < 0.001 ? "" : $"lavfi=[volume={Number(db)}dB,alimiter=limit=0.95:level=false:latency=true]");
    }
    public void SetSubtitleFontSize(double size) => SetProperty("sub-font-size", Number(Math.Clamp(size, 16, 80)));
    public void SetSubtitleDelay(double seconds) => SetProperty("sub-delay", Number(seconds));
    public void SetSubtitleMargin(double margin) => SetProperty("sub-margin-y", Number(Math.Round(Math.Clamp(margin, 0, 250))));
    public void AddSubtitle(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (FindExternalSubtitle(fullPath) is { } id) SelectSubtitle(id);
        else Command("sub-add", fullPath, "select");
    }

    /// <summary>Loads/selects the exact external subtitle and waits for mpv to finish.
    /// Returns the real track ID, or null if another file replaced it before completion.
    /// The caller should serialize additions and check its playback generation after await.</summary>
    public async Task<long?> AddSubtitleAsync(string path, long? expectedGeneration = null)
    {
        var fullPath = path.StartsWith("memory://", StringComparison.Ordinal) ? path : Path.GetFullPath(path);
        long generation;
        Task command;
        lock (_gate)
        {
            generation = _loadGeneration;
            if (_loadedGeneration != generation || (expectedGeneration.HasValue && expectedGeneration.Value != generation)) return null;
            // Keep this generation check and enqueue atomic relative to Load(). mpv's
            // sub-add is abort_on_playback_end, so replacing the file cancels an in-flight add.
            command = FindExternalSubtitle(fullPath) is { } existing
                ? CommandAsync("set", "sid", existing.ToString(CultureInfo.InvariantCulture))
                : CommandAsync("sub-add", fullPath, "select");
        }
        try { await command.ConfigureAwait(false); }
        catch (InvalidOperationException) when (generation != LoadGeneration) { return null; }
        return generation == LoadGeneration ? FindExternalSubtitle(fullPath) : null;
    }

    private long? FindExternalSubtitle(string fullPath)
    {
        var count = Math.Min(1000, (int)GetDouble("track-list/count"));
        for (var i = 0; i < count; i++)
        {
            var prefix = $"track-list/{i}/";
            if (GetString(prefix + "type") != "sub") continue;
            var externalPath = GetString(prefix + "external-filename");
            if (externalPath is not null && string.Equals(externalPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return (long)GetDouble(prefix + "id");
            }
        }
        return null;
    }
    public void SelectSubtitle(long id) => SetProperty("sid", id < 0 ? "no" : id.ToString(CultureInfo.InvariantCulture));
    public void SelectAudio(long id) => SetProperty("aid", id < 0 ? "no" : id.ToString(CultureInfo.InvariantCulture));

    public IReadOnlyList<MpvTrack> GetTracks()
    {
        var tracks = new List<MpvTrack>();
        var count = Math.Min(1000, (int)GetDouble("track-list/count"));
        for (var i = 0; i < count; i++)
        {
            var prefix = $"track-list/{i}/";
            tracks.Add(new MpvTrack((long)GetDouble(prefix + "id"), GetString(prefix + "type") ?? "",
                GetString(prefix + "title") ?? "", GetString(prefix + "lang") ?? "", GetFlag(prefix + "selected"), GetFlag(prefix + "albumart")));
        }
        return tracks;
    }

    public string? GetString(string property)
    {
        lock (_gate)
        {
            if (_disposed || _handle == 0) return null;
            var data = MpvNative.mpv_get_property_string(_handle, property);
            if (data == 0) return null;
            try { return Marshal.PtrToStringUTF8(data); }
            finally { MpvNative.mpv_free(data); }
        }
    }

    public double GetDouble(string property) => double.TryParse(GetString(property), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;
    public bool GetFlag(string property) => GetString(property) is "yes" or "true";

    public void SetProperty(string property, string value)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var text = Marshal.StringToCoTaskMemUTF8(value);
            var pointer = Marshal.AllocHGlobal(nint.Size);
            try
            {
                Marshal.WriteIntPtr(pointer, text);
                var id = NewRequest($"设置 {property}");
                var result = MpvNative.mpv_set_property_async(_handle, id, property, 1, pointer);
                if (result < 0) _requests.Remove(id);
                Check(result, $"设置 {property}");
            }
            finally { Marshal.FreeHGlobal(pointer); Marshal.FreeCoTaskMem(text); }
        }
    }

    public void Command(params string[] arguments) => QueueCommand(arguments, null);

    /// <summary>Completes on the matching MPV_EVENT_COMMAND_REPLY. Unlike fire-and-forget
    /// Command, native failures are returned through this Task rather than ErrorOccurred.</summary>
    public Task CommandAsync(params string[] arguments)
    {
        if (arguments.Length == 0) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueCommand(arguments, completion);
        return completion.Task;
    }

    private void QueueCommand(string[] arguments, TaskCompletionSource? completion, LoadRequest? loadRequest = null)
    {
        if (arguments.Length == 0) return;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var strings = new nint[arguments.Length];
            var pointers = Marshal.AllocHGlobal((arguments.Length + 1) * nint.Size);
            try
            {
                for (var i = 0; i < arguments.Length; i++)
                {
                    strings[i] = Marshal.StringToCoTaskMemUTF8(arguments[i]);
                    Marshal.WriteIntPtr(pointers, i * nint.Size, strings[i]);
                }
                Marshal.WriteIntPtr(pointers, arguments.Length * nint.Size, 0);
                var id = NewRequest(arguments[0]);
                if (completion is not null) _completions[id] = completion;
                if (loadRequest is not null) _loadRequests[id] = loadRequest;
                var result = MpvNative.mpv_command_async(_handle, id, pointers);
                if (result < 0) { _requests.Remove(id); _completions.Remove(id); _loadRequests.Remove(id); }
                Check(result, arguments[0]);
            }
            finally
            {
                foreach (var str in strings) if (str != 0) Marshal.FreeCoTaskMem(str);
                Marshal.FreeHGlobal(pointers);
            }
        }
    }

    private ulong NewRequest(string operation)
    {
        var id = ++_requestId;
        _requests[id] = operation;
        return id;
    }

    private void EventLoop()
    {
        try
        {
            while (true)
            {
                lock (_gate) { if (_disposed) break; }
                var data = MpvNative.mpv_wait_event(_handle, 0.2);
                if (data == 0) continue;
                var ev = Marshal.PtrToStructure<MpvNative.Event>(data);
                if (ev.Id == 1) break;
                if (ev.Id is 4 or 5)
                {
                    string? operation;
                    TaskCompletionSource? completion;
                    LoadRequest? loadRequest;
                    lock (_gate)
                    {
                        _requests.Remove(ev.ReplyUserData, out operation);
                        _completions.Remove(ev.ReplyUserData, out completion);
                        _loadRequests.Remove(ev.ReplyUserData, out loadRequest);
                        if (ev.Error >= 0 && loadRequest is not null && ReadCommandEntryId(ev.Data) is { } entryId)
                            _entryLoads[entryId] = new MpvFileEventArgs(loadRequest.Path, loadRequest.Generation, entryId);
                    }
                    if (ev.Error < 0)
                    {
                        var message = $"{operation ?? "播放操作"}失败：{MpvNative.Error(ev.Error)}";
                        if (completion is not null) completion.TrySetException(new InvalidOperationException(message));
                        else if (loadRequest is not null)
                            PostForFile(new MpvFileEventArgs(loadRequest.Path, loadRequest.Generation, -1), () => ErrorOccurred?.Invoke(this, message));
                        else Post(() => NonFatalErrorOccurred?.Invoke(this, message));
                    }
                    else completion?.TrySetResult();
                }
                else if (ev.Id == 6 && ev.Data != 0) _activeEntryId = Marshal.ReadInt64(ev.Data);
                else if (ev.Id == 8)
                {
                    MpvFileEventArgs? file;
                    lock (_gate) _entryLoads.TryGetValue(_activeEntryId, out file);
                    if (file is not null) PostForFile(file, () =>
                    {
                        lock (_gate) { CurrentPath = file.Path; _loadedGeneration = file.Generation; }
                        FileLoadedDetailed?.Invoke(this, file);
                        if (file.Generation == LoadGeneration) FileLoaded?.Invoke(this, EventArgs.Empty);
                    });
                }
                else if (ev.Id == 7 && ev.Data != 0)
                {
                    var end = Marshal.PtrToStructure<MpvNative.EndFile>(ev.Data);
                    MpvFileEventArgs? file;
                    lock (_gate) _entryLoads.Remove(end.PlaylistEntryId, out file);
                    if (file is not null && end.Reason == 0) PostForFile(file, () =>
                    {
                        PlaybackEndedDetailed?.Invoke(this, file);
                        if (file.Generation == LoadGeneration) PlaybackEnded?.Invoke(this, EventArgs.Empty);
                    });
                    else if (file is not null && end.Reason == 4)
                        PostForFile(file, () => ErrorOccurred?.Invoke(this, $"无法播放此文件：{MpvNative.Error(end.Error)}"));
                }
                else if (ev.Id == 16 && ev.Data != 0) HandleClientMessage(ev.Data);
                else if (ev.Id == 22 && ev.ReplyUserData == 1) ForwardPointer(NativeVideoPointerKind.Move);
                else if (ev.Id == 22 && ev.ReplyUserData == 2) HandleFileDrop();
            }
        }
        catch (Exception ex) { RaiseError("播放内核事件异常：" + ex.Message); }
        finally
        {
            nint handle;
            lock (_gate)
            {
                _disposed = true;
                handle = _handle;
                _handle = 0;
                _requests.Clear();
                foreach (var completion in _completions.Values)
                    completion.TrySetException(new ObjectDisposedException(nameof(MpvPlayer)));
                _completions.Clear();
                _loadRequests.Clear();
                _entryLoads.Clear();
            }
            if (handle != 0) MpvNative.mpv_terminate_destroy(handle);
        }
    }

    private void HandleClientMessage(nint data)
    {
        var message = Marshal.PtrToStructure<MpvNative.ClientMessage>(data);
        if (message.Count < 2 || message.Arguments == 0) return;
        var name = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(message.Arguments));
        var action = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(message.Arguments, nint.Size));
        if (name == "key-binding" && message.Count >= 3)
        {
            // script-binding is a native client-message protocol, not a Lua
            // dependency. Unlike script-message it preserves down/up/cancel state.
            var state = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(message.Arguments, 2 * nint.Size));
            if (string.IsNullOrEmpty(state)) return;
            var canceled = state.Length > 2 && state[2] == 'c';
            if (action == "nightingale-fast-forward")
            {
                if (canceled) Post(() => TemporarySpeedCanceled?.Invoke());
                else if (state[0] == 'u') Post(() => TemporarySpeedRequested?.Invoke(false));
                else if (state[0] == 'd') Post(() => TemporarySpeedRequested?.Invoke(true));
            }
            else if (action == "nightingale-left" && !canceled)
            {
                if (state[0] == 'd') ForwardPointer(NativeVideoPointerKind.LeftDown);
                else if (state[0] == 'u') ForwardPointer(NativeVideoPointerKind.LeftUp);
                else if (state[0] == 'p')
                {
                    ForwardPointer(NativeVideoPointerKind.LeftDown);
                    ForwardPointer(NativeVideoPointerKind.LeftUp);
                }
            }
            return;
        }
        if (name == "nightingale-shortcut" && action is not null)
        {
            Post(() => ShortcutInput?.Invoke(action));
            return;
        }
        if (name != "nightingale-pointer") return;
        var kind = action switch
        {
            "left-up" => NativeVideoPointerKind.LeftUp,
            "double-click" => NativeVideoPointerKind.DoubleClick,
            "right-up" => NativeVideoPointerKind.RightUp,
            "wheel-up" or "wheel-down" => NativeVideoPointerKind.Wheel,
            _ => NativeVideoPointerKind.Move
        };
        ForwardPointer(kind, action == "wheel-up" ? 120 : action == "wheel-down" ? -120 : 0);
    }

    private static long? ReadCommandEntryId(nint data)
    {
        // mpv_event_command starts with mpv_node result. loadfile returns the newly
        // allocated playlist entry ID, which remains stable across later replacements.
        if (data == 0) return null;
        var node = Marshal.PtrToStructure<MpvNative.Node>(data);
        if (node.Format != 8 || node.Data == 0) return null;
        var map = Marshal.PtrToStructure<MpvNative.NodeList>(node.Data);
        for (var i = 0; i < Math.Min(map.Count, 100); i++)
        {
            if (Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(map.Keys, i * nint.Size)) != "playlist_entry_id") continue;
            var value = Marshal.PtrToStructure<MpvNative.Node>(map.Values + i * Marshal.SizeOf<MpvNative.Node>());
            return value.Format == 4 ? value.Data.ToInt64() : null;
        }
        return null;
    }

    private void PostForFile(MpvFileEventArgs file, Action action) => Post(() =>
    {
        lock (_gate) { if (file.Generation != _loadGeneration) return; }
        action();
    });

    private void ForwardPointer(NativeVideoPointerKind kind, int delta = 0)
    {
        var input = new NativeVideoPointerEvent(kind, (int)GetDouble("mouse-pos/x"), (int)GetDouble("mouse-pos/y"), delta);
        Post(() => PointerInput?.Invoke(input));
    }

    private void HandleFileDrop()
    {
        var timestamp = GetString("dropped-files/time");
        if (timestamp is null || timestamp == _lastDropTimestamp) return;
        _lastDropTimestamp = timestamp;
        var paths = GetStringList("dropped-files/files")
            .Where(path => Path.IsPathFullyQualified(path) && (File.Exists(path) || Directory.Exists(path)))
            .ToArray();
        if (paths.Length > 0) Post(() => FilesDropped?.Invoke(paths));
    }

    private IReadOnlyList<string> GetStringList(string property)
    {
        lock (_gate)
        {
            var result = new List<string>();
            if (_disposed || _handle == 0 || MpvNative.mpv_get_property(_handle, property, 6, out var node) < 0) return result;
            try
            {
                if (node.Format != 7 || node.Data == 0) return result;
                var list = Marshal.PtrToStructure<MpvNative.NodeList>(node.Data);
                for (var i = 0; i < Math.Min(list.Count, 10000); i++)
                {
                    var value = Marshal.PtrToStructure<MpvNative.Node>(list.Values + i * Marshal.SizeOf<MpvNative.Node>());
                    if (value.Format == 1 && value.Data != 0 && Marshal.PtrToStringUTF8(value.Data) is { } text) result.Add(text);
                }
                return result;
            }
            finally { MpvNative.mpv_free_node_contents(ref node); }
        }
    }

    private void Post(Action action)
    {
        void Invoke() { lock (_gate) { if (_disposed) return; } action(); }
        if (_context is not null) _context.Post(_ => Invoke(), null);
        else Invoke();
    }

    private void RaiseError(string message)
    {
        // An event-loop failure destroys the native client immediately afterward.
        // Keep this final failure notification deliverable after internal disposal.
        if (_context is not null) _context.Post(_ => ErrorOccurred?.Invoke(this, message), null);
        else ErrorOccurred?.Invoke(this, message);
    }
    private void Option(string key, string value) => Check(MpvNative.mpv_set_option_string(_handle, key, value), $"配置 {key}");
    private static string Number(double value) => (double.IsFinite(value) ? value : 0).ToString("0.###", CultureInfo.InvariantCulture);
    private static void Check(int result, string operation)
    {
        if (result < 0) throw new InvalidOperationException($"{operation}失败：{MpvNative.Error(result)}");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            MpvNative.mpv_wakeup(_handle);
        }
    }
}
