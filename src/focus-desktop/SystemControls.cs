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
    private readonly TextBlock _volumeLabel = Label("音量");
    private readonly TextBlock _lightLabel = Label("亮度");
    private readonly TextBlock _error = Label("正在读取内屏亮度…");
    private readonly Button _speaker;
    private readonly Border _panel;
    private readonly Grid _grid;
    private readonly Border _inlineHost = new() { Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _writeDelay = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private bool _syncing;
    private bool _volumeDragging;
    private bool _lightDragging;
    private bool _reading;
    private bool _writing;
    private bool _disposed;
    private int? _pendingBrightness;
    private bool _atDock;

    internal StackPanel Host { get; } = new();
    internal StackPanel Launchers { get; } = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    internal bool AtDock
    {
        get => _atDock;
        set
        {
            Close();
            _popup.Child = null;
            _inlineHost.Child = null;
            _atDock = value;
            _panel.Width = value ? double.NaN : 224;
            _grid.Margin = new Thickness(value ? 4 : 16);
            _panel.BorderThickness = new Thickness(value ? 0 : 1);
            _error.MaxWidth = value ? 80 : 192;
            foreach (var label in new[] { _volumeLabel, _lightLabel, _volumeText, _lightText })
                label.FontSize = value ? 11 : 14;
            if (value) _inlineHost.Child = _panel;
            else _popup.Child = _panel;
        }
    }

    internal SystemControls()
    {
        VolumeHelper.Init();
        _speaker = IconButton("\uE767", "静音");
        var sun = IconButton("\uE706", "展开或收起音量与亮度");
        Launchers.Children.Add(_speaker);
        Launchers.Children.Add(sun);
        Host.Children.Add(_inlineHost);
        Host.Children.Add(Launchers);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_speaker, "SystemMute");
        _speaker.Click += (_, _) => { VolumeHelper.ToggleMute(); SyncVolume(); };

        var grid = new Grid { Margin = new Thickness(16), Focusable = true };
        _grid = grid;
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var audio = Column(_volumeLabel, _volumeText, _volume);
        var light = Column(_lightLabel, _lightText, _light);
        grid.Children.Add(audio);
        Grid.SetColumn(light, 1);
        grid.Children.Add(light);
        _error.FontSize = 11;
        _error.TextWrapping = TextWrapping.Wrap;
        _error.MaxWidth = 192;
        _error.MaxHeight = 60;
        _error.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(_error, 1);
        Grid.SetColumnSpan(_error, 2);
        grid.Children.Add(_error);
        _panel = new Border { Background = new SolidColorBrush(Color.FromRgb(37, 42, 51)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(95, 107, 126)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Child = grid, Width = 224 };
        _popup = new Popup
        {
            StaysOpen = false, AllowsTransparency = true,
            Child = _panel,
        };
        grid.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        sun.Click += (_, _) =>
        {
            if (AtDock)
            {
                _inlineHost.Visibility = _inlineHost.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                if (_inlineHost.Visibility == Visibility.Visible) grid.Focus();
            }
            else
            {
                _popup.PlacementTarget = sun;
                _popup.Placement = PlacementMode.Bottom;
                _popup.IsOpen = !_popup.IsOpen;
                if (_popup.IsOpen) grid.Focus();
            }
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
            _volume.IsEnabled = _speaker.IsEnabled = ready;
            _volumeText.Text = ready ? $"{percent}%" : "不可用";
            if (!_volumeDragging && ready) _volume.Value = percent;
            ((TextBlock)_speaker.Content).Text = muted || percent == 0 ? "\uE74F" : "\uE767";
            _speaker.ToolTip = muted ? "取消静音" : "静音";
            System.Windows.Automation.AutomationProperties.SetName(_speaker, muted ? "取消静音" : "静音");
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
            _error.ToolTip = state.Error;
            _error.Visibility = state.Error == null ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { _syncing = false; }
    }

    internal void Close()
    {
        _popup.IsOpen = false;
        _inlineHost.Visibility = Visibility.Collapsed;
    }

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
            Height = 180, Width = 32, IsSnapToTickEnabled = true, TickFrequency = 1,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(slider, name);
        return slider;
    }

    private static TextBlock Label(string text) => new() { Text = text, Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center, FontSize = 14 };

    private static StackPanel Column(TextBlock name, TextBlock value, Slider slider)
    {
        var panel = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
        panel.Children.Add(name);
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
