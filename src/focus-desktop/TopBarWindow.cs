using System.Windows;
using focus_desktop.Services;

namespace focus_desktop;

/// <summary>Hosts the existing controls; no duplicate tabs, browser or timer.</summary>
internal sealed class TopBarWindow : Window
{
    private readonly AppBarWindowService _appBar;
    private bool _restored;

    internal bool IsAppBarReleased => !_appBar.IsRegistered;
    internal AppBarLayout? Layout => _appBar.Layout;

    internal TopBarWindow(MainWindow main, bool fullScreen, Func<int> leftBoundary)
    {
        Title = "Focus Desk 顶部栏";
        WindowTransitions.Disable(this);
        Height = 44;
        Width = SystemParameters.PrimaryScreenWidth;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        UseLayoutRounding = true;
        Content = main.DetachHeader();
        _appBar = new AppBarWindowService(this, fullScreen, topEdge: true, leftBoundary: leftBoundary);
        _appBar.LayoutChanged += layout => { if (layout is { } current) main.ApplyAppBarWorkArea(current.WorkArea); };
        Closed += (_, _) =>
        {
            _appBar.Dispose();
            Content = null;
            main.RestoreHeader();
            _restored = true;
        };
    }

    internal bool TryShow(bool registerAppBar, Action<string> failure)
    {
        _appBar.RegistrationLost += failure;
        WindowTransitions.SetVisible(this, false);
        Show();
        if (registerAppBar)
        {
            var registered = _appBar.Register();
            if (registered) WindowTransitions.SetVisible(this, true);
            return registered;
        }
        Left = 96;
        Top = 0;
        Width = Math.Max(1, SystemParameters.PrimaryScreenWidth - 96);
        WindowTransitions.SetVisible(this, true);
        return true;
    }

    internal bool Release()
    {
        if (_restored) return true;
        WindowTransitions.SetVisible(this, false);
        if (!_appBar.Release()) return false;
        if (!_restored) Close();
        return _restored;
    }
}
