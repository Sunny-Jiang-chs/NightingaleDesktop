using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nightingale.Core;
using Nightingale.Desktop.Platform;

namespace Nightingale.Desktop;

internal sealed class PreferencesWindow : Window
{
    public event Action? SettingsChanged;
    public PreferencesWindow(AppSettings settings)
    {
        SetResourceReference(StyleProperty, "AppWindowStyle");
        Title = "夜莺 · 设置"; Width = 520; Height = 660; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false; ResizeMode = ResizeMode.CanResizeWithGrip;
        var body = new StackPanel { Margin = new Thickness(26) };
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(Heading("播放与窗口"));
        body.Children.Add(Check("始终置顶", settings.KeepOnTop, value => settings.KeepOnTop = value));
        body.Children.Add(Check("循环播放列表", settings.Repeat, value => settings.Repeat = value));
        body.Children.Add(Heading("隐私与播放记录"));
        body.Children.Add(Check("无痕播放", settings.Incognito, value => settings.Incognito = value));
        body.Children.Add(new TextBlock { Text = "开启后，不新增最近播放、播放进度或恢复队列，也不读取旧进度续播。此前保存的记录会保留。\n关闭无痕播放后，新播放的内容将正常保存。", Foreground = new SolidColorBrush(Color.FromRgb(164, 177, 190)), Margin = new Thickness(0, 0, 0, 14), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Heading("字幕"));
        body.Children.Add(Check("进度定位吸附到字幕起点", settings.SnapToSubtitles, value => settings.SnapToSubtitles = value));
        body.Children.Add(new TextBlock { Text = "开启后，短按 ← / → 跳到上一句 / 下一句起点；点击、拖动进度和 Shift + 方向键跳转也会吸附，并计入字幕延迟。\n需要加载并选中 SRT、VTT 或 LRC 字幕。", Foreground = new SolidColorBrush(Color.FromRgb(164, 177, 190)), Margin = new Thickness(0, 0, 0, 14), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = "愿你今晚睡得安稳", Foreground = new SolidColorBrush(Color.FromRgb(128, 220, 200)), FontSize = 22, Margin = new Thickness(0, 0, 0, 14) });
        body.Children.Add(Range("字号", 16, 64, settings.SubtitleFontSize, value => settings.SubtitleFontSize = value));
        body.Children.Add(Range("底部距离", 0, 150, settings.SubtitleBottomMargin, value => settings.SubtitleBottomMargin = (int)value));
        var delayRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        delayRow.Children.Add(new TextBlock { Text = "字幕延迟（秒）", Width = 126, VerticalAlignment = VerticalAlignment.Center });
        var delayInput = new TextBox { Text = settings.SubtitleDelay.ToString("0.##", CultureInfo.CurrentCulture), Width = 85 };
        delayRow.Children.Add(delayInput);
        var applyDelay = new Button { Content = "应用", Margin = new Thickness(8, 0, 0, 0) };
        var validation = new TextBlock { Text = "正数延后，负数提前", Foreground = new SolidColorBrush(Color.FromRgb(164, 177, 190)), Margin = new Thickness(0, 8, 0, 0) };
        applyDelay.Click += (_, _) =>
        {
            if (double.TryParse(delayInput.Text, out var value) && double.IsFinite(value) && Math.Abs(value) <= 3600)
            { settings.SubtitleDelay = value; SettingsChanged?.Invoke(); validation.Text = "字幕延迟已应用"; }
            else validation.Text = "请输入 -3600 到 3600 之间的秒数";
        };
        delayRow.Children.Add(applyDelay); body.Children.Add(delayRow); body.Children.Add(validation);
        body.Children.Add(Heading("Windows"));
        var defaults = new Button { Content = "设为默认播放器…", HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Color.FromRgb(35, 75, 69)) };
        defaults.Click += (_, _) => { try { DefaultApps.OpenSettings(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法打开系统设置"); } };
        body.Children.Add(defaults);
        body.Children.Add(new TextBlock { Text = "安装版可在系统“默认应用”中选择夜莺。", Foreground = new SolidColorBrush(Color.FromRgb(164, 177, 190)), Margin = new Thickness(0, 9, 0, 0), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Heading("快捷键"));
        body.Children.Add(new TextBlock { Text = "空格 / 单击画面  播放 / 暂停      F / 双击  全屏\n短按 ← / →  后退 / 前进 5 秒\n开启字幕吸附后，短按 ← / → 改为上一句 / 下一句\n按住 → 超过 0.3 秒  临时三倍速，松开恢复原速\nShift + ← / →  后退 / 前进 30 秒（遵循字幕吸附）\n↑ / ↓  音量      M  静音      Home  从头播放\n[ / ]  上一句 / 下一句      L  播放列表\nCtrl + O  打开文件      Ctrl + Shift + O  打开文件夹\nCtrl + ← / →  上一个 / 下一个文件", LineHeight = 25, Foreground = new SolidColorBrush(Color.FromRgb(183, 196, 207)), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = "夜莺播放器 0.1.2 · Windows", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(164, 177, 190)), Margin = new Thickness(0, 24, 0, 0) });
    }
    private CheckBox Check(string text, bool value, Action<bool> update)
    {
        var control = new CheckBox { Content = text, IsChecked = value };
        control.Click += (_, _) => { update(control.IsChecked == true); SettingsChanged?.Invoke(); };
        return control;
    }
    private UIElement Range(string title, double min, double max, double value, Action<double> update)
    {
        var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });
        row.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = value.ToString("0"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(label, 2); row.Children.Add(label);
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, IsSnapToTickEnabled = true, TickFrequency = 1, IsMoveToPointEnabled = true };
        System.Windows.Automation.AutomationProperties.SetName(slider, title);
        slider.ValueChanged += (_, e) => { label.Text = e.NewValue.ToString("0"); update(e.NewValue); SettingsChanged?.Invoke(); };
        Grid.SetColumn(slider, 1); row.Children.Add(slider);
        return row;
    }
    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 12) };
}

internal sealed class InputDialog : Window
{
    private InputDialog() => SetResourceReference(StyleProperty, "AppWindowStyle");

    public static string? Ask(Window owner, string title, string message, string value)
    {
        var window = new InputDialog { Owner = owner, Title = title, Width = 380, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        var input = new TextBox { Text = value };
        panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var okay = new Button { Content = "确定", IsDefault = true, Margin = new Thickness(8, 0, 0, 0), Background = new SolidColorBrush(Color.FromRgb(35, 75, 69)) };
        okay.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(okay); panel.Children.Add(buttons);
        window.Content = panel;
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return window.ShowDialog() == true ? input.Text : null;
    }
}
