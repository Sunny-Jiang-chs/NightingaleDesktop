using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Nightingale.Core;
using Nightingale.Desktop;
using Nightingale.Desktop.Playback;

namespace Nightingale.Integration.Tests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = FindRoot();
        var run = Path.Combine(root, "tmp", "integration-tests", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(run);
        using var log = new StreamWriter(Path.Combine(run, "result.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
        var output = new DualWriter(Console.Out, log);
        Console.SetOut(output);
        Console.SetError(output);
        Console.WriteLine($"Integration test run: {run}");
        try
        {
            var statePath = Path.Combine(run, "state");
            Environment.SetEnvironmentVariable("NIGHTINGALE_DATA_DIR", statePath);
            var state = new StateStore(statePath);
            state.Settings.Muted = true;
            state.Save();
            var fixtures = Fixtures.Create(root, run);
            var app = new IntegrationApplication(fixtures, args.Contains("--skip-fullscreen", StringComparer.OrdinalIgnoreCase));
            // Compiled Application.LoadComponent requires App's exact runtime type.
            // Load the same application resource nodes into our isolated Application
            // instead of executing the production single-instance startup hook.
            var appSource = XDocument.Load(Path.Combine(root, "src", "Nightingale.Desktop", "App.xaml"));
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var resources = new XElement(presentation + "ResourceDictionary",
                new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
                appSource.Root!.Element(presentation + "Application.Resources")!.Elements());
            app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Run();
            Console.WriteLine($"Results retained in {run}");
            return app.ExitCode;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "Nightingale.Desktop"))) return directory.FullName;
        throw new InvalidOperationException("Run with --artifacts-path tmp/integration-build inside the repository.");
    }
}

internal sealed class IntegrationApplication(Fixtures fixtures, bool skipFullscreen) : Application
{
    private MainWindow? _window;
    private ContextMenu? _openedMenu;
    private int _passed;
    private int _failed;
    private readonly List<Exception> _dispatcherErrors = [];
    public int ExitCode { get; private set; } = 1;
    private MainWindow Window => _window ?? throw new InvalidOperationException("No window.");
    private MpvPlayer Player => Field<MpvPlayer>("_player");
    private StateStore Store => Field<StateStore>("_store");

    // Deliberately bypass App.OnStartup: this test process never touches the user's
    // single-instance pipe or existing application windows.
    protected override async void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, error) =>
        {
            _dispatcherErrors.Add(error.Exception);
            Console.Error.WriteLine("DISPATCHER ERROR " + error.Exception);
            error.Handled = true;
        };
        try
        {
            // Keep test popups invisible while still exercising actual WPF opening,
            // checkable-item clicks, closing and application event routing.
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
                new RoutedEventHandler((sender, _) => { _openedMenu = (ContextMenu)sender; _openedMenu.Opacity = 0; }));
            CreateWindow([fixtures.VideoA]);
            await Run("Initial video, automatic SRT and exact subtitle identity", async () =>
            {
                await WaitLoaded(fixtures.VideoA);
                await Until(() => Field<SubtitleTimeline>("_timeline").IsAvailable && Player.GetTracks().Count(track => track.Type == "sub") == 1, "automatic SRT");
                Equal(3, Field<IReadOnlyList<SubtitleCue>>("_cues").Count);
                Check(Player.GetFlag("mute"), "fixture must stay muted");
                Equal(Path.ChangeExtension(fixtures.VideoA, ".srt"), Field<string>("_selectedSubtitlePath"));
                Check(!Field<bool>("_audioOnly"), "video detected as audio");
                await Pause();
                await InvokeTask("LoadSubtitleAsync", Path.ChangeExtension(fixtures.VideoA, ".srt"), true, true);
                Equal(1, Player.GetTracks().Count(track => track.Type == "sub"));
                var selected = Player.GetTracks().Single(track => track.Type == "sub" && track.Selected);
                Equal(Path.ChangeExtension(fixtures.VideoA, ".srt"), Field<Dictionary<long, string>>("_externalSubtitlePaths")[selected.Id]);
            });
            await Run("Sentence navigation and seek snapping honor subtitle delay", async () =>
            {
                Store.Settings.SubtitleDelay = .8;
                Store.Settings.SnapToSubtitles = false;
                Invoke("ApplySettings");
                Player.SeekAbsolute(52);
                await Until(() => Math.Abs(Player.Position - 52) < .3, "seek 52");
                Invoke("SeekSentence", true);
                await Until(() => Math.Abs(Player.Position - 60.8) < .3, "next cue with delay");
                Invoke("SeekSentence", false);
                await Until(() => Math.Abs(Player.Position - .8) < .3, "previous cue with delay");
                Invoke("SeekTo", 88d);
                await Until(() => Math.Abs(Player.Position - 88) < .3, "exact seek with snapping disabled");
                Invoke("ToggleSubtitleSnap");
                Check(Store.Settings.SnapToSubtitles, "subtitle snap toggle did not enable the setting");
                Player.SeekAbsolute(10);
                await Until(() => Math.Abs(Player.Position - 10) < .3, "seek before cue");
                Invoke("SeekTo", 88d);
                await Until(() => Math.Abs(Player.Position - 60.8) < .3, "snapped seek");
                Check(new StateStore(Store.DataDirectory).Settings.SnapToSubtitles, "enabled subtitle snapping was not persisted");
                Invoke("ToggleSubtitleSnap");
                Check(!Store.Settings.SnapToSubtitles, "subtitle snap toggle did not disable the setting");
                Store.Settings.SubtitleDelay = 0;
                Invoke("ApplySettings");
                Check(!new StateStore(Store.DataDirectory).Settings.SnapToSubtitles, "disabled subtitle snapping was not persisted");
            });
            await Run("Settings window has an opaque dark background and readable labels", async () =>
            {
                var type = typeof(MainWindow).Assembly.GetType("Nightingale.Desktop.PreferencesWindow")!;
                var preferences = (Window)Activator.CreateInstance(type, Store.Settings)!;
                preferences.ShowActivated = false;
                preferences.ShowInTaskbar = false;
                preferences.WindowStartupLocation = WindowStartupLocation.Manual;
                preferences.Left = -2600;
                preferences.Top = -1800;
                try
                {
                    preferences.Show();
                    await Task.Yield();
                    preferences.UpdateLayout();
                    var background = preferences.Background as SolidColorBrush;
                    Check(background is not null && background.Color.A == 255 && Luminance(background.Color) < .15,
                        "settings inherited the white system window background");
                    var labels = LogicalChildren(preferences).OfType<TextBlock>().Where(label => !string.IsNullOrWhiteSpace(label.Text)).ToArray();
                    Check(labels.Length >= 10, "settings labels were not created");
                    foreach (var label in labels)
                        Check(label.Foreground is SolidColorBrush foreground && Contrast(foreground.Color, background!.Color) >= 3,
                            $"settings label has unreadable contrast: {label.Text}");
                    var checks = LogicalChildren(preferences).OfType<CheckBox>().ToArray();
                    Check(checks.Length >= 4, "settings is missing its playback, subtitle or privacy choices");
                    foreach (var check in checks)
                        Check(check.Foreground is SolidColorBrush foreground && Contrast(foreground.Color, background!.Color) >= 4.5,
                            $"settings checkbox has unreadable contrast: {check.Content}");
                }
                finally { preferences.Close(); }
            });
            await Run("Arrow taps follow subtitle starts and subtitle delay, with a five-second fallback", async () =>
            {
                try
                {
                    Store.Settings.SubtitleDelay = .8;
                    Store.Settings.SnapToSubtitles = true;
                    Invoke("ApplySettings");
                    await Pause();
                    Player.SeekAbsolute(52);
                    await Until(() => Math.Abs(Player.Position - 52) < .3, "arrow cue origin");
                    RaiseKey(Key.Right, true);
                    RaiseKey(Key.Right, false);
                    await Until(() => Math.Abs(Player.Position - 60.8) < .3, "short Right advances to next subtitle");
                    Equal(1d, Player.GetDouble("speed"));
                    RaiseKey(Key.Left, true);
                    RouteKey(Window, Key.Left, false);
                    await Until(() => Math.Abs(Player.Position - .8) < .3, "Left returns to previous subtitle");

                    Player.SeekAbsolute(52);
                    await Until(() => Math.Abs(Player.Position - 52) < .3, "native arrow cue origin");
                    await Player.CommandAsync("keydown", "RIGHT");
                    await Player.CommandAsync("keyup", "RIGHT");
                    await Until(() => Math.Abs(Player.Position - 60.8) < .3, "native short Right advances to next subtitle");
                    await Player.CommandAsync("keypress", "LEFT");
                    await Until(() => Math.Abs(Player.Position - .8) < .3, "native Left returns to previous subtitle");

                    Player.SeekAbsolute(52);
                    await Until(() => Math.Abs(Player.Position - 52) < .3, "modified arrow origin");
                    Invoke("HandleShortcut", "Shift+Right");
                    await Until(() => Math.Abs(Player.Position - 60.8) < .3, "Shift+Right snaps its thirty-second target");
                    Invoke("HandleShortcut", "Shift+Left");
                    await Until(() => Math.Abs(Player.Position - .8) < .3, "Shift+Left snaps its thirty-second target");

                    Store.Settings.SnapToSubtitles = false;
                    Invoke("ApplySettings");
                    Player.SeekAbsolute(52);
                    await Until(() => Math.Abs(Player.Position - 52) < .3, "five second arrow origin");
                    RaiseKey(Key.Right, true);
                    RaiseKey(Key.Right, false);
                    await Until(() => Math.Abs(Player.Position - 57) < .3, "short Right advances five seconds");
                    RaiseKey(Key.Left, true);
                    RouteKey(Window, Key.Left, false);
                    await Until(() => Math.Abs(Player.Position - 52) < .3, "Left returns five seconds");
                }
                finally
                {
                    Store.Settings.SnapToSubtitles = false;
                    Store.Settings.SubtitleDelay = 0;
                    Invoke("ApplySettings");
                }
            });
            await Run("Progress bar Left and Home key releases preserve the requested keyboard seek", async () =>
            {
                var controls = (PlayerControls)Window.FindName("Controls");
                var bar = (Slider)controls.FindName("SeekBar");
                foreach (var (key, expected) in new[] { (Key.Left, 85d), (Key.Home, 0d) })
                {
                    Player.SeekAbsolute(90);
                    await Until(() => Math.Abs(Player.Position - 90) < .3, "progress keyboard origin");
                    Invoke("RefreshPlayback");
                    Check(Math.Abs(bar.Value - 90) < .3, "progress bar did not reflect the last position");
                    // Route both keyboard phases through the bar, as when it retains
                    // focus after a drag. Release immediately, before a UI refresh.
                    RouteKey(bar, key, true);
                    RouteKey(bar, key, false);
                    await Until(() => Math.Abs(Player.Position - expected) < .3, $"progress {key} seek");
                    await Task.Delay(300);
                    Check(Math.Abs(Player.Position - expected) < .3, $"{key} keyup sent the stale progress value back to mpv");
                }
            });
            await Run("Audio LRC creates one subtitle, displays lyrics and exposes timeline", async () =>
            {
                await Open(fixtures.Audio);
                await Until(() => Field<IReadOnlyList<SubtitleCue>>("_cues").Count == 3, "LRC cues");
                Check(Field<bool>("_audioOnly"), "WAV was not audio only");
                Player.SeekAbsolute(2);
                await Until(() => ((TextBlock)Window.FindName("AudioSubtitle")).Text.Contains("夜雨第一句", StringComparison.Ordinal), "visible audio lyric");
                Equal(1, Player.GetTracks().Count(track => track.Type == "sub"));
                Equal(Visibility.Visible, ((FrameworkElement)Window.FindName("AudioPanel")).Visibility);
                await Pause();
            });
            await Run("Embedded cover art stays in audio mode and shows LRC", async () =>
            {
                await Open(fixtures.CoverAudio);
                await Until(() => Field<IReadOnlyList<SubtitleCue>>("_cues").Count == 3, "cover audio lyrics");
                Check(Player.GetTracks().Any(track => track.Type == "video" && track.AlbumArt), "fixture cover art was not exposed by mpv");
                Check(Field<bool>("_audioOnly"), "embedded cover classified as ordinary video");
                Player.SeekAbsolute(2);
                await Until(() => ((TextBlock)Window.FindName("AudioSubtitle")).Text.Contains("夜雨第一句", StringComparison.Ordinal), "cover lyric");
                await Pause();
            });
            await Run("Audio without lyrics clears the last lyric and has no placeholder", async () =>
            {
                await Open(fixtures.BareAudio);
                await Pause();
                await Task.Delay(300);
                Equal(Visibility.Visible, ((FrameworkElement)Window.FindName("AudioPanel")).Visibility);
                Equal(0, Field<IReadOnlyList<SubtitleCue>>("_cues").Count);
                Equal("", ((TextBlock)Window.FindName("AudioSubtitle")).Text);
                Check(!Field<SubtitleTimeline>("_timeline").IsAvailable, "lyric timeline leaked between files");
            });
            await Run("Audio picture single click toggles pause and playback", async () =>
            {
                var panel = (FrameworkElement)Window.FindName("AudioPanel");
                void Click()
                {
                    var released = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
                    panel.RaiseEvent(released);
                    Check(released.Handled, "audio picture click did not reach its playback handler");
                }
                Click();
                await Until(() => !Player.Paused, "audio picture click resumes");
                await Task.Delay(750);
                Click();
                await Until(() => Player.Paused, "audio picture click pauses");
            });
            await Run("Switching from audio back to video restores the native video surface", async () =>
            {
                await Open(fixtures.VideoA);
                Check(!Field<bool>("_audioOnly"), "video remained in audio mode");
                Equal(Visibility.Visible, ((FrameworkElement)Window.FindName("VideoHost")).Visibility);
                Equal(Visibility.Collapsed, ((FrameworkElement)Window.FindName("AudioPanel")).Visibility);
                await Pause();
            });
            await Run("Dropping onto the playlist appends without replacing or restarting playback", async () =>
            {
                Player.SeekAbsolute(24);
                await Until(() => Math.Abs(Player.Position - 24) < .3, "paused playlist position");
                var list = (ListBox)Window.FindName("MediaList");
                var data = new DataObject(DataFormats.FileDrop, new[] { fixtures.VideoB, fixtures.VideoA });
                var drop = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                    null, [data, DragDropKeyStates.None, DragDropEffects.Copy, list, new Point(12, 12)], null)!;
                drop.RoutedEvent = DragDrop.DropEvent;
                list.RaiseEvent(drop);
                await Until(() => Field<IEnumerable<MediaRow>>("_queue").Count() == 2, "playlist append");
                await Task.Delay(300);
                Check(drop.Handled, "playlist drop was not marked handled and can reach the window replacement handler");
                Check(Field<IEnumerable<MediaRow>>("_queue").Select(item => item.Path).SequenceEqual([fixtures.VideoA, fixtures.VideoB]), "playlist entries were removed, duplicated or reordered");
                Equal(fixtures.VideoA, Player.CurrentPath);
                Check(Player.Paused, "playlist append resumed paused playback");
                Check(Math.Abs(Player.Position - 24) < .3, "playlist append changed the current position");
            });
            await Run("Native video single click toggles pause and playback", async () =>
            {
                await Player.CommandAsync("keypress", "MBTN_LEFT");
                await Until(() => !Player.Paused, "native video click resumes");
                await Task.Delay(750);
                await Player.CommandAsync("keypress", "MBTN_LEFT");
                await Until(() => Player.Paused, "native video click pauses");
            });
            await Run("WPF Right hold temporarily uses 3x then restores the selected speed", async () =>
            {
                Store.Settings.Speed = 1.5;
                Invoke("ApplySettings");
                Player.Play();
                await Until(() => !Player.Paused && Math.Abs(Player.GetDouble("speed") - 1.5) < .01, "selected 1.5x playback");
                try
                {
                    RaiseKey(Key.Right, true);
                    await Task.Delay(120);
                    Check(Math.Abs(Player.GetDouble("speed") - 1.5) < .01, "short press started 3x before the hold threshold");
                    await Until(() => Math.Abs(Player.GetDouble("speed") - 3) < .01, "Right down boosts to 3x");
                    RaiseKey(Key.Right, true);
                    Invoke("SavePlayback");
                    Equal(1.5, Store.Settings.Speed);
                    Equal(1.5, new StateStore(Store.DataDirectory).Settings.Speed);
                    RaiseKey(Key.Right, false);
                    await Until(() => Math.Abs(Player.GetDouble("speed") - 1.5) < .01, "Right up restores 1.5x");
                    Check(!Player.Paused, "Right release paused active playback");

                    await Pause();
                    var pausedPosition = Player.Position;
                    RaiseKey(Key.Right, true);
                    await Task.Delay(420);
                    Check(Player.Paused, "Right hold unexpectedly resumed a paused file");
                    RaiseKey(Key.Right, false);
                    await Until(() => Math.Abs(Player.GetDouble("speed") - 1.5) < .01, "paused Right release restores 1.5x");
                    Check(Player.Paused, "Right release unexpectedly resumed a paused file");
                    await Task.Delay(150);
                    Check(Math.Abs(Player.Position - pausedPosition) < .3, "releasing a long Right hold performed a short-press seek");
                }
                finally
                {
                    RaiseKey(Key.Right, false);
                    await Pause();
                    Store.Settings.Speed = 1;
                    Invoke("ApplySettings");
                }
            });
            await Run("Native Right down/up and lost focus restore the selected playback speed", async () =>
            {
                Store.Settings.Speed = 1.5;
                Invoke("ApplySettings");
                Player.Play();
                await Until(() => !Player.Paused, "play before native Right hold");
                try
                {
                    await Player.CommandAsync("keydown", "RIGHT");
                    await Until(() => Math.Abs(Player.GetDouble("speed") - 3) < .01, "native Right down boosts to 3x");
                    await Player.CommandAsync("keyup", "RIGHT");
                    await Until(() => Math.Abs(Player.GetDouble("speed") - 1.5) < .01, "native Right up restores 1.5x");

                    await Player.CommandAsync("keydown", "RIGHT");
                    await Until(() => Math.Abs(Player.GetDouble("speed") - 3) < .01, "native second Right hold");
                    Invoke("WindowDeactivated", Window, EventArgs.Empty);
                    await Until(() => Math.Abs(Player.GetDouble("speed") - 1.5) < .01, "focus loss cancels 3x");
                    await Player.CommandAsync("keyup", "RIGHT");
                    Equal(1.5, Store.Settings.Speed);
                    Check(!Player.Paused, "focus loss interrupted active playback");

                    await Pause();
                    Player.SeekAbsolute(42);
                    await Until(() => Math.Abs(Player.Position - 42) < .3, "native canceled tap origin");
                    await Player.CommandAsync("keydown", "RIGHT");
                    await Until(() => Field<bool>("_rightPressed"), "native short press begins");
                    Invoke("WindowDeactivated", Window, EventArgs.Empty);
                    await Player.CommandAsync("keyup", "RIGHT");
                    await Task.Delay(160);
                    Check(Math.Abs(Player.Position - 42) < .3, "a canceled native Right press performed a seek when released");
                }
                finally
                {
                    await Player.CommandAsync("keyup", "RIGHT");
                    await Pause();
                    Store.Settings.Speed = 1;
                    Invoke("ApplySettings");
                }
            });
            await Run("Subtitle menu cancels temporary speed and reflects saved snapping choices", async () =>
            {
                Store.Settings.Speed = 1.5;
                Store.Settings.SnapToSubtitles = false;
                Invoke("ApplySettings");
                Player.Play();
                await Until(() => !Player.Paused, "play before menu");
                try
                {
                    RaiseKey(Key.Right, true);
                    await Until(() => Math.Abs(Player.GetDouble("speed") - 3) < .01, "hold Right before menu");
                    var menu = await OpenSubtitleMenu();
                    await Until(() => Math.Abs(Player.GetDouble("speed") - 1.5) < .01, "opening menu cancels temporary speed");
                    var snap = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "进度定位吸附到字幕起点"));
                    Check(snap.IsCheckable && !snap.IsChecked, "subtitle menu did not reflect disabled snapping");
                    // Route the menu's Click event without relying on a physical
                    // pointer or a foreground popup in this isolated test window.
                    snap.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, snap));
                    Check(Store.Settings.SnapToSubtitles, "click did not check the subtitle snap option");
                    var enabledInMemory = Store.Settings.SnapToSubtitles;
                    var enabledOnDisk = new StateStore(Store.DataDirectory).Settings.SnapToSubtitles;
                    Check(enabledInMemory && enabledOnDisk, $"menu click did not persist enabled snapping (memory={enabledInMemory}, disk={enabledOnDisk})");
                    menu.IsOpen = false;
                    await Until(() => !Field<bool>("_menuOpen"), "subtitle menu closes");

                    menu = await OpenSubtitleMenu();
                    snap = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "进度定位吸附到字幕起点"));
                    Check(snap.IsChecked, "reopened menu lost the saved checked state");
                    snap.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, snap));
                    Check(!Store.Settings.SnapToSubtitles && !new StateStore(Store.DataDirectory).Settings.SnapToSubtitles, "menu click did not persist disabled snapping");
                }
                finally
                {
                    if (_openedMenu is not null) _openedMenu.IsOpen = false;
                    RaiseKey(Key.Right, false);
                    await Pause();
                    Store.Settings.SnapToSubtitles = false;
                    Store.Settings.Speed = 1;
                    Invoke("ApplySettings");
                }
            });
            await Run("Quick A/B switching preserves the loaded path and per-file history", async () =>
            {
                Player.SeekAbsolute(37);
                await Until(() => Math.Abs(Player.Position - 37) < .3, "history origin");
                Invoke("SavePlayback");
                for (var index = 0; index < 8; index++)
                {
                    Invoke("PlayFile", fixtures.VideoB);
                    Invoke("PlayFile", fixtures.VideoA);
                }
                await WaitLoaded(fixtures.VideoA);
                await Until(() => Math.Abs(Player.Position - 37) < 2, "A resume after replacement");
                await Pause();
                Equal(fixtures.VideoA, Player.CurrentPath);
                Invoke("SavePlayback");
                Check(Math.Abs(Store.Find(fixtures.VideoA)!.Position - 37) < 2, "A history corrupted by B event");
                Check(Store.Find(fixtures.VideoB) is null, "never-loaded B received A history");
            });
            await Run("Resume survives switching files and persisted state reload", async () =>
            {
                Player.SeekAbsolute(67);
                await Until(() => Math.Abs(Player.Position - 67) < .3, "resume origin");
                await Open(fixtures.Audio);
                await Pause();
                await Open(fixtures.VideoA);
                await Until(() => Player.Position >= 66.7 && Player.Position < 70, "restored position");
                await Pause();
                Invoke("SavePlayback");
                var reloaded = new StateStore(Store.DataDirectory);
                Check(reloaded.Find(fixtures.VideoA)!.Position is >= 66.7 and < 70, "saved state lost resume position");
            });
            await Run("Missing subtitle and nonfatal property error preserve active playback", async () =>
            {
                await InvokeTask("LoadSubtitleAsync", Path.Combine(fixtures.Directory, "missing.srt"), true, true);
                Check(Field<bool>("_loaded"), "subtitle failure unloaded media");
                Player.SetProperty("nightingale-invalid-test-property", "yes");
                await Task.Delay(300);
                Check(Field<bool>("_loaded"), "property failure unloaded media");
                Equal(fixtures.VideoA, Field<string>("_currentPath"));
                Equal(Visibility.Collapsed, ((FrameworkElement)Window.FindName("WelcomePanel")).Visibility);
            });
            await Run("Sleep timer pauses and saves the active file", async () =>
            {
                Player.Play();
                await Until(() => !Player.Paused, "unpause before timer");
                Invoke("SetTimer", .02d);
                await Until(() => Player.Paused && Field<DateTimeOffset?>("_sleepDeadline") is null, "timer expiration", 5000);
                Check(Field<bool>("_loaded"), "timer discarded loaded state");
                Check(Store.Find(fixtures.VideoA)!.Position > 0, "timer did not save position");
            });
            await Run("Natural EOF advances the queue and marks completed items from the beginning", async () =>
            {
                await InvokeTask("OpenInputs", new string[] { fixtures.ShortA, fixtures.ShortB }, false);
                await WaitLoaded(fixtures.ShortA);
                await WaitLoaded(fixtures.ShortB, 9000);
                var first = Store.Find(fixtures.ShortA);
                Check(first is not null && first.Position == 0, "completed first item not reset");
                await Until(() => !Field<bool>("_loaded") && Field<string>("_playbackStatus") == "播放完毕", "last EOF", 9000);
                Equal(0d, Store.Find(fixtures.ShortB)!.Position);
                var saved = new StateStore(Store.DataDirectory);
                Equal(0d, saved.Find(fixtures.ShortA)!.Position);
                Equal(0d, saved.Find(fixtures.ShortB)!.Position);
            });
            if (!skipFullscreen)
            {
                await Run("Fullscreen controls appear and Escape restores window mode", async () =>
                {
                    await Open(fixtures.VideoA);
                    await Pause();
                    Invoke("ToggleFullscreen");
                    try
                    {
                        Check(Field<bool>("_fullscreen"), "did not enter fullscreen");
                        Check(Field<Window>("_fullscreenControls").IsVisible, "fullscreen controls are hidden");
                        var source = PresentationSource.FromVisual(Window) ?? throw new Exception("missing window presentation source");
                        var key = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                        Invoke("WindowKeyDown", Window, key);
                        Check(key.Handled, "Escape was not handled");
                        Check(!Field<bool>("_fullscreen"), "Escape did not exit fullscreen");
                        Check(Field<Window?>("_fullscreenControls") is null, "fullscreen controls were retained");
                    }
                    finally { if (Field<bool>("_fullscreen")) Invoke("ToggleFullscreen"); }
                });
            }
            await Run("Closing and reopening preserves recent files, displayed history and resume position", async () =>
            {
                await Open(fixtures.VideoA);
                await Pause();
                Player.SeekAbsolute(73);
                await Until(() => Math.Abs(Player.Position - 73) < .3, "close/reopen history origin");
                var dataDirectory = Store.DataDirectory;
                Window.Close();
                CreateWindow([]);
                await Until(() => Field<bool>("_showHistory"), "recent list on fresh window");
                Equal(dataDirectory, Store.DataDirectory);
                var list = (ListBox)Window.FindName("MediaList");
                Check(list.Items.OfType<MediaRow>().Any(row => row.Path == fixtures.VideoA), "recent list is empty or missing the saved file after reopening");
                Check(Field<IEnumerable<MediaRow>>("_queue").Any(row => row.Path == fixtures.VideoA), "saved playlist was not restored into the fresh window");
                Check(!Field<bool>("_loaded"), "reopening unexpectedly started playback");
                Check(Math.Abs(Store.Find(fixtures.VideoA)!.Position - 73) < .3, "closing did not persist the final playback position");
                Equal(fixtures.VideoA, Store.History[0].Path);
                await Open(fixtures.VideoA);
                await Until(() => Player.Position is >= 72.7 and < 76, "fresh window resumes saved position");
                await Pause();
            });
            await Run("Incognito persists across reopening without changing old records or writing private files", async () =>
            {
                var privatePath = Path.Combine(fixtures.Directory, "隐私检查 private-session.mp4");
                File.Copy(fixtures.VideoB, privatePath);
                Invoke("SavePlayback");
                var previous = Store.Find(fixtures.VideoA)!;
                var previousPosition = previous.Position;
                var previousTime = previous.LastPlayedUtc;
                var historyCount = Store.History.Count;
                var ordinaryQueue = Store.Playlist.ToArray();
                // Preferences updates the shared settings first, then emits its
                // SettingsChanged event. Exercise that ordering, which differs
                // from the direct checkable menu command below.
                Store.Settings.Incognito = true;
                Invoke("OnSettingsChanged");
                Check(Store.Settings.Incognito && new StateStore(Store.DataDirectory).Settings.Incognito, "incognito choice was not saved");
                Check(Store.Find(fixtures.VideoA) is null, "incognito still restores previous viewing positions");
                await Open(fixtures.VideoA);
                await Pause();
                Check(Player.Position < 3, "incognito resumed an existing history entry");
                Player.SeekAbsolute(22);
                await Until(() => Math.Abs(Player.Position - 22) < .3, "incognito existing-file seek");
                Invoke("SavePlayback");
                var retained = Store.History.Single(entry => entry.Path == fixtures.VideoA);
                Equal(previousPosition, retained.Position);
                Equal(previousTime, retained.LastPlayedUtc);
                await Open(privatePath);
                await Pause();
                Player.SeekAbsolute(19);
                await Until(() => Math.Abs(Player.Position - 19) < .3, "private new-file seek");
                Invoke("SavePlayback");
                Equal(historyCount, Store.History.Count);
                var privateAudio = Path.Combine(fixtures.Directory, "隐私检查 private-lyrics.wav");
                File.Copy(fixtures.Audio, privateAudio);
                File.Copy(Path.ChangeExtension(fixtures.Audio, ".lrc"), Path.ChangeExtension(privateAudio, ".lrc"));
                var subtitleDirectory = Path.Combine(Store.DataDirectory, "subtitles");
                var cacheBefore = Directory.Exists(subtitleDirectory) ? Directory.GetFiles(subtitleDirectory).Order().ToArray() : [];
                await Open(privateAudio);
                await Until(() => Field<IReadOnlyList<SubtitleCue>>("_cues").Count == 3, "private LRC in-memory timeline");
                await Pause();
                Player.SeekAbsolute(2);
                await Until(() => ((TextBlock)Window.FindName("AudioSubtitle")).Text.Contains("夜雨第一句", StringComparison.Ordinal), "private in-memory lyric displays");
                Invoke("SavePlayback");
                var cacheAfter = Directory.Exists(subtitleDirectory) ? Directory.GetFiles(subtitleDirectory).Order().ToArray() : [];
                Check(cacheBefore.SequenceEqual(cacheAfter), "private LRC left a converted subtitle cache file on disk");
                CheckStateExcludes(Store.DataDirectory, Path.GetFileName(privateAudio));
                var dataDirectory = Store.DataDirectory;
                Window.Close();
                CreateWindow([]);
                await Until(() => Field<bool>("_showHistory"), "recent list after private close");
                Check(Store.Settings.Incognito, "reopening lost incognito mode");
                Equal(historyCount, Store.History.Count);
                Equal(0, ((ListBox)Window.FindName("MediaList")).Items.Count);
                retained = Store.History.Single(entry => entry.Path == fixtures.VideoA);
                Equal(previousPosition, retained.Position);
                Equal(previousTime, retained.LastPlayedUtc);
                Check(!Store.History.Any(entry => entry.Path == privatePath), "private path was retained in history");
                CheckStateExcludes(dataDirectory, Path.GetFileName(privatePath));

                await Open(privatePath);
                await Pause();
                Invoke("SetIncognito", false);
                Check(!Store.Settings.Incognito, "incognito did not disable");
                Player.SeekAbsolute(24);
                await Until(() => Math.Abs(Player.Position - 24) < .3, "private file after disabling incognito");
                Invoke("SavePlayback");
                Check(!Store.History.Any(entry => entry.Path == privatePath), "disabling incognito retroactively recorded the active private file");
                Check(new StateStore(dataDirectory).Playlist.SequenceEqual(ordinaryQueue), "disabling incognito replaced the saved ordinary queue with the private session");
                CheckStateExcludes(dataDirectory, Path.GetFileName(privatePath));
                var normalPath = Path.Combine(fixtures.Directory, "公开检查 normal-session.mp4");
                File.Copy(fixtures.VideoB, normalPath);
                await Open(normalPath);
                await Pause();
                Player.SeekAbsolute(31);
                await Until(() => Math.Abs(Player.Position - 31) < .3, "new ordinary file seek");
                Invoke("SavePlayback");
                Check(Math.Abs(new StateStore(dataDirectory).Find(normalPath)!.Position - 31) < .3, "new ordinary playback after incognito did not persist");
                CheckStateExcludes(dataDirectory, Path.GetFileName(privatePath));
            });
            await Run("No dispatcher errors or unexpected unmute occurred", () =>
            {
                Check(_dispatcherErrors.Count == 0, string.Join("\n", _dispatcherErrors));
                Check(Store.Settings.Muted, "fixture unmuted itself");
                return Task.CompletedTask;
            });
            ExitCode = _failed == 0 ? 0 : 1;
        }
        catch (Exception error) { Console.Error.WriteLine("HARNESS ERROR " + error); _failed++; }
        finally
        {
            _window?.Close();
            Console.WriteLine($"\nIntegration results: {_passed} passed, {_failed} failed.");
            Shutdown(ExitCode);
        }
    }

    private async Task Run(string name, Func<Task> body)
    {
        var timer = Stopwatch.StartNew();
        try { await body(); _passed++; Console.WriteLine($"PASS {name} ({timer.Elapsed.TotalSeconds:0.00}s)"); }
        catch (Exception error)
        {
            _failed++;
            Console.Error.WriteLine($"FAIL {name} ({timer.Elapsed.TotalSeconds:0.00}s)\n{error}\nState: {Diagnostic()}");
        }
    }
    private void CreateWindow(string[] files)
    {
        _window = new MainWindow(files)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -2600,
            Top = -1800
        };
        MainWindow = _window;
        _window.Show();
    }
    private static IEnumerable<DependencyObject> LogicalChildren(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in LogicalChildren(child)) yield return descendant;
        }
    }
    private static double Luminance(Color color)
    {
        static double Linear(byte value) => value / 255d <= .04045 ? value / 255d / 12.92 : Math.Pow((value / 255d + .055) / 1.055, 2.4);
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
    private static double Contrast(Color left, Color right)
    {
        var a = Luminance(left);
        var b = Luminance(right);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    private static void CheckStateExcludes(string directory, string privateFileName)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "state.json*"))
        {
            var text = File.ReadAllText(file);
            // JsonSerializer escapes Chinese characters by default. Decode JSON
            // strings before checking for a private filename in either saved copy.
            var unescaped = System.Text.RegularExpressions.Regex.Unescape(text);
            Check(!unescaped.Contains(privateFileName, StringComparison.Ordinal), $"private filename leaked to {Path.GetFileName(file)}");
        }
    }
    private async Task Open(string path)
    {
        await InvokeTask("OpenInputs", new string[] { path }, false);
        await WaitLoaded(path);
    }
    private Task WaitLoaded(string path, int milliseconds = 12000) => Until(() => Field<bool>("_loaded")
        && string.Equals(Field<string>("_currentPath"), path, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Player.CurrentPath, path, StringComparison.OrdinalIgnoreCase)
        && Player.Duration > 0, "loaded " + Path.GetFileName(path), milliseconds);
    private async Task Pause()
    {
        Player.Pause();
        await Until(() => Player.Paused, "pause");
    }
    private async Task Until(Func<bool> predicate, string reason, int milliseconds = 10000)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.ElapsedMilliseconds > milliseconds) throw new TimeoutException(reason);
            await Task.Delay(35);
        }
    }
    private T Field<T>(string name) => (T)(typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window)!);
    private object? Invoke(string name, params object?[] arguments)
    {
        try { return typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, arguments); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    private Task InvokeTask(string name, params object?[] arguments) => (Task)(Invoke(name, arguments) ?? throw new Exception(name + " did not return Task"));
    private void RaiseKey(Key key, bool pressed)
    {
        var args = RouteKey(Window, key, pressed);
        Check(args.Handled, $"{key} {(pressed ? "down" : "up")} did not reach the application shortcut handler");
    }
    private KeyEventArgs RouteKey(UIElement target, Key key, bool pressed)
    {
        var source = PresentationSource.FromVisual(Window) ?? throw new Exception("missing window presentation source");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = pressed ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent
        };
        target.RaiseEvent(args);
        if (!args.Handled)
        {
            args.RoutedEvent = pressed ? Keyboard.KeyDownEvent : Keyboard.KeyUpEvent;
            target.RaiseEvent(args);
        }
        return args;
    }
    private async Task<ContextMenu> OpenSubtitleMenu()
    {
        _openedMenu = null;
        Invoke("ShowSubtitleMenu", (FrameworkElement)Window.FindName("Controls"));
        await Until(() => _openedMenu?.IsOpen == true && Field<bool>("_menuOpen"), "subtitle menu opens");
        return _openedMenu!;
    }
    private string Diagnostic()
    {
        try { return $"loaded={Field<bool>("_loaded")}, uiPath={Field<string>("_currentPath")}, mpvPath={Player.CurrentPath}, pos={Player.Position}, duration={Player.Duration}, status={Field<string>("_playbackStatus")}, tracks={string.Join("; ", Player.GetTracks())}"; }
        catch (Exception error) { return error.Message; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected <{expected}>, actual <{actual}>"); }
}

internal sealed record Fixtures(string Directory, string VideoA, string VideoB, string Audio, string CoverAudio, string BareAudio, string ShortA, string ShortB)
{
    public static Fixtures Create(string root, string run)
    {
        var directory = Path.Combine(run, "media");
        System.IO.Directory.CreateDirectory(directory);
        var ffmpeg = Environment.GetEnvironmentVariable("NIGHTINGALE_FFMPEG") ?? @"D:\tools\ffmpeg\ffmpeg-9.0.1-essentials_build\bin\ffmpeg.exe";
        if (!File.Exists(ffmpeg)) throw new FileNotFoundException("Set NIGHTINGALE_FFMPEG to the installed ffmpeg.exe", ffmpeg);
        var videoA = Path.Combine(directory, "中文 视频 A.mp4");
        var videoB = Path.Combine(directory, "中文 视频 B.mp4");
        var sourceVideo = Path.Combine(root, "tmp", "qa-media", "中文 测试.mp4");
        if (File.Exists(sourceVideo)) File.Copy(sourceVideo, videoA);
        else RunFfmpeg(ffmpeg, "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=15", "-f", "lavfi", "-i", "sine=frequency=220", "-t", "180", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", videoA);
        File.Copy(videoA, videoB);
        File.WriteAllText(Path.ChangeExtension(videoA, ".srt"), "1\n00:00:00,000 --> 00:01:00,000\n夜莺第一句\n\n2\n00:01:00,000 --> 00:02:00,000\n夜莺第二句\n\n3\n00:02:00,000 --> 00:03:00,000\n夜莺第三句\n", new UTF8Encoding(false));
        var audio = Path.Combine(directory, "夜雨 音频.wav");
        RunFfmpeg(ffmpeg, "-f", "lavfi", "-i", "sine=frequency=220:sample_rate=44100", "-t", "45", "-c:a", "pcm_s16le", audio);
        var coverAudio = Path.Combine(directory, "夜雨 封面.mp3");
        RunFfmpeg(ffmpeg, "-i", audio, "-i", Path.Combine(root, "assets", "nightingale.png"), "-map", "0:a", "-map", "1:v", "-c:a", "libmp3lame", "-c:v", "copy", "-id3v2_version", "3", "-disposition:v", "attached_pic", coverAudio);
        const string lyrics = "[00:00.00]夜雨第一句\n[00:10.00]夜雨第二句\n[00:20.00]夜雨第三句\n";
        File.WriteAllText(Path.ChangeExtension(audio, ".lrc"), lyrics, new UTF8Encoding(false));
        File.WriteAllText(Path.ChangeExtension(coverAudio, ".lrc"), lyrics, new UTF8Encoding(false));
        var bareAudio = Path.Combine(directory, "无歌词 音频.wav");
        File.Copy(audio, bareAudio);
        var shortA = Path.Combine(directory, "短片 1.mp4");
        var shortB = Path.Combine(directory, "短片 2.mp4");
        RunFfmpeg(ffmpeg, "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=15", "-f", "lavfi", "-i", "sine=frequency=110", "-t", "2", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", shortA);
        File.Copy(shortA, shortB);
        return new Fixtures(directory, videoA, videoB, audio, coverAudio, bareAudio, shortA, shortB);
    }
    private static void RunFfmpeg(string executable, params string[] args)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin" }.Concat(args)) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception("ffmpeg fixture failed: " + error);
    }
}

internal sealed class DualWriter(TextWriter console, TextWriter file) : TextWriter
{
    public override Encoding Encoding => Encoding.UTF8;
    public override void Write(char value) { console.Write(value); file.Write(value); }
    public override void WriteLine(string? value) { console.WriteLine(value); file.WriteLine(value); }
    public override void Flush() { console.Flush(); file.Flush(); }
}
