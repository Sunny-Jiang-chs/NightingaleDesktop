using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using Nightingale.Core;
using Nightingale.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "src", "Nightingale.Desktop"))) root = root.Parent;
        if (root == null) throw new InvalidOperationException("Run tests within the project's tmp build directory.");
        var source = XDocument.Load(Path.Combine(root.FullName, "src", "Nightingale.Desktop", "App.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        app.Resources = (ResourceDictionary)XamlReader.Parse(new XElement(presentation + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            source.Root!.Element(presentation + "Application.Resources")!.Elements()).ToString());
        var result = 1;
        var pointerSuite = Environment.GetCommandLineArgs().Contains("--pointer", StringComparer.OrdinalIgnoreCase);
        app.Startup += async (_, _) =>
        {
            var controls = new PlayerControls();
            var window = new Window
            {
                Content = controls, Width = 960, Height = 160, ShowActivated = pointerSuite,
                ShowInTaskbar = false, Topmost = pointerSuite, WindowStyle = WindowStyle.None,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = pointerSuite ? 50 : -2600, Top = pointerSuite ? 50 : -1800
            };
            NativePoint originalPointer = default;
            if (pointerSuite) GetCursorPos(out originalPointer);
            try
            {
                window.Show();
                if (pointerSuite) window.Activate();
                await System.Threading.Tasks.Task.Delay(100);
                var slider = (Slider)controls.FindName("SeekBar");
                var volume = (Slider)controls.FindName("VolumeBar");
                var hwnd = new WindowInteropHelper(window).Handle;
                var requested = new List<(long Id, double Position)>();
                controls.SeekRequested += position => requested.Add((controls.SeekRequestId, position));
                void Update(double position) => controls.Update(position, 100, true, 75, false, 1, "", "", false);
                void Check(bool pass, string message)
                {
                    if (!pass) throw new InvalidOperationException(message);
                    Console.WriteLine("PASS " + message);
                }
                // Window's implicit style does not apply to subclasses. Verify
                // the actual dialog types explicitly resolve the shared theme.
                var settings = new AppSettings();
                Window CreateDialog(string name, params object[] arguments) => (Window)Activator.CreateInstance(
                    typeof(PlayerControls).Assembly.GetType("Nightingale.Desktop." + name)!,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    binder: null, args: arguments, culture: null)!;
                foreach (var dialogName in new[] { "PreferencesWindow", "InputDialog", "FullscreenControlsWindow" })
                {
                    var dialog = dialogName == "PreferencesWindow" ? CreateDialog(dialogName, settings) : CreateDialog(dialogName);
                    var expectedBackground = dialogName == "FullscreenControlsWindow" ? Color.FromRgb(24, 29, 35) : Color.FromRgb(17, 21, 25);
                    Check(dialog.Background is SolidColorBrush background && background.Color == expectedBackground,
                        dialogName + " resolves its dark background instead of the system white default");
                    Check(dialog.Foreground is SolidColorBrush foreground && foreground.Color == Color.FromRgb(231, 238, 243),
                        dialogName + " resolves readable light text");
                    if (dialogName == "PreferencesWindow")
                    {
                        var incognito = Descendants(dialog).OfType<CheckBox>().Single(check => Equals(check.Content, "无痕播放"));
                        incognito.IsChecked = true;
                        incognito.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Check(settings.Incognito, "preferences incognito checkbox updates the shared settings object");
                    }
                    dialog.Close();
                }
                async System.Threading.Tasks.Task SendPointer(uint message, double fraction, double y = 16)
                {
                    var desired = new Point(8 + fraction * (slider.ActualWidth - 16), y);
                    var point = slider.TranslatePoint(desired, window);
                    var devicePoint = PresentationSource.FromVisual(window)!.CompositionTarget.TransformToDevice.Transform(point);
                    var screenPoint = slider.PointToScreen(desired);
                    SetCursorPos((int)screenPoint.X, (int)screenPoint.Y);
                    Mouse.Synchronize();
                    var location = ((int)devicePoint.Y << 16) | ((int)devicePoint.X & 0xffff);
                    // Update the native pointer in this fixture, then deliver real
                    // WPF preview events through the hit visual's production route.
                    // Raw WM_LBUTTONDOWN is suppressed by Windows when another
                    // process is foreground, making it unsuitable for this test.
                    SendMessage(hwnd, 0x200, 0, location);
                    // The WPF MouseDevice position can lag SetCursorPos until the
                    // input dispatcher processes the synthesized move. Wait for
                    // that position before constructing the routed button event.
                    for (var attempt = 0; attempt < 20; attempt++)
                    {
                        await System.Threading.Tasks.Task.Delay(10);
                        var actual = Mouse.GetPosition(slider);
                        if (Math.Abs(actual.X - desired.X) < 2 && Math.Abs(actual.Y - desired.Y) < 2) break;
                        if (attempt == 19) throw new InvalidOperationException($"Mouse position did not settle at target. Expected {desired}; WPF reported {actual}.");
                        SetCursorPos((int)screenPoint.X, (int)screenPoint.Y);
                        Mouse.Synchronize();
                        SendMessage(hwnd, 0x200, 0, location);
                    }
                    var target = (Mouse.Captured ?? window.InputHitTest(point)) as UIElement ?? slider;
                    if (message == 0x200)
                        target.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent });
                    else
                        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                        {
                            RoutedEvent = message == 0x201 ? Mouse.PreviewMouseDownEvent : Mouse.PreviewMouseUpEvent
                        });
                }
                Update(10);
                window.UpdateLayout();
                Check(slider.ActualHeight >= 32 && volume.ActualHeight < 32, "32px seek hit area without enlarging volume");
                var track = (Track)slider.Template.FindName("PART_Track", slider);
                Check(track.Thumb.ActualWidth == 16, "16px seek thumb");

                // RequestSeek is invoked through reflection only to establish a
                // business request without depending on the machine-wide cursor.
                // The optional --pointer suite below covers the routed input path.
                var requestMethod = typeof(PlayerControls).GetMethod("RequestSeek", BindingFlags.Instance | BindingFlags.NonPublic)!;
                void Submit(double seconds)
                {
                    slider.Value = seconds;
                    requestMethod.Invoke(controls, null);
                }
                Update(10);
                Submit(70);
                Check(requested.Count == 1 && requested[0].Position == 70, "slider request publishes its selected time");
                Update(10);
                Check(slider.Value == 70, "old playback poll cannot undo a pending seek");
                controls.AcknowledgeSeek(requested[0].Id, 62);
                Update(10);
                Check(slider.Value == 62, "snapped seek target stays visible while playback catches up");
                Update(62.2);
                Check(Math.Abs(slider.Value - 62.2) < .01, "polling resumes when playback reaches acknowledged target");
                Submit(40);
                var latest = requested[^1];
                controls.AcknowledgeSeek(requested[0].Id, 80);
                Update(80);
                Check(slider.Value == 40, "old acknowledgement cannot replace a newer seek");
                controls.CancelSeek(latest.Id);
                Update(45);
                Check(slider.Value == 45, "failed seek releases poll protection");
                Submit(30);
                var discarded = requested[^1];
                controls.ResetSeek();
                controls.AcknowledgeSeek(discarded.Id, 30);
                Update(5);
                Check(slider.Value == 5, "media reset invalidates earlier seek acknowledgement");
                var geometry = typeof(PlayerControls).GetMethod("PointerValue", BindingFlags.Instance | BindingFlags.NonPublic)!;
                double At(double x) => (double)geometry.Invoke(controls, [track, new Point(x, 16)])!;
                Check(Math.Abs(At(track.Thumb.ActualWidth / 2)) < .01 &&
                      Math.Abs(At(track.ActualWidth - track.Thumb.ActualWidth / 2) - 100) < .01,
                    "rendered track endpoints map to duration endpoints");
                Check(Math.Abs(At(track.ActualWidth / 2) - 50) < .01,
                    "rendered track midpoint maps to media midpoint");

                if (pointerSuite)
                {
                controls.ResetSeek();
                requested.Clear();
                Update(10);

                await SendPointer(0x200, .7, 2);
                await System.Threading.Tasks.Task.Delay(50);
                await SendPointer(0x201, .7, 2);
                Check(slider.IsMouseCaptured, "track press captures mouse in the transparent upper hit area");
                Check(Math.Abs(slider.Value - 70) < 1, "track press moves the actual slider to clicked time");
                Update(10);
                Check(Math.Abs(slider.Value - 70) < 1, "polling cannot overwrite active mouse gesture");
                await SendPointer(0x202, .7, 2);
                Check(requested.Count == 1 && Math.Abs(requested[0].Position - 70) < 1, "track release commits the clicked time exactly once");
                Check(!slider.IsMouseCaptured, "release relinquishes mouse capture");
                Update(10);
                Check(Math.Abs(slider.Value - 70) < 1, "old playback poll cannot undo a pending seek");
                controls.AcknowledgeSeek(requested[0].Id, 62);
                Update(10);
                Check(Math.Abs(slider.Value - 62) < .01, "snapped target stays visible until playback catches up");
                Update(62.2);
                Check(Math.Abs(slider.Value - 62.2) < .01, "playback polling resumes at acknowledged target");

                await SendPointer(0x200, .3);
                await SendPointer(0x201, .3);
                await SendPointer(0x200, .55);
                Check(Math.Abs(slider.Value - 55) < 1, "captured drag updates actual slider value");
                await SendPointer(0x202, 1.2);
                Check(requested.Count == 2 && requested[1].Position == 100, "release outside bar clamps and commits exactly once");

                Update(30);
                await SendPointer(0x200, .4);
                await SendPointer(0x201, .4);
                await SendPointer(0x202, .4);
                var newest = requested[^1];
                controls.AcknowledgeSeek(requested[0].Id, 62);
                Update(62);
                Check(Math.Abs(slider.Value - newest.Position) < .01, "old asynchronous acknowledgement cannot replace a newer seek");
                controls.CancelSeek(newest.Id);
                Update(45);
                Check(Math.Abs(slider.Value - 45) < .01, "failed seek releases poll protection");

                Update(50);
                window.UpdateLayout();
                var thumbOffset = 4 / (slider.ActualWidth - 16);
                await SendPointer(0x200, .5 + thumbOffset);
                await SendPointer(0x201, .5 + thumbOffset);
                Check(Math.Abs(slider.Value - 50) < .01, "pressing the thumb edge preserves the original time");
                await SendPointer(0x200, .7 + thumbOffset);
                await SendPointer(0x202, .7 + thumbOffset);
                Check(Math.Abs(requested[^1].Position - 70) < 1, "thumb dragging commits using the original grab offset");

                await SendPointer(0x200, .2);
                await SendPointer(0x201, .2);
                var dragPreview = slider.Value;
                controls.AcknowledgeSeek(requested[^1].Id, 85);
                Check(slider.Value == dragPreview, "an old acknowledgement cannot move a new drag preview");
                var beforeLostCapture = requested.Count;
                slider.ReleaseMouseCapture();
                await SendPointer(0x202, .2);
                Check(requested.Count == beforeLostCapture + 1, "lost capture followed by release commits only once");
                controls.ResetSeek();
                controls.AcknowledgeSeek(requested[^1].Id, 20);
                Update(0);
                Check(slider.Value == 0, "media reset invalidates previous seek acknowledgements");
                }

                var beforeKeyRelease = requested.Count;
                window.PreviewKeyUp += (_, e) => e.Handled = true;
                slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Right)
                {
                    RoutedEvent = Keyboard.PreviewKeyUpEvent
                });
                Check(requested.Count == beforeKeyRelease, "window-handled right-key release does not issue a slider seek");
                Console.WriteLine(pointerSuite ? "Deterministic and optional pointer checks passed." : "All deterministic controls and dialog theme checks passed. Use --pointer for optional mouse input checks.");
                result = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { window.Close(); if (pointerSuite) SetCursorPos(originalPointer.X, originalPointer.Y); app.Shutdown(); }
        };
        app.Run();
        return result;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
}
