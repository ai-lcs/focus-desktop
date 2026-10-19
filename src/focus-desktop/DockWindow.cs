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

    private readonly MainWindow _main;
    private readonly FocusModeService _focus;
    private readonly ExternalAppWindowService _windows;
    private readonly AppBarWindowService _appBar;
    private readonly TextBlock _status;
    private bool _isClosed;
    private int _registrationFailureQueued;

    public bool IsExternalWindowForeground => _windows.IsExternalWindowForeground;
    public bool IsClosed => _isClosed;
    public bool IsAppBarReleased => !_appBar.IsRegistered;
    internal bool TryGetAppBarLayout(out AppBarLayout layout)
    {
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
        KeyboardGuardService keyboard)
    {
        _main = main;
        _focus = focus;

        Title = "Focus Desk 应用 Dock";
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

        var root = new Grid { Margin = new Thickness(6, 10, 6, 8) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock
        {
            Text = "应用 Dock",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        });
        header.Children.Add(new TextBlock
        {
            Text = "已确认名单",
            FontSize = 10,
            Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(155, 165, 180)),
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        });

        _status = new TextBlock
        {
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(240, 190, 115)),
            Margin = new Thickness(0, 10, 0, 0),
            MinHeight = 18,
        };

        var returnButton = CreateButton("返回 Focus Desk", emphasized: true);
        returnButton.Margin = new Thickness(0, 8, 0, 0);
        returnButton.Click += (_, _) => ReturnToFocusDesk();
        header.Children.Add(returnButton);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var list = new StackPanel();
        foreach (var entry in entries.OrderBy(item => item.Order))
        {
            var button = CreateButton(entry.DisplayName, emphasized: false);
            button.Margin = new Thickness(0, 0, 0, 6);
            button.Click += async (_, _) => await ActivateEntryAsync(entry);
            list.Children.Add(button);
        }

        var scroll = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        Grid.SetRow(_status, 2);
        root.Children.Add(_status);
        Content = root;

        var windows = new ExternalAppWindowService(entries, keyboard);
        try
        {
            var appBar = new AppBarWindowService(this);
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

        Closed += (_, _) =>
        {
            _isClosed = true;
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
            Show();
            if (registerAppBar)
            {
                return _appBar.Register();
            }

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

    public bool ReleaseAppBar() => _appBar.Release();

    private async Task ActivateEntryAsync(ExternalAppEntry entry)
    {
        var intent = _windows.BeginIntent(entry.Id);
        _main.PrepareForExternalWindow();
        _status.Text = $"正在打开 {entry.DisplayName}…";
        var result = await _windows.ActivateAsync(entry, intent);
        if (!_windows.IsCurrentIntent(intent)) return;
        if (result.Activated)
        {
            _status.Text = "";
            return;
        }

        if (result.Superseded) return;
        _status.Text = result.Error ?? "无法切换到此应用。";
        _main.ReturnToFocusDesk();
    }

    private void ReturnToFocusDesk()
    {
        _windows.CancelRequests();
        _status.Text = "";
        _main.ReturnToFocusDesk();
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
        if (layout is { } current) _main.ApplyAppBarWorkArea(current.WorkArea);
    }

    private void OnActiveExternalWindowUnavailable(long revision, long intent)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isClosed || !_windows.IsCurrentUnavailable(revision, intent)) return;
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
            MaxHeight = 42,
        },
        Height = 54,
        HorizontalContentAlignment = HorizontalAlignment.Center,
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
