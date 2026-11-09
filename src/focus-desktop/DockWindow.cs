using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using focus_desktop.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace focus_desktop;

/// <summary>Small, non-owned launcher for the frozen external-app list.</summary>
internal sealed class DockWindow : Window
{
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly IntPtr HwndTopmost = new(-1);
    private TopBarWindow? _topBar;
    private readonly bool _fullScreen;

    private readonly MainWindow _main;
    private readonly FocusModeService _focus;
    private readonly ExternalAppWindowService _windows;
    private readonly AppBarWindowService _appBar;
    private readonly TextBlock _status;
    private readonly Dictionary<string, WpfButton> _entryButtons = new();
    private readonly WpfButton _returnButton;
    private readonly System.Windows.Threading.DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _isClosed;
    private int _registrationFailureQueued;

    public bool IsExternalWindowForeground => _windows.IsExternalWindowForeground;
    public bool IsClosed => _isClosed;
    public bool IsAppBarReleased => !_appBar.IsRegistered && (_topBar?.IsAppBarReleased ?? true);
    internal Task CaptureCurrentPlacementsAsync() => Task.Run(_windows.CaptureCurrentPlacements);
    internal bool TryGetAppBarLayout(out AppBarLayout layout)
    {
        if (_topBar?.Layout is { } topLayout)
        {
            layout = topLayout;
            return true;
        }
        if (_appBar.IsRegistered && _appBar.Layout is { } current)
        {
            layout = current;
            return true;
        }

        layout = default;
        return false;
    }

    public DockWindow(
        MainWindow main,
        FocusModeService focus,
        IReadOnlyList<ExternalAppEntry> entries,
        KeyboardGuardService keyboard,
        bool fullScreen)
    {
        _main = main;
        _focus = focus;
        _fullScreen = fullScreen;
        WindowTransitions.Disable(this);
        _main.PersistentNavigationRequested += ReturnToFocusDesk;

        Title = "Focus Desk 软件栏";
        Width = 96;
        Height = SystemParameters.PrimaryScreenHeight;
        MinWidth = 96;
        MaxWidth = 96;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0;
        Top = 0;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = new WpfSolidColorBrush(WpfColor.FromRgb(23, 27, 34));
        Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(237, 240, 246));
        UseLayoutRounding = true;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(6, 0, 6, 10) };
        var clock = new TextBlock
        {
            Text = DateTime.Now.ToString("HH:mm"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var clockRegion = new Border
        {
            Height = 44,
            Background = new WpfSolidColorBrush(WpfColor.FromRgb(38, 41, 46)),
            BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(58, 63, 70)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = clock,
        };
        root.Children.Add(clockRegion);

        _status = new TextBlock
        {
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(240, 190, 115)),
            Margin = new Thickness(0, 10, 0, 0),
            MinHeight = 18,
        };

        var returnButton = CreateButton("返回 Focus Desk", emphasized: true);
        _returnButton = returnButton;
        returnButton.Margin = new Thickness(0, 8, 0, 0);
        returnButton.Click += (_, _) => ReturnHome();
        header.Children.Add(returnButton);
        Grid.SetRow(header, 1);
        root.Children.Add(header);

        var list = new StackPanel { Margin = new Thickness(6, 0, 6, 0) };
        foreach (var entry in entries.OrderBy(item => item.Order))
        {
            var button = CreateButton(entry.DisplayName, emphasized: false);
            button.Margin = new Thickness(0, 0, 0, 6);
            button.Click += async (_, _) => await ActivateEntryAsync(entry);
            _entryButtons.Add(entry.Id, button);
            list.Children.Add(button);
        }

        var scroll = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        _status.Margin = new Thickness(6, 10, 6, 8);
        Grid.SetRow(_status, 3);
        root.Children.Add(_status);
        Content = root;

        var windows = new ExternalAppWindowService(entries, keyboard);
        try
        {
            var appBar = new AppBarWindowService(this, fullScreen);
            appBar.RegistrationLost += OnRegistrationLost;
            appBar.LayoutChanged += OnAppBarLayoutChanged;
            windows.ActiveExternalWindowUnavailable += OnActiveExternalWindowUnavailable;
            _windows = windows;
            _appBar = appBar;
        }
        catch
        {
            windows.Dispose();
            throw;
        }

        SelectEntry(null);
        _clock.Tick += (_, _) =>
        {
            clock.Text = DateTime.Now.ToString("HH:mm");
            if (_windows.ForegroundEntryId is { } id) SelectEntry(id);
        };
        _clock.Start();

        Closed += (_, _) =>
        {
            _isClosed = true;
            _clock.Stop();
            _main.PersistentNavigationRequested -= ReturnToFocusDesk;
            _topBar?.Release();
            _appBar.RegistrationLost -= OnRegistrationLost;
            _appBar.LayoutChanged -= OnAppBarLayoutChanged;
            _windows.ActiveExternalWindowUnavailable -= OnActiveExternalWindowUnavailable;
            _appBar.Dispose();
            _windows.Dispose();
        };
    }

    public bool TryShowDock(bool registerAppBar)
    {
        try
        {
            WindowTransitions.SetVisible(this, false);
            Show();
            if (registerAppBar)
            {
                if (!_appBar.Register()) return false;
                _topBar = new TopBarWindow(_main, _fullScreen, () => _appBar.Layout?.ApprovedBounds.Right ?? 0);
                var shown = _topBar.TryShow(true, OnRegistrationLost);
                if (TryGetAppBarLayout(out var layout)) _windows.Viewport = layout.WorkArea;
                if (shown) WindowTransitions.SetVisible(this, true);
                return shown;
            }

            _topBar = new TopBarWindow(_main, _fullScreen, () => 96);
            if (!_topBar.TryShow(false, OnRegistrationLost)) return false;
            WindowTransitions.SetVisible(this, true);
            Topmost = true;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0,
                    SwpNoActivate | SwpNoOwnerZOrder | SwpNoMove | SwpNoSize);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool ReleaseAppBar()
    {
        WindowTransitions.SetVisible(this, false);
        if (_topBar != null && !_topBar.Release()) return false;
        return _appBar.Release();
    }

    private async Task ActivateEntryAsync(ExternalAppEntry entry)
    {
        Activate();
        var intent = _windows.BeginIntent(entry.Id);
        _main.PrepareForExternalWindow();
        _status.Text = $"正在打开 {entry.DisplayName}…";
        var result = await _windows.ActivateAsync(entry, intent);
        if (!_windows.IsCurrentIntent(intent)) return;
        if (result.Activated)
        {
            SelectEntry(entry.Id);
            _status.Text = "";
            return;
        }

        if (result.Superseded) return;
        _status.Text = result.Error ?? "无法切换到此应用。";
        SelectEntry(null);
        _main.ReturnToFocusDesk();
    }

    private void ReturnToFocusDesk()
    {
        _windows.CancelRequests();
        _status.Text = "";
        SelectEntry(null);
        _main.ReturnToFocusDesk();
        Topmost = true;
        SetWindowPos(new WindowInteropHelper(this).Handle, HwndTopmost, 0, 0, 0, 0,
            SwpNoActivate | SwpNoOwnerZOrder | SwpNoMove | SwpNoSize);
    }

    private void ReturnHome()
    {
        ReturnToFocusDesk();
        _main.ShowHome();
    }

    private void SelectEntry(string? id)
    {
        foreach (var (entryId, button) in _entryButtons)
            ApplySelection(button, entryId == id);
        ApplySelection(_returnButton, id == null);
    }

    private static void ApplySelection(WpfButton button, bool selected)
    {
        button.Background = new WpfSolidColorBrush(selected
            ? WpfColor.FromRgb(59, 111, 196) : WpfColor.FromRgb(42, 49, 60));
        button.BorderBrush = new WpfSolidColorBrush(selected
            ? WpfColor.FromRgb(135, 181, 255) : WpfColor.FromRgb(67, 77, 92));
        System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? "当前使用" : "");
    }

    private void OnRegistrationLost(string reason)
    {
        if (Interlocked.Exchange(ref _registrationFailureQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isClosed) return;
            _focus.Recover();
            _main.ReturnToFocusDesk();
            _main.ReportFocusRecoveryError(reason);
        }));
    }

    private void OnAppBarLayoutChanged(AppBarLayout? layout)
    {
        if (layout is { } current)
        {
            var viewport = (_topBar?.Layout ?? current).WorkArea;
            _main.ApplyAppBarWorkArea(viewport);
            _windows.Viewport = viewport;
        }
    }

    private void OnActiveExternalWindowUnavailable(long revision, long intent)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isClosed || !_windows.IsCurrentUnavailable(revision, intent)) return;
            SelectEntry(null);
            _main.ReturnToFocusDesk();
        }));
    }

    private static WpfButton CreateButton(string text, bool emphasized) => new()
    {
        Content = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
        MinHeight = 54,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Padding = new Thickness(3, 2, 3, 2),
        Background = new WpfSolidColorBrush(emphasized
            ? WpfColor.FromRgb(59, 111, 196)
            : WpfColor.FromRgb(42, 49, 60)),
        Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(243, 246, 251)),
        BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(67, 77, 92)),
        BorderThickness = new Thickness(1),
        FontSize = 14,
        FontWeight = emphasized ? FontWeights.SemiBold : FontWeights.Normal,
        Cursor = System.Windows.Input.Cursors.Hand,
        ToolTip = text,
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y,
        int width, int height, uint flags);
}
