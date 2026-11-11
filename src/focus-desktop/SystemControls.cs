using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using focus_desktop.Services;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Cursors = System.Windows.Input.Cursors;

namespace focus_desktop;

/// <summary>One audio state and one brightness writer for both launcher locations.</summary>
internal sealed class SystemControls : IDisposable
{
    private readonly BrightnessService _brightness = new();
    private readonly Popup _popup;
    private readonly Slider _volume = Slider("系统音量");
    private readonly Slider _light = Slider("内屏亮度");
    private readonly TextBlock _volumeText = Label("—");
    private readonly TextBlock _lightText = Label("—");
    private readonly TextBlock _error = Label("正在读取内屏亮度…");
    private readonly Button _speaker;
    private readonly Button _mute;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _writeDelay = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private bool _syncing;
    private bool _volumeDragging;
    private bool _lightDragging;
    private bool _reading;
    private bool _writing;
    private bool _disposed;
    private int? _pendingBrightness;

    internal StackPanel Launchers { get; } = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    internal bool AtDock { get; set; }

    internal SystemControls()
    {
        VolumeHelper.Init();
        _speaker = IconButton("\uE767", "音量与亮度");
        var sun = IconButton("\uE706", "音量与亮度");
        Launchers.Children.Add(_speaker);
        Launchers.Children.Add(sun);
        _mute = new Button { Content = "静音", MinHeight = 30, Margin = new Thickness(0, 8, 0, 0), ToolTip = "切换系统静音" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_mute, "SystemMute");
        _mute.Click += (_, _) => { VolumeHelper.ToggleMute(); SyncVolume(); };

        var grid = new Grid { Margin = new Thickness(16), Focusable = true };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var audio = Column("音量", _volumeText, _volume);
        audio.Children.Add(_mute);
        var light = Column("亮度", _lightText, _light);
        grid.Children.Add(audio);
        Grid.SetColumn(light, 1);
        grid.Children.Add(light);
        _error.FontSize = 11;
        _error.TextWrapping = TextWrapping.Wrap;
        _error.MaxWidth = 192;
        _error.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(_error, 1);
        Grid.SetColumnSpan(_error, 2);
        grid.Children.Add(_error);
        _popup = new Popup
        {
            StaysOpen = false, AllowsTransparency = true,
            Child = new Border { Background = new SolidColorBrush(Color.FromRgb(37, 42, 51)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(95, 107, 126)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Child = grid },
        };
        grid.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        foreach (Button button in Launchers.Children)
            button.Click += (_, _) =>
            {
                _popup.PlacementTarget = button;
                _popup.Placement = AtDock ? PlacementMode.Right : PlacementMode.Bottom;
                _popup.IsOpen = true;
                grid.Focus();
                _ = ReadBrightnessAsync();
            };
        TrackDrag(_volume, value => _volumeDragging = value);
        TrackDrag(_light, value => _lightDragging = value);
        _volume.ValueChanged += (_, e) =>
        {
            if (_syncing) return;
            VolumeHelper.Set((int)e.NewValue);
            SyncVolume();
        };
        _light.ValueChanged += (_, e) =>
        {
            if (_syncing) return;
            _lightText.Text = $"{(int)e.NewValue}%";
            _pendingBrightness = (int)e.NewValue;
            if (!_writeDelay.IsEnabled) _writeDelay.Start();
        };
        _writeDelay.Tick += async (_, _) => { _writeDelay.Stop(); await WriteBrightnessAsync(); };
        _poll.Tick += async (_, _) => { if (!_volumeDragging) SyncVolume(); await ReadBrightnessAsync(); };
        SyncVolume();
        _light.IsEnabled = false;
        _poll.Start();
        _ = ReadBrightnessAsync();
    }

    private void SyncVolume()
    {
        _syncing = true;
        try
        {
            var ready = VolumeHelper.TryRead(out var percent, out var muted);
            _volume.IsEnabled = _mute.IsEnabled = ready;
            _volumeText.Text = ready ? $"{percent}%" : "不可用";
            if (!_volumeDragging && ready) _volume.Value = percent;
            ((TextBlock)_speaker.Content).Text = muted || percent == 0 ? "\uE74F" : "\uE767";
            _speaker.ToolTip = muted ? "音量与亮度（已静音）" : "音量与亮度";
            _mute.Content = muted ? "取消静音" : "静音";
        }
        finally { _syncing = false; }
    }

    private async Task ReadBrightnessAsync()
    {
        if (_disposed || _reading || _writing || _lightDragging || _pendingBrightness != null) return;
        _reading = true;
        try
        {
            var state = await _brightness.ReadAsync();
            if (!_disposed && !_lightDragging && _pendingBrightness == null && !_writing) ApplyBrightness(state);
        }
        finally { _reading = false; }
    }

    private async Task WriteBrightnessAsync()
    {
        if (_disposed || _writing || _pendingBrightness is not { } value) return;
        _pendingBrightness = null;
        _writing = true;
        try
        {
            var state = await _brightness.SetAsync(value);
            if (!_disposed && _pendingBrightness == null) ApplyBrightness(state);
        }
        finally
        {
            _writing = false;
            if (!_disposed && _pendingBrightness != null) _writeDelay.Start();
        }
    }

    private void ApplyBrightness(BrightnessState state)
    {
        _syncing = true;
        try
        {
            _light.IsEnabled = state.Percent != null;
            _lightText.Text = state.Percent is { } percent ? $"{percent}%" : "不可用";
            if (state.Percent is { } value && !_lightDragging) _light.Value = value;
            _error.Text = state.Error ?? "";
            _error.Visibility = state.Error == null ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { _syncing = false; }
    }

    internal void Close() => _popup.IsOpen = false;

    public void Dispose()
    {
        _disposed = true;
        _poll.Stop();
        _writeDelay.Stop();
        Close();
    }

    private static void TrackDrag(Slider slider, Action<bool> set)
    {
        slider.PreviewMouseLeftButtonDown += (_, _) => set(true);
        slider.PreviewMouseLeftButtonUp += (_, _) => set(false);
        slider.LostMouseCapture += (_, _) => set(false);
    }

    private static Slider Slider(string name)
    {
        var slider = new Slider { Minimum = 0, Maximum = 100, Orientation = Orientation.Vertical,
            Height = 180, Width = 40, IsSnapToTickEnabled = true, TickFrequency = 1,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(slider, name);
        return slider;
    }

    private static TextBlock Label(string text) => new() { Text = text, Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center, FontSize = 14 };

    private static StackPanel Column(string name, TextBlock value, Slider slider)
    {
        var panel = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
        panel.Children.Add(Label(name));
        panel.Children.Add(value);
        panel.Children.Add(slider);
        return panel;
    }

    private static Button IconButton(string glyph, string name)
    {
        var button = new Button { Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 18, Foreground = Brushes.White }, ToolTip = name,
            Width = 34, Height = 34, Margin = new Thickness(2), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        return button;
    }
}
