using System.Windows;
using focus_desktop.Services;

namespace focus_desktop;

/// <summary>One-time external-app setup for users with an already configured installation.</summary>
public partial class AppSupplementWindow : Window
{
    public event Action<AppSettings>? Completed;

    private readonly AppSettings _settings;
    private bool _completed;

    public AppSupplementWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        AppPicker.IsReadyChanged += ready =>
        {
            SaveButton.IsEnabled = ready;
            SkipButton.IsEnabled = ready;
        };
        Loaded += async (_, _) => await AppPicker.InitializeAsync();
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Commit(AppPicker.GetSelectedEntries());

    private void Skip_Click(object sender, RoutedEventArgs e) => Commit(new List<ExternalAppEntry>());

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Commit(List<ExternalAppEntry> entries)
    {
        if (!AppPicker.IsReady)
        {
            SaveError.Text = "应用入口仍在扫描或解析，请等待完成后再确认。";
            SaveError.Visibility = Visibility.Visible;
            return;
        }

        var previousConfirmed = _settings.ExternalAppsConfirmed;
        var previousEntries = _settings.ExternalApps;
        _settings.ExternalAppsConfirmed = true;
        _settings.ExternalApps = entries;

        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            _settings.ExternalAppsConfirmed = previousConfirmed;
            _settings.ExternalApps = previousEntries;
            SaveError.Text = $"保存失败，原配置仍保留。请重试：{ex.Message}";
            SaveError.Visibility = Visibility.Visible;
            return;
        }

        _completed = true;
        Completed?.Invoke(_settings);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        AppPicker.CancelPendingOperations();
        if (!_completed)
        {
            try { Application.Current.Shutdown(); } catch { }
        }
        base.OnClosed(e);
    }
}
