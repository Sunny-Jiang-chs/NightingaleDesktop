using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using Nightingale.Core;
using Nightingale.Desktop.Platform;
using Nightingale.Desktop.Playback;

namespace Nightingale.Desktop;

public partial class MainWindow : Window
{
    private readonly StateStore _store = new();
    private readonly ObservableCollection<MediaRow> _queue = [];
    private readonly PlaybackPower _power = new();
    private readonly DispatcherTimer _pulse = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _click = new() { Interval = TimeSpan.FromMilliseconds(InputTiming.DoubleClickMilliseconds + 30) };
    private readonly DispatcherTimer _rightHold = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private MpvPlayer? _player;
    private FullscreenControlsWindow? _fullscreenControls;
    private string? _pendingPath, _currentPath;
    private IReadOnlyList<SubtitleCue> _cues = [];
    private SubtitleTimeline _timeline = new(Array.Empty<SubtitleCue>());
    private readonly Dictionary<long, string> _externalSubtitlePaths = [];
    private bool _loaded, _audioOnly, _fullscreen, _closing, _showHistory, _menuOpen;
    private bool _temporarySpeed;
    private bool _rightPressed, _rightHeld, _knownIncognito, _currentPlaybackPrivate;
    private bool _rememberQueue;
    private long _rightPressedAt;
    private readonly HashSet<string> _privatePaths = new(StringComparer.OrdinalIgnoreCase);
    private string? _reportedSaveError;
    private readonly SemaphoreSlim _openLock = new(1, 1);
    private int _openGeneration;
    private int _playGeneration;
    private DateTimeOffset? _sleepDeadline;
    private DateTimeOffset _lastSave, _lastPointer, _noticeUntil;
    private DateTimeOffset _suppressClickUntil;
    private string _notice = "";
    private string _playbackStatus = "就绪";
    private Rect _windowBounds;
    private WindowState _previousState;
    private bool _sidebarBeforeFullscreen;
    private double _resumePosition;
    private double _lastPosition, _lastDuration;
    private string? _selectedSubtitlePath;
    private readonly string[] _initialFiles;

    public MainWindow(string[] files)
    {
        _initialFiles = files;
        InitializeComponent();
        _knownIncognito = _store.Settings.Incognito;
        _rememberQueue = !_knownIncognito;
        App.SuppressErrorLogging |= _knownIncognito;
        if (!_knownIncognito && files.Length == 0)
            foreach (var path in _store.Playlist) _queue.Add(new MediaRow(path));
        UpdatePrivacyIndicator();
        MediaList.ItemsSource = _queue;
        Topmost = _store.Settings.KeepOnTop;
        WireControls(Controls);
        VideoHost.HandleReady += InitializePlayer;
        VideoHost.PointerInput += VideoPointer;
        VideoHost.ShortcutInput += shortcut => HandleShortcut(shortcut);
        VideoHost.TemporarySpeedRequested += pressed => { if (pressed) BeginRightGesture(); else EndRightGesture(); };
        VideoHost.TemporarySpeedCanceled += CancelRightGesture;
        VideoHost.FilesDropped += paths => _ = OpenInputs(paths, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        _click.Tick += (_, _) => { _click.Stop(); TogglePlayback(); };
        _rightHold.Tick += (_, _) =>
        {
            _rightHold.Stop();
            if (!_rightPressed) return;
            _rightHeld = true;
            SetTemporarySpeed(true);
        };
        _pulse.Tick += (_, _) => RefreshPlayback();
        Loaded += async (_, _) =>
        {
            _pulse.Start();
            if (_initialFiles.Length > 0) { SetSidebar(false); await OpenInputs(_initialFiles); }
            else ShowHistory();
            if (_store.LastLoadError != null) ShowNotice("播放记录读取异常，已尝试从备份恢复。", 8);
        };
    }

    public void ReceiveFiles(string[] files)
    {
        if (_closing) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (files.Length > 0) _ = OpenInputs(files);
    }

    private void InitializePlayer(nint handle)
    {
        if (_player != null || _closing) return;
        try
        {
            _player = new MpvPlayer(handle);
            VideoHost.AttachPlayer(_player);
            _player.FileLoaded += (_, _) => OnFileLoaded();
            _player.PlaybackEnded += (_, _) => OnPlaybackEnded();
            _player.ErrorOccurred += (_, message) => ShowPlaybackError(message);
            _player.NonFatalErrorOccurred += (_, message) => ShowNotice(message);
            ApplySettings();
            if (_pendingPath != null) LoadNow(_pendingPath);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            VideoHost.Visibility = Visibility.Collapsed;
            WelcomePanel.Visibility = Visibility.Visible;
            _playbackStatus = "播放组件未能启动";
            ShowNotice("播放组件未能启动：" + ex.Message, 12);
        }
    }

    private async Task OpenInputs(IEnumerable<string> paths, bool append = false)
    {
        var inputs = paths.ToArray();
        if (inputs.Length == 1 && MediaFiles.IsSubtitle(inputs[0])) { LoadSubtitle(inputs[0]); return; }
        var generation = append ? _openGeneration : ++_openGeneration;
        await _openLock.WaitAsync();
        if (_closing || generation != _openGeneration) { _openLock.Release(); return; }
        _playbackStatus = "正在读取文件…";
        try
        {
            var files = await Task.Run(() => MediaFiles.Enumerate(inputs));
            if (_closing || generation != _openGeneration) return;
            if (files.Count == 0) { ShowNotice("没有找到可播放的本地音视频文件。"); _playbackStatus = _loaded ? "" : "就绪"; return; }
            if (!append) _queue.Clear();
            if (!_store.Settings.Incognito) _rememberQueue = true;
            var added = 0;
            foreach (var path in files)
            {
                if (_store.Settings.Incognito) _privatePaths.Add(path);
                if (!_queue.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))) { _queue.Add(new MediaRow(path)); added++; }
            }
            ShowQueue();
            QueueCount.Text = $"{_queue.Count} 个文件";
            if (!append || _currentPath == null) PlayFile(files[0]);
            else { _playbackStatus = ""; ShowNotice(added > 0 ? $"已添加 {added} 个文件" : "这些文件已在播放列表中"); }
            SavePlayback();
        }
        catch (Exception ex) { App.LogError(ex); ShowNotice("无法读取文件：" + ex.Message); _playbackStatus = "读取失败"; }
        finally { _openLock.Release(); }
    }

    private void PlayFile(string path)
    {
        if (!File.Exists(path)) { ShowNotice("文件已移动或无法访问：" + Path.GetFileName(path)); return; }
        SavePlayback();
        ResetPlaybackGestures();
        ++_playGeneration;
        _loaded = false;
        _currentPath = path;
        _currentPlaybackPrivate = _store.Settings.Incognito;
        if (_currentPlaybackPrivate) _privatePaths.Add(path);
        else _privatePaths.Remove(path);
        _pendingPath = path;
        _lastPosition = _lastDuration = 0;
        _cues = [];
        _timeline = new(Array.Empty<SubtitleCue>());
        _externalSubtitlePaths.Clear();
        _selectedSubtitlePath = null;
        var previous = _store.Find(path);
        _resumePosition = previous != null && previous.Position > 3 && (previous.Duration <= 0 || previous.Position < previous.Duration - 5) ? previous.Position : 0;
        FileTitle.Text = Path.GetFileName(path);
        Title = $"{Path.GetFileName(path)} — 夜莺播放器";
        _playbackStatus = "正在打开…";
        WelcomePanel.Visibility = Visibility.Collapsed;
        AudioPanel.Visibility = Visibility.Collapsed;
        VideoHost.Visibility = Visibility.Visible;
        if (!_showHistory) MediaList.SelectedItem = _queue.FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        if (_player != null) LoadNow(path);
    }

    private void LoadNow(string path)
    {
        _pendingPath = null;
        try { _player!.Load(path, _resumePosition); }
        catch (Exception ex) { ShowPlaybackError(ex.Message); }
    }

    private async void OnFileLoaded()
    {
        if (_closing || _player == null || _currentPath == null) return;
        if (!string.Equals(_player.CurrentPath, _currentPath, StringComparison.OrdinalIgnoreCase)) return;
        _loaded = true;
        _playbackStatus = "";
        ApplySettings();
        SavePlayback();
        var tracks = _player.GetTracks();
        _audioOnly = !tracks.Any(track => track.Type == "video" && !track.AlbumArt);
        VideoHost.Visibility = _audioOnly ? Visibility.Collapsed : Visibility.Visible;
        AudioPanel.Visibility = _audioOnly ? Visibility.Visible : Visibility.Collapsed;
        AudioTitle.Text = Path.GetFileNameWithoutExtension(_currentPath);
        AudioSubtitle.Text = "";
        AudioSubtitle.Visibility = Visibility.Collapsed;
        var sidecars = MediaFiles.FindSubtitles(_currentPath);
        var generation = _playGeneration;
        for (var index = 0; index < sidecars.Count; index++)
        {
            await LoadSubtitleAsync(sidecars[index], select: index == 0, notify: false);
            if (_closing || generation != _playGeneration) return;
        }
        if (_resumePosition > 0) ShowNotice($"已从 {PlayerControls.FormatTime(_resumePosition)} 继续播放 · Home 从头播放", 5);
        RefreshPlayback();
    }

    private void OnPlaybackEnded()
    {
        if (_closing || !_loaded || _currentPath == null) return;
        if (!_currentPlaybackPrivate) _store.Record(_currentPath, 0, _lastDuration);
        PersistState();
        ResetPlaybackGestures();
        ++_playGeneration;
        _loaded = false;
        _power.SetVideoPlaying(false);
        var index = CurrentIndex();
        if (index >= 0 && index + 1 < _queue.Count) PlayFile(_queue[index + 1].Path);
        else if (_store.Settings.Repeat && _queue.Count > 0) PlayFile(_queue[0].Path);
        else { _playbackStatus = "播放完毕"; _lastPosition = _lastDuration; }
    }

    private void ShowPlaybackError(string message)
    {
        if (_closing) return;
        ResetPlaybackGestures();
        _loaded = false;
        _playbackStatus = "播放失败";
        _power.SetVideoPlaying(false);
        VideoHost.Visibility = Visibility.Collapsed;
        AudioPanel.Visibility = Visibility.Collapsed;
        WelcomePanel.Visibility = Visibility.Visible;
        ShowNotice("无法播放此文件：" + message, 15);
    }

    private void ApplySettings()
    {
        if (_player == null) return;
        var s = _store.Settings;
        _player.SetVolume(s.Volume); _player.SetMute(s.Muted); _player.SetSpeed(EffectiveSpeed); _player.SetGain(s.GainDb);
        _player.SetSubtitleFontSize(s.SubtitleFontSize); _player.SetSubtitleDelay(s.SubtitleDelay); _player.SetSubtitleMargin(s.SubtitleBottomMargin);
    }

    private void WireControls(PlayerControls controls)
    {
        controls.PlayRequested += TogglePlayback;
        controls.PreviousRequested += () => MoveQueue(-1);
        controls.NextRequested += () => MoveQueue(1);
        controls.MuteRequested += ToggleMute;
        controls.QueueRequested += ToggleSidebar;
        controls.FullscreenRequested += ToggleFullscreen;
        controls.SeekRequested += async seconds =>
        {
            var requestId = controls.SeekRequestId;
            var generation = _playGeneration;
            var target = await SeekToAsync(seconds);
            if (_closing || generation != _playGeneration) return;
            if (target is double position) controls.AcknowledgeSeek(requestId, position);
            else controls.CancelSeek(requestId);
        };
        controls.VolumeRequested += volume => { _store.Settings.Volume = volume; _store.Settings.Muted = false; _player?.SetMute(false); _player?.SetVolume(volume); };
        controls.SpeedRequested += ShowSpeedMenu;
        controls.SubtitleRequested += ShowSubtitleMenu;
        controls.TimerRequested += ShowTimerMenu;
        controls.SettingsRequested += ShowSettingsMenu;
    }

    private void TogglePlayback()
    {
        if (_player == null || _currentPath == null) { OpenFileDialog(false); return; }
        if (!_loaded) { PlayFile(_currentPath); return; }
        CancelRightGesture();
        _player.TogglePause();
        _lastPointer = DateTimeOffset.Now;
        RefreshPlayback();
    }
    private int CurrentIndex() => _queue.ToList().FindIndex(item => string.Equals(item.Path, _currentPath, StringComparison.OrdinalIgnoreCase));
    private void MoveQueue(int delta)
    {
        if (_queue.Count == 0) return;
        var target = CurrentIndex() + delta;
        if (_store.Settings.Repeat) target = (target + _queue.Count) % _queue.Count;
        if (target >= 0 && target < _queue.Count) PlayFile(_queue[target].Path);
        else ShowNotice(delta < 0 ? "已经是第一个文件" : "已经是最后一个文件");
    }
    private void SeekTo(double seconds) => _ = SeekToAsync(seconds);
    private async Task<double?> SeekToAsync(double seconds, double originTolerance = 0)
    {
        if (!_loaded || _player == null || !double.IsFinite(seconds)) return null;
        var generation = _playGeneration;
        var position = _player.Position;
        if (_store.Settings.SnapToSubtitles && _timeline.IsAvailable)
        {
            var delay = _store.Settings.SubtitleDelay;
            var direction = Math.Sign(seconds - position);
            seconds = (_timeline.Snap(seconds - delay, position - delay + direction * originTolerance, direction) ?? (seconds - delay)) + delay;
        }
        var target = Math.Clamp(seconds, 0, Math.Max(0, _player.Duration));
        try
        {
            await _player.CommandAsync("seek", target.ToString("R", CultureInfo.InvariantCulture), "absolute+exact");
            if (_closing || generation != _playGeneration) return null;
            _lastPointer = DateTimeOffset.Now;
            return target;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            if (!_closing && generation == _playGeneration) ShowNotice("无法定位到此进度：" + ex.Message);
            return null;
        }
    }
    private void SeekSentence(bool next)
    {
        if (!_loaded || _player == null) return;
        var delay = _store.Settings.SubtitleDelay;
        var target = next ? _timeline.FindNext(_player.Position - delay + .05) : _timeline.FindPrevious(_player.Position - delay - .5);
        if (target is double position) _player.SeekAbsolute(Math.Max(0, position + delay));
        else ShowNotice(_timeline.IsAvailable ? "没有更多字幕句子" : "当前字幕不提供按句跳转，请加载 VTT、SRT 或 LRC");
    }
    private void SeekArrow(int direction)
    {
        if (!_loaded || _player == null) return;
        Controls.ResetSeek();
        _fullscreenControls?.Controls.ResetSeek();
        if (_store.Settings.SnapToSubtitles && _timeline.IsAvailable) SeekSentence(direction > 0);
        else _player.SeekRelative(direction * 5);
    }
    private void ToggleMute() { _store.Settings.Muted = !_store.Settings.Muted; _player?.SetMute(_store.Settings.Muted); RefreshPlayback(); }

    private void RefreshPlayback()
    {
        if (_closing) return;
        var now = DateTimeOffset.Now;
        if (_sleepDeadline.HasValue && now >= _sleepDeadline)
        {
            _sleepDeadline = null;
            CancelRightGesture();
            _player?.Pause();
            ShowNotice("睡眠定时已到，播放已暂停。", 10);
            SavePlayback();
        }
        var paused = !_loaded || _player == null || _player.Paused;
        if (_loaded && _player != null)
        {
            _lastPosition = _player.Position;
            _lastDuration = _player.Duration;
            if (_audioOnly)
            {
                var subtitle = _player.SubtitleText;
                AudioSubtitle.Text = string.IsNullOrWhiteSpace(subtitle) ? "" : subtitle;
                AudioSubtitle.Visibility = string.IsNullOrWhiteSpace(subtitle) ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        _power.SetPlaying(_loaded && !paused, !_audioOnly);
        var timer = _sleepDeadline.HasValue ? "定时 " + PlayerControls.FormatTime((_sleepDeadline.Value - now).TotalSeconds) : "";
        var status = now < _noticeUntil ? _notice : _playbackStatus;
        var s = _store.Settings;
        if (_temporarySpeed) status = "按住 → · 3× 播放";
        Controls.Update(_lastPosition, _lastDuration, paused, s.Volume, s.Muted, EffectiveSpeed, status, timer, _fullscreen);
        _fullscreenControls?.Controls.Update(_lastPosition, _lastDuration, paused, s.Volume, s.Muted, EffectiveSpeed, status, timer, _fullscreen);
        Notice.Visibility = now < _noticeUntil && VideoHost.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        if (_fullscreen && _fullscreenControls?.IsVisible == true && !_menuOpen && !paused && now - _lastPointer > TimeSpan.FromSeconds(3) && !_fullscreenControls.IsMouseOver)
        {
            _fullscreenControls.Hide();
            _player?.SetSubtitleMargin(_store.Settings.SubtitleBottomMargin);
        }
        if (now - _lastSave > TimeSpan.FromSeconds(5)) { _lastSave = now; SavePlayback(); }
    }

    private void SavePlayback()
    {
        if (_loaded && !_currentPlaybackPrivate && _currentPath != null && _player != null) _store.Record(_currentPath, _player.Position, _player.Duration);
        if (_rememberQueue) _store.RememberPlaylist(_queue.Where(item => !_privatePaths.Contains(item.Path)).Select(item => item.Path));
        PersistState();
        if (_showHistory) ShowHistory();
    }
    private void PersistState()
    {
        if (_store.Save()) { _reportedSaveError = null; return; }
        if (_closing || _reportedSaveError == _store.LastSaveError) return;
        _reportedSaveError = _store.LastSaveError;
        _notice = "播放记录未能保存，请检查用户数据目录是否可写。";
        _noticeUntil = DateTimeOffset.Now.AddSeconds(12);
        NoticeText.Text = _notice;
    }

    private void ShowNotice(string text, int seconds = 5)
    {
        _notice = text; _noticeUntil = DateTimeOffset.Now.AddSeconds(seconds); NoticeText.Text = text;
        if (_fullscreen) ShowFullscreenControls();
        RefreshPlayback();
    }

    private void LoadSubtitle(string path, bool select = true, bool notify = true) => _ = LoadSubtitleAsync(path, select, notify);

    private async Task LoadSubtitleAsync(string path, bool select = true, bool notify = true)
    {
        if (_player == null || !_loaded) { if (notify) ShowNotice("请先打开一个音频或视频文件。"); return; }
        var generation = _playGeneration;
        var nativeGeneration = _player.LoadGeneration;
        try
        {
            var actual = path;
            if (Path.GetExtension(path).Equals(".lrc", StringComparison.OrdinalIgnoreCase))
            {
                var cues = SubtitleParser.Parse(path);
                actual = "memory://" + SubtitleParser.ToWebVtt(cues);
            }
            var previous = _player.GetTracks().FirstOrDefault(track => track.Type == "sub" && track.Selected)?.Id;
            var added = await _player.AddSubtitleAsync(actual, nativeGeneration);
            if (_closing || generation != _playGeneration || added == null) return;
            _externalSubtitlePaths[added.Value] = path;
            if (!select) await _player.CommandAsync("set", "sid", previous?.ToString(CultureInfo.InvariantCulture) ?? "no");
            if (_closing || generation != _playGeneration) return;
            if (select) SetSubtitleTimeline(path);
            if (notify) ShowNotice("已加载字幕：" + Path.GetFileName(path));
        }
        catch (Exception ex) { App.LogError(ex); if (notify && !_closing && generation == _playGeneration) ShowNotice("字幕加载失败：" + ex.Message); }
    }

    private void SetSubtitleTimeline(string? path)
    {
        _selectedSubtitlePath = path;
        _cues = path == null ? [] : SubtitleParser.Parse(path);
        _timeline = new(_cues, _player?.Duration ?? double.PositiveInfinity);
    }

    private ContextMenu Menu(FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Top };
        menu.Opened += (_, _) => { CancelRightGesture(); _menuOpen = true; };
        menu.Closed += (_, _) => { _menuOpen = false; _lastPointer = DateTimeOffset.Now; };
        return menu;
    }
    private static MenuItem Item(string label, Action action, bool? check = null)
    {
        var item = new MenuItem { Header = label };
        if (check.HasValue) { item.IsCheckable = true; item.IsChecked = check.Value; }
        item.Click += (_, _) => action();
        return item;
    }
    private void ShowSpeedMenu(FrameworkElement anchor)
    {
        var menu = Menu(anchor);
        foreach (var speed in new[] { .5, .75, 1, 1.25, 1.5, 1.75, 2, 3 })
            menu.Items.Add(Item($"{speed:0.0#}×", () => { _store.Settings.Speed = speed; _player?.SetSpeed(EffectiveSpeed); _store.Save(); }, Math.Abs(_store.Settings.Speed - speed) < .01));
        menu.IsOpen = true;
    }
    private void ShowSubtitleMenu(FrameworkElement anchor)
    {
        var menu = Menu(anchor);
        menu.Items.Add(Item("加载外挂字幕…", PickSubtitle));
        menu.Items.Add(Item("关闭字幕", () => { _player?.SelectSubtitle(-1); SetSubtitleTimeline(null); }));
        if (_player != null && _loaded)
        {
            var tracks = _player.GetTracks();
            foreach (var track in tracks.Where(track => track.Type == "sub"))
            {
                var label = _externalSubtitlePaths.TryGetValue(track.Id, out var path) ? Path.GetFileName(path) : TrackName(track);
                menu.Items.Add(Item(label, () => { _player.SelectSubtitle(track.Id); SetSubtitleTimeline(_externalSubtitlePaths.GetValueOrDefault(track.Id)); }, track.Selected));
            }
            var audio = tracks.Where(track => track.Type == "audio").ToList();
            if (audio.Count > 0)
            {
                menu.Items.Add(new Separator());
                foreach (var track in audio) menu.Items.Add(Item("音轨 · " + TrackName(track), () => _player.SelectAudio(track.Id), track.Selected));
            }
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("进度定位吸附到字幕起点", ToggleSubtitleSnap, _store.Settings.SnapToSubtitles));
        if (_store.Settings.SnapToSubtitles && !_timeline.IsAvailable)
            menu.Items.Add(new MenuItem { Header = "加载并选中 SRT、VTT 或 LRC 后生效", IsEnabled = false });
        menu.Items.Add(Item("上一句    [", () => SeekSentence(false)));
        menu.Items.Add(Item("下一句    ]", () => SeekSentence(true)));
        menu.Items.Add(Item("字幕设置…", ShowPreferences));
        menu.IsOpen = true;
    }
    private void ToggleSubtitleSnap()
    {
        _store.Settings.SnapToSubtitles = !_store.Settings.SnapToSubtitles;
        _store.Save();
        ShowNotice(_store.Settings.SnapToSubtitles
            ? (_timeline.IsAvailable ? "已开启：点击或拖动进度时吸附到字幕起点" : "已开启字幕吸附，加载并选中 SRT、VTT 或 LRC 后生效")
            : "已关闭字幕吸附，按选定时间定位");
    }
    private static string TrackName(MpvTrack track) => string.IsNullOrWhiteSpace(track.Title) ? $"{track.Id} · {(string.IsNullOrWhiteSpace(track.Language) ? "未标记语言" : track.Language)}" : $"{track.Id} · {track.Title}";
    private void PickSubtitle()
    {
        var dialog = new OpenFileDialog { AddToRecent = false, Title = "加载字幕或歌词", Filter = "字幕与歌词|*.vtt;*.srt;*.ass;*.ssa;*.lrc|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) LoadSubtitle(dialog.FileName);
    }
    private void ShowTimerMenu(FrameworkElement anchor)
    {
        var menu = Menu(anchor);
        foreach (var minutes in new[] { 15, 30, 60, 90 }) menu.Items.Add(Item($"{minutes} 分钟后暂停", () => SetTimer(minutes)));
        menu.Items.Add(Item("自定义时长…", () =>
        {
            var value = InputDialog.Ask(this, "睡眠定时", "多少分钟后暂停播放？（1–1440）", "45");
            if (value != null && double.TryParse(value, out var minutes) && minutes >= 1 && minutes <= 1440) SetTimer(minutes);
            else if (value != null) ShowNotice("请输入 1 到 1440 之间的分钟数。");
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("取消定时", () => { _sleepDeadline = null; ShowNotice("已取消睡眠定时"); }));
        menu.IsOpen = true;
    }
    private void SetTimer(double minutes) { _sleepDeadline = DateTimeOffset.Now.AddMinutes(minutes); ShowNotice($"将在 {minutes:0.#} 分钟后暂停播放"); }
    private void ShowSettingsMenu(FrameworkElement anchor)
    {
        var menu = Menu(anchor);
        menu.Items.Add(Item("窗口置顶", () => { Topmost = _store.Settings.KeepOnTop = !_store.Settings.KeepOnTop; _store.Save(); }, _store.Settings.KeepOnTop));
        menu.Items.Add(Item("循环播放列表", () => { _store.Settings.Repeat = !_store.Settings.Repeat; _store.Save(); }, _store.Settings.Repeat));
        menu.Items.Add(Item("无痕播放", () => SetIncognito(!_store.Settings.Incognito), _store.Settings.Incognito));
        var gain = new MenuItem { Header = "音频增益" };
        foreach (var db in new[] { 0d, 6d, 12d }) gain.Items.Add(Item(db == 0 ? "原声" : $"+{db} dB", () => { _store.Settings.GainDb = db; _player?.SetGain(db); _store.Save(); }, _store.Settings.GainDb == db));
        menu.Items.Add(gain);
        var picture = new MenuItem { Header = "画面比例" };
        picture.Items.Add(Item("适应窗口", () => SetPicture("fit")));
        picture.Items.Add(Item("填满并裁剪", () => SetPicture("crop")));
        picture.Items.Add(Item("拉伸至窗口", () => SetPicture("stretch")));
        menu.Items.Add(picture);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("打开文件所在位置", () => { if (_currentPath != null) ShellIntegration.RevealFile(_currentPath); }));
        menu.Items.Add(Item("设为默认播放器…", OpenDefaultSettings));
        menu.Items.Add(Item("更多设置与快捷键…", ShowPreferences));
        menu.IsOpen = true;
    }
    private void SetPicture(string mode)
    {
        if (_player == null) return;
        _player.SetProperty("keepaspect", mode == "stretch" ? "no" : "yes");
        _player.SetProperty("panscan", mode == "crop" ? "1" : "0");
    }
    private void ShowPreferences()
    {
        var dialog = new PreferencesWindow(_store.Settings) { Owner = this };
        dialog.SettingsChanged += OnSettingsChanged;
        dialog.ShowDialog();
    }
    private void OnSettingsChanged()
    {
        if (_knownIncognito != _store.Settings.Incognito) SetIncognito(_store.Settings.Incognito);
        Topmost = _store.Settings.KeepOnTop;
        ApplySettings();
        PersistState();
    }
    private void SetIncognito(bool enabled)
    {
        _store.Settings.Incognito = enabled;
        _knownIncognito = enabled;
        if (enabled)
        {
            _rememberQueue = false;
            App.SuppressErrorLogging = true;
            if (_currentPath != null) { _currentPlaybackPrivate = true; _privatePaths.Add(_currentPath); }
        }
        UpdatePrivacyIndicator();
        PersistState();
        if (_showHistory) ShowHistory();
        ShowNotice(enabled ? "无痕播放已开启：不保存本次播放记录和进度" : "无痕播放已关闭：之后打开的文件会保存记录");
    }
    private void UpdatePrivacyIndicator() => PrivacyBadge.Visibility = _store.Settings.Incognito ? Visibility.Visible : Visibility.Collapsed;
    private void OpenDefaultSettings()
    {
        try { DefaultApps.OpenSettings(); }
        catch (Exception ex) { ShowNotice("无法打开系统设置：" + ex.Message); }
    }

    private void ShowQueue()
    {
        _showHistory = false; MediaList.ItemsSource = _queue; ListHeading.Text = "播放列表";
        QueueTab.Background = Brush("#253E3B"); QueueTab.Foreground = Brush("#80DCC8");
        HistoryTab.Background = Brushes.Transparent; HistoryTab.Foreground = Brush("#E7EEF3");
        MediaList.SelectedItem = _queue.FirstOrDefault(item => item.Path == _currentPath);
        QueueCount.Text = $"{_queue.Count} 个文件";
    }
    private void ShowHistory()
    {
        _showHistory = true;
        var entries = _store.Settings.Incognito ? [] : _store.History;
        MediaList.ItemsSource = entries.Select(entry => new MediaRow(entry.Path, entry.Position > 0 ? "继续 " + PlayerControls.FormatTime(entry.Position) : "最近播放")).ToList();
        ListHeading.Text = "最近播放"; QueueCount.Text = _store.Settings.Incognito ? "无痕播放 · 历史记录已隐藏" : $"{entries.Count} 个记录";
        HistoryTab.Background = Brush("#253E3B"); HistoryTab.Foreground = Brush("#80DCC8");
        QueueTab.Background = Brushes.Transparent; QueueTab.Foreground = Brush("#E7EEF3");
    }
    private static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
    private void SetSidebar(bool visible) { Sidebar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; SidebarColumn.Width = visible ? new GridLength(244) : new GridLength(0); }
    private void ToggleSidebar() { if (_fullscreen) ToggleFullscreen(); SetSidebar(Sidebar.Visibility != Visibility.Visible); }

    private void OpenFileDialog(bool append)
    {
        var dialog = new OpenFileDialog { AddToRecent = false, Title = append ? "添加到播放列表" : "打开音视频文件", Multiselect = true, Filter = "音视频文件|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v;*.ts;*.mp3;*.wav;*.flac;*.m4a;*.aac;*.ogg;*.opus|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) _ = OpenInputs(dialog.FileNames, append);
    }
    private void OpenFolderDialog()
    {
        var dialog = new OpenFolderDialog { AddToRecent = false, Title = "选择媒体文件夹", Multiselect = false };
        if (dialog.ShowDialog(this) == true) { SetSidebar(true); _ = OpenInputs([dialog.FolderName]); }
    }
    private void FilesDragged(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void FilesDropped(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = OpenInputs(paths, Keyboard.Modifiers.HasFlag(ModifierKeys.Control)); e.Handled = true; }
    private void PlaylistFilesDropped(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = OpenInputs(paths, append: true); e.Handled = true; }
    private void MediaDoubleClicked(object sender, MouseButtonEventArgs e) { if (MediaList.SelectedItem is MediaRow row) { if (_showHistory) _ = OpenInputs([row.Path]); else PlayFile(row.Path); } }
    private void ListKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && MediaList.SelectedItem is MediaRow row) { if (_showHistory) _ = OpenInputs([row.Path]); else PlayFile(row.Path); e.Handled = true; } }
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || _menuOpen) return;
        if (_rightPressed && Keyboard.Modifiers != ModifierKeys.None) CancelRightGesture();
        if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.None)
        {
            BeginRightGesture();
            e.Handled = true;
            return;
        }
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var key = e.Key switch { Key.OemOpenBrackets => "OemOpenBrackets", Key.OemCloseBrackets => "OemCloseBrackets", _ => e.Key.ToString() };
        var shortcut = (control ? "Ctrl+" : "") + (shift ? "Shift+" : "") + key;
        e.Handled = HandleShortcut(shortcut);
    }
    private void WindowKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Right) { EndRightGesture(Keyboard.Modifiers == ModifierKeys.None); e.Handled = true; }
    }
    private void WindowDeactivated(object? sender, EventArgs e) => CancelRightGesture();
    private void BeginRightGesture()
    {
        if (_rightPressed || _closing || !_loaded || _player == null || _menuOpen) return;
        _rightPressed = true;
        _rightHeld = false;
        _rightPressedAt = Environment.TickCount64;
        _rightHold.Start();
    }
    private void EndRightGesture(bool commitTap = true)
    {
        var tap = _rightPressed && !_rightHeld && Environment.TickCount64 - _rightPressedAt < 300;
        CancelRightGesture();
        if (tap && commitTap) SeekArrow(1);
    }
    private void CancelRightGesture()
    {
        _rightHold.Stop();
        _rightPressed = false;
        _rightHeld = false;
        SetTemporarySpeed(false);
    }
    private double EffectiveSpeed => _temporarySpeed ? 3 : _store.Settings.Speed;
    private void SetTemporarySpeed(bool active)
    {
        if (active && (_closing || !_loaded || _player == null || _menuOpen)) return;
        if (_temporarySpeed == active) return;
        _temporarySpeed = active;
        _player?.SetSpeed(EffectiveSpeed);
        _lastPointer = DateTimeOffset.Now;
        if (_fullscreen && active) ShowFullscreenControls();
    }
    private void ResetPlaybackGestures()
    {
        CancelRightGesture();
        _click.Stop();
        Controls.ResetSeek();
        _fullscreenControls?.Controls.ResetSeek();
    }

    private bool HandleShortcut(string shortcut)
    {
        switch (shortcut)
        {
            case "Ctrl+O": OpenFileDialog(false); break;
            case "Ctrl+Shift+O": OpenFolderDialog(); break;
            case "Space": TogglePlayback(); break;
            case "F": case "F11": ToggleFullscreen(); break;
            case "Escape": if (_fullscreen) ToggleFullscreen(); else return false; break;
            case "Ctrl+Left": MoveQueue(-1); break;
            case "Ctrl+Right": MoveQueue(1); break;
            case "Left": CancelRightGesture(); SeekArrow(-1); break;
            case "Right": BeginRightGesture(); break;
            case "Shift+Left": CancelRightGesture(); if (_loaded && _player != null) _ = SeekToAsync(_player.Position - 30, .1); break;
            case "Shift+Right": CancelRightGesture(); if (_loaded && _player != null) _ = SeekToAsync(_player.Position + 30, .1); break;
            case "Up": ChangeVolume(5); break;
            case "Down": ChangeVolume(-5); break;
            case "M": ToggleMute(); break;
            case "L": ToggleSidebar(); break;
            case "Home": if (_loaded) _player?.SeekAbsolute(0); break;
            case "OemOpenBrackets": SeekSentence(false); break;
            case "OemCloseBrackets": SeekSentence(true); break;
            default: return false;
        }
        _lastPointer = DateTimeOffset.Now;
        if (_fullscreen) ShowFullscreenControls();
        return true;
    }
    private void ChangeVolume(double delta)
    {
        _store.Settings.Volume = Math.Clamp(_store.Settings.Volume + delta, 0, 100); _store.Settings.Muted = false;
        _player?.SetVolume(_store.Settings.Volume); _player?.SetMute(false);
        ShowNotice($"音量 {_store.Settings.Volume:0}%", 2);
    }
    private void VideoPointer(NativeVideoPointerEvent pointer)
    {
        switch (pointer.Kind)
        {
            case NativeVideoPointerKind.Move: _lastPointer = DateTimeOffset.Now; if (_fullscreen) ShowFullscreenControls(); break;
            case NativeVideoPointerKind.LeftUp: if (DateTimeOffset.Now < _suppressClickUntil) break; _click.Stop(); _click.Start(); break;
            case NativeVideoPointerKind.DoubleClick: _click.Stop(); _suppressClickUntil = DateTimeOffset.Now.AddMilliseconds(InputTiming.DoubleClickMilliseconds); ToggleFullscreen(); break;
            case NativeVideoPointerKind.Wheel: ChangeVolume(pointer.Delta > 0 ? 5 : -5); break;
            case NativeVideoPointerKind.RightUp: ShowSettingsMenu(_fullscreenControls?.IsVisible == true ? _fullscreenControls.Controls : Controls); break;
        }
    }
    private void StageMouseMoved(object sender, MouseEventArgs e) { _lastPointer = DateTimeOffset.Now; if (_fullscreen) ShowFullscreenControls(); }
    private void AudioMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_loaded) return;
        if (e.ClickCount == 2) VideoPointer(new NativeVideoPointerEvent(NativeVideoPointerKind.DoubleClick, 0, 0));
        e.Handled = true;
    }
    private void AudioMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_loaded) return;
        VideoPointer(new NativeVideoPointerEvent(NativeVideoPointerKind.LeftUp, 0, 0));
        e.Handled = true;
    }

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _previousState = WindowState;
            _windowBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _sidebarBeforeFullscreen = Sidebar.Visibility == Visibility.Visible;
            _fullscreen = true;
            TitleBar.Visibility = Visibility.Collapsed; TitleRow.Height = new GridLength(0);
            Controls.Visibility = Visibility.Collapsed;
            SetSidebar(false);
            WindowChrome.SetWindowChrome(this, null);
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            DesktopGeometry.FillMonitor(this);
            _fullscreenControls = new FullscreenControlsWindow { Owner = this };
            WireControls(_fullscreenControls.Controls);
            _fullscreenControls.PreviewKeyDown += WindowKeyDown;
            _fullscreenControls.PreviewKeyUp += WindowKeyUp;
            _fullscreenControls.Deactivated += WindowDeactivated;
            _fullscreenControls.MouseMove += (_, _) => _lastPointer = DateTimeOffset.Now;
            ShowFullscreenControls();
        }
        else
        {
            _fullscreen = false;
            _fullscreenControls?.Close(); _fullscreenControls = null;
            ResizeMode = ResizeMode.CanResize;
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 48, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0) });
            WindowState = WindowState.Normal;
            Left = _windowBounds.Left; Top = _windowBounds.Top; Width = _windowBounds.Width; Height = _windowBounds.Height;
            WindowState = _previousState;
            TitleBar.Visibility = Visibility.Visible; TitleRow.Height = new GridLength(48);
            Controls.Visibility = Visibility.Visible; SetSidebar(_sidebarBeforeFullscreen);
            _player?.SetSubtitleMargin(_store.Settings.SubtitleBottomMargin);
            Activate();
        }
        _lastPointer = DateTimeOffset.Now;
        RefreshPlayback();
    }
    private void ShowFullscreenControls()
    {
        if (_fullscreenControls == null) return;
        if (!_fullscreenControls.IsVisible)
        {
            _fullscreenControls.Show();
            _player?.SetSubtitleMargin(Math.Max(120, _store.Settings.SubtitleBottomMargin));
        }
        DesktopGeometry.PositionControls(this, _fullscreenControls);
    }
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        ResetPlaybackGestures();
        _closing = true; _pulse.Stop(); _click.Stop(); SavePlayback();
        _fullscreenControls?.Close(); _power.Dispose(); _player?.Dispose();
    }
    private void WindowStateChanged(object? sender, EventArgs e) { if (MaximizeButton != null) MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922"; if (WindowState == WindowState.Minimized) _fullscreenControls?.Hide(); }
    private void WindowMoved(object? sender, EventArgs e) { if (_fullscreenControls?.IsVisible == true) DesktopGeometry.PositionControls(this, _fullscreenControls); }
    private void WindowResized(object sender, SizeChangedEventArgs e) { if (_fullscreenControls?.IsVisible == true) DesktopGeometry.PositionControls(this, _fullscreenControls); }
    private void OpenClicked(object sender, RoutedEventArgs e) => OpenFileDialog(false);
    private void AddClicked(object sender, RoutedEventArgs e) => OpenFileDialog(true);
    private void FolderClicked(object sender, RoutedEventArgs e) => OpenFolderDialog();
    private void QueueTabClicked(object sender, RoutedEventArgs e) => ShowQueue();
    private void HistoryTabClicked(object sender, RoutedEventArgs e) => ShowHistory();
    private void MinimizeClicked(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClicked(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
}

public sealed class MediaRow
{
    public MediaRow(string path, string? detail = null) { Path = path; Name = System.IO.Path.GetFileNameWithoutExtension(path); Detail = detail ?? System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant(); }
    public string Path { get; }
    public string Name { get; }
    public string Detail { get; }
    public string Glyph => new[] { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus" }.Contains(System.IO.Path.GetExtension(Path).ToLowerInvariant()) ? "\uE8D6" : "\uE714";
}
