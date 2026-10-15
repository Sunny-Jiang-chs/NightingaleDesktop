using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Nightingale.Desktop;

public partial class PlayerControls : UserControl
{
    private bool _updating;
    private bool _seeking;
    private double _dragValueOffset;
    private double? _pendingSeek;
    private bool _seekAcknowledged;
    private DateTimeOffset _seekDeadline;
    public long SeekRequestId { get; private set; }
    public event Action? PlayRequested, PreviousRequested, NextRequested, MuteRequested, QueueRequested, FullscreenRequested;
    public event Action<double>? SeekRequested, VolumeRequested;
    public event Action<FrameworkElement>? SpeedRequested, SubtitleRequested, TimerRequested, SettingsRequested;
    public PlayerControls() { InitializeComponent(); Unloaded += (_, _) => ResetSeek(); }
    public void Update(double position, double duration, bool paused, double volume, bool muted, double speed, string status, string timer, bool fullscreen)
    {
        _updating = true;
        // mpv accepts seeks asynchronously. Keep the requested position visible until
        // the command is acknowledged AND its new position reaches the next poll.
        if (_pendingSeek is double target && _seekAcknowledged &&
            (DateTimeOffset.UtcNow >= _seekDeadline || Math.Abs(position - target) <= 1))
            _pendingSeek = null;
        if (!_seeking && _pendingSeek == null) { SeekBar.Maximum = Math.Max(1, duration); SeekBar.Value = Math.Clamp(position, 0, SeekBar.Maximum); PositionLabel.Text = FormatTime(position); }
        SeekBar.IsEnabled = duration > 0;
        DurationLabel.Text = FormatTime(duration);
        PlayButton.Content = paused ? "\uE768" : "\uE769";
        MuteButton.Content = muted || volume == 0 ? "\uE74F" : "\uE767";
        VolumeBar.Value = volume;
        SpeedButton.Content = $"{speed:0.0#}×";
        StatusLabel.Text = status;
        TimerButton.Content = string.IsNullOrEmpty(timer) ? "定时" : timer;
        FullscreenButton.Content = fullscreen ? "\uE73F" : "\uE740";
        _updating = false;
    }
    public static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
    }
    public void AcknowledgeSeek(long requestId, double position)
    {
        if (requestId != SeekRequestId || _pendingSeek == null) return;
        _pendingSeek = Math.Clamp(position, SeekBar.Minimum, SeekBar.Maximum);
        _seekAcknowledged = true;
        _seekDeadline = DateTimeOffset.UtcNow.AddSeconds(3);
        if (!_seeking) SeekBar.Value = _pendingSeek.Value;
    }
    public void CancelSeek(long requestId) { if (requestId == SeekRequestId) _pendingSeek = null; }
    public void ResetSeek()
    {
        _seeking = false;
        _pendingSeek = null;
        SeekRequestId++;
        if (SeekBar.IsMouseCaptured) SeekBar.ReleaseMouseCapture();
    }
    private void SeekStarted(object sender, MouseButtonEventArgs e)
    {
        if (!SeekBar.IsEnabled || e.ChangedButton != MouseButton.Left) return;
        _seeking = true;
        _dragValueOffset = 0;
        // Own the whole gesture, including clicks on the track and releases outside
        // the bar. Slider's IsMoveToPointEnabled does not capture track clicks.
        if (SeekBar.Template.FindName("PART_Track", SeekBar) is Track track)
        {
            for (var source = e.OriginalSource as DependencyObject; source != null && source != SeekBar; source = VisualTreeHelper.GetParent(source))
                if (source is Thumb) { _dragValueOffset = SeekBar.Value - PointerValue(track, e.GetPosition(track)); break; }
        }
        SeekBar.Focus();
        SeekBar.CaptureMouse();
        UpdateSeekFromPointer(e);
        e.Handled = true;
    }
    private void SeekMoved(object sender, MouseEventArgs e)
    {
        if (!_seeking) return;
        UpdateSeekFromPointer(e);
        e.Handled = true;
    }
    private void SeekFinished(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking || e.ChangedButton != MouseButton.Left) return;
        UpdateSeekFromPointer(e);
        CommitSeek();
        e.Handled = true;
    }
    private void UpdateSeekFromPointer(MouseEventArgs e)
    {
        if (SeekBar.Template.FindName("PART_Track", SeekBar) is not Track track) return;
        var value = PointerValue(track, e.GetPosition(track)) + _dragValueOffset;
        if (double.IsFinite(value)) SeekBar.Value = Math.Clamp(value, SeekBar.Minimum, SeekBar.Maximum);
    }
    private double PointerValue(Track track, Point point)
    {
        var thumbWidth = track.Thumb?.ActualWidth ?? 0;
        var width = track.ActualWidth - thumbWidth;
        if (width <= 0) return SeekBar.Value;
        // Track.ValueFromPoint adds Value to its last layout's thumb offset. During
        // capture changes and rapid moves Value may already be newer than layout.
        var fraction = (point.X - thumbWidth / 2) / width;
        if (track.IsDirectionReversed) fraction = 1 - fraction;
        return SeekBar.Minimum + fraction * (SeekBar.Maximum - SeekBar.Minimum);
    }
    private void SeekLostCapture(object sender, MouseEventArgs e) { if (_seeking) CommitSeek(); }
    private void CommitSeek()
    {
        if (!_seeking) return;
        _seeking = false;
        if (SeekBar.IsMouseCaptured) SeekBar.ReleaseMouseCapture();
        RequestSeek();
    }
    private void RequestSeek()
    {
        SeekRequestId++;
        _pendingSeek = SeekBar.Value;
        _seekAcknowledged = false;
        SeekRequested?.Invoke(SeekBar.Value);
    }
    private void SeekValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_updating && PositionLabel != null) PositionLabel.Text = FormatTime(e.NewValue); }
    private void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_updating) VolumeRequested?.Invoke(e.NewValue); }
    private void PlayClicked(object s, RoutedEventArgs e) => PlayRequested?.Invoke();
    private void PreviousClicked(object s, RoutedEventArgs e) => PreviousRequested?.Invoke();
    private void NextClicked(object s, RoutedEventArgs e) => NextRequested?.Invoke();
    private void MuteClicked(object s, RoutedEventArgs e) => MuteRequested?.Invoke();
    private void QueueClicked(object s, RoutedEventArgs e) => QueueRequested?.Invoke();
    private void FullscreenClicked(object s, RoutedEventArgs e) => FullscreenRequested?.Invoke();
    private void SpeedClicked(object s, RoutedEventArgs e) => SpeedRequested?.Invoke((FrameworkElement)s);
    private void SubtitleClicked(object s, RoutedEventArgs e) => SubtitleRequested?.Invoke((FrameworkElement)s);
    private void TimerClicked(object s, RoutedEventArgs e) => TimerRequested?.Invoke((FrameworkElement)s);
    private void SettingsClicked(object s, RoutedEventArgs e) => SettingsRequested?.Invoke((FrameworkElement)s);
}
