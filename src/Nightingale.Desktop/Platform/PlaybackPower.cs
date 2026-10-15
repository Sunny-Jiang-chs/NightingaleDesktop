using System.Runtime.InteropServices;

namespace Nightingale.Desktop.Platform;

/// <summary>
/// Keeps the system awake during playback and optionally also the display for
/// video. Create, call, and dispose on the UI thread because execution state is
/// per-thread. Paused playback, errors, and shutdown must clear both requests.
/// </summary>
public sealed class PlaybackPower : IDisposable
{
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private ExecutionState _executionState = ExecutionState.Continuous;

    public void SetPlaying(bool playing, bool keepDisplayOn)
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("PlaybackPower must be used on the thread that created it.");
        var state = ExecutionState.Continuous;
        if (playing)
        {
            state |= ExecutionState.SystemRequired;
            if (keepDisplayOn) state |= ExecutionState.DisplayRequired;
        }
        if (_executionState == state) return;
        if (SetThreadExecutionState(state) != 0) _executionState = state;
    }

    public void SetVideoPlaying(bool playing) => SetPlaying(playing, keepDisplayOn: true);

    public void Dispose() => SetPlaying(false, keepDisplayOn: false);

    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
        Continuous = 0x80000000
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState flags);
}
