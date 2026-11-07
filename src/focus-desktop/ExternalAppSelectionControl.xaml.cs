using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using focus_desktop.Services;
using DrawingIcon = System.Drawing.Icon;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfImage = System.Windows.Controls.Image;
using WpfImageSource = System.Windows.Media.ImageSource;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfUserControl = System.Windows.Controls.UserControl;
using WpfWindow = System.Windows.Window;
using WpfColor = System.Windows.Media.Color;
using WpfBrushes = System.Windows.Media.Brushes;

namespace focus_desktop;

public partial class ExternalAppSelectionControl : WpfUserControl
{
    private readonly List<Choice> _choices = new();
    private bool _initialized;
    private bool _scanCompleted;
    private bool _browseInProgress;
    private bool _isActive = true;

    public bool IsReady => _isActive && _scanCompleted && !_browseInProgress;
    public event Action<bool>? IsReadyChanged;

    public ExternalAppSelectionControl()
    {
        InitializeComponent();
        CountText.Text = "正在扫描桌面快捷方式…";
    }

    public async Task InitializeAsync()
    {
        if (_initialized || !_isActive) return;
        _initialized = true;
        try
        {
            var scan = await ExternalAppCatalog.ScanDesktopAsync();
            if (!_isActive) return;
            foreach (var entry in scan.Entries) AddEntry(entry, out _);
            ShowErrors(scan.Errors.Select(error => $"{Path.GetFileName(error.Path)}：{error.Message}"));
            UpdateCount();
        }
        catch (Exception ex)
        {
            if (!_isActive) return;
            CountText.Text = "桌面快捷方式扫描失败；仍可浏览其他位置。";
            ShowErrors(new[] { ex.Message });
        }
        finally
        {
            _scanCompleted = true;
            if (_isActive)
            {
                BrowseButton.IsEnabled = true;
                IsReadyChanged?.Invoke(IsReady);
            }
        }
    }

    public void CancelPendingOperations()
    {
        _isActive = false;
        BrowseButton.IsEnabled = false;
    }

    public List<ExternalAppEntry> GetSelectedEntries()
    {
        var selected = _choices.Where(choice => choice.IsSelected).ToList();
        return selected.Select((choice, order) => CopyWithOrder(choice.Entry, order)).ToList();
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (!_isActive || !_scanCompleted || _browseInProgress) return;

        var dialog = new WpfOpenFileDialog
        {
            Title = "选择应用快捷方式或程序",
            Filter = "应用快捷方式或程序 (*.lnk;*.exe)|*.lnk;*.exe|快捷方式 (*.lnk)|*.lnk|应用程序 (*.exe)|*.exe",
            Multiselect = true,
            CheckFileExists = true,
        };
        var owner = WpfWindow.GetWindow(this);
        if (owner == null || dialog.ShowDialog(owner) != true) return;

        _browseInProgress = true;
        BrowseButton.IsEnabled = false;
        CountText.Text = "正在解析所选应用入口，请稍候…";
        IsReadyChanged?.Invoke(IsReady);
        try
        {
            var errors = new List<string>();
            foreach (var path in dialog.FileNames)
            {
                if (!_isActive) return;
                try
                {
                    var entry = await ExternalAppCatalog.ResolveAsync(path);
                    if (!_isActive) return;
                    if (!AddEntry(entry, out var identityConflict))
                        errors.Add(identityConflict ?? $"{Path.GetFileName(path)}：这个入口已在列表中。");
                    else if (identityConflict != null)
                        errors.Add(identityConflict);
                }
                catch (Exception ex)
                {
                    if (!_isActive) return;
                    errors.Add($"{Path.GetFileName(path)}：{ex.Message}");
                }
            }

            if (!_isActive) return;
            ShowErrors(errors);
            UpdateCount();
        }
        finally
        {
            _browseInProgress = false;
            if (_isActive)
            {
                BrowseButton.IsEnabled = _scanCompleted;
                IsReadyChanged?.Invoke(IsReady);
            }
        }
    }

    private bool AddEntry(ExternalAppEntry entry, out string? identityConflict)
    {
        var sameDefinition = _choices.FirstOrDefault(choice =>
            string.Equals(choice.Entry.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
        if (sameDefinition != null)
        {
            identityConflict = ExternalAppEntry.DescribeWindowMatchIdentityConflict(sameDefinition.Entry, entry);
            return false;
        }

        var existingIdentity = ExternalAppEntry.FindWindowMatchIdentityConflict(
            _choices.Select(choice => choice.Entry), entry);
        identityConflict = existingIdentity == null
            ? null
            : ExternalAppEntry.DescribeWindowMatchIdentityConflict(existingIdentity, entry);

        var choice = new Choice(entry);
        _choices.Add(choice);
        RenderChoice(choice);
        _ = LoadIconAsync(choice);
        return true;
    }

    private void RenderChoice(Choice choice)
    {
        var row = new Border
        {
            Background = WpfBrushes.Transparent,
            BorderBrush = new SolidColorBrush(WpfColor.FromRgb(61, 66, 75)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 7, 6, 7),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = new WpfCheckBox
        {
            IsChecked = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 10, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(check, $"ExternalApp_{choice.Entry.Id}");
        check.Checked += (_, _) =>
        {
            var selectedConflict = ExternalAppEntry.FindWindowMatchIdentityConflict(
                _choices.Where(other => other.IsSelected).Select(other => other.Entry), choice.Entry);
            if (selectedConflict != null)
            {
                choice.IsSelected = false;
                check.IsChecked = false;
                ShowErrors(new[] { ExternalAppEntry.DescribeWindowMatchIdentityConflict(selectedConflict, choice.Entry) });
                return;
            }
            choice.IsSelected = true;
            UpdateCount();
        };
        check.Unchecked += (_, _) => { choice.IsSelected = false; UpdateCount(); };

        var icon = new WpfImage
        {
            Width = 28,
            Height = 28,
            Stretch = Stretch.Uniform,
            Source = CreateFallbackIcon(),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(icon, 1);

        var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        details.Children.Add(new TextBlock
        {
            Text = choice.Entry.DisplayName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = WpfBrushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var identity = choice.Entry.AppUserModelId ?? choice.Entry.ExecutablePath ?? choice.Entry.ShortcutPath ?? "";
        var kindLabel = choice.Entry.Kind == ExternalAppEntry.PackagedAppKind ? "打包应用" : "桌面应用";
        var launchArguments = string.IsNullOrWhiteSpace(choice.Entry.Arguments) ? "默认" : choice.Entry.Arguments;
        var workingDirectory = choice.Entry.Kind == ExternalAppEntry.PackagedAppKind
            ? "打包应用默认"
            : string.IsNullOrWhiteSpace(choice.Entry.WorkingDirectory)
                ? Path.GetDirectoryName(choice.Entry.ExecutablePath ?? "") ?? "未指定"
                : choice.Entry.WorkingDirectory;
        var detailText = new TextBlock
        {
            Text = $"{kindLabel} · 参数：{launchArguments}",
            FontSize = 11,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(154, 160, 166)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        details.Children.Add(detailText);
        details.ToolTip = $"身份：{identity}{Environment.NewLine}启动参数：{launchArguments}{Environment.NewLine}工作目录：{workingDirectory}";
        Grid.SetColumn(details, 2);

        var remove = new WpfButton
        {
            Content = "移除",
            Padding = new Thickness(8, 4, 8, 4),
            Background = WpfBrushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(WpfColor.FromRgb(154, 160, 166)),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "从本次候选列表移除",
        };
        remove.Click += (_, _) =>
        {
            _choices.Remove(choice);
            EntriesPanel.Children.Remove(row);
            UpdateCount();
        };
        Grid.SetColumn(remove, 3);

        grid.Children.Add(check);
        grid.Children.Add(icon);
        grid.Children.Add(details);
        grid.Children.Add(remove);
        row.Child = grid;
        EntriesPanel.Children.Add(row);
        choice.IconControl = icon;
    }

    private static async Task LoadIconAsync(Choice choice)
    {
        var path = choice.Entry.ExecutablePath ?? choice.Entry.ShortcutPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            var source = await Task.Run(() => ExtractIcon(path));
            var iconControl = choice.IconControl;
            if (source != null && iconControl != null)
                await iconControl.Dispatcher.InvokeAsync(() => iconControl.Source = source);
        }
        catch { /* Missing shell icons keep the neutral placeholder. */ }
    }

    private static BitmapSource? ExtractIcon(string path)
    {
        using var icon = DrawingIcon.ExtractAssociatedIcon(path);
        if (icon == null) return null;
        var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
        source.Freeze();
        return source;
    }

    private static WpfImageSource CreateFallbackIcon()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(
            new SolidColorBrush(WpfColor.FromRgb(76, 84, 97)),
            null,
            new RectangleGeometry(new Rect(2, 2, 28, 28), 6, 6)));
        drawing.Children.Add(new GeometryDrawing(
            new SolidColorBrush(WpfColor.FromRgb(209, 215, 225)),
            null,
            Geometry.Parse("M9,9 L23,9 L23,23 L9,23 Z M12,6 L26,6 L26,20")));
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private void UpdateCount()
    {
        var selected = _choices.Count(choice => choice.IsSelected);
        CountText.Text = $"已选择 {selected} 个应用 · { _choices.Count } 个候选入口";
    }

    private void ShowErrors(IEnumerable<string> errors)
    {
        var messages = errors.Where(message => !string.IsNullOrWhiteSpace(message)).ToList();
        if (messages.Count == 0)
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
            ErrorText.Text = "";
            return;
        }

        ErrorText.Text = string.Join(Environment.NewLine, messages.Take(4));
        ErrorText.ToolTip = string.Join(Environment.NewLine, messages);
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private static ExternalAppEntry CopyWithOrder(ExternalAppEntry entry, int order) => new()
    {
        Id = entry.Id,
        DisplayName = entry.DisplayName,
        Kind = entry.Kind,
        ShortcutPath = entry.ShortcutPath,
        ExecutablePath = entry.ExecutablePath,
        Arguments = entry.Arguments,
        WorkingDirectory = entry.WorkingDirectory,
        AppUserModelId = entry.AppUserModelId,
        Order = order,
    };

    private sealed class Choice(ExternalAppEntry entry)
    {
        public ExternalAppEntry Entry { get; } = entry;
        public bool IsSelected { get; set; }
        public WpfImage? IconControl { get; set; }
    }
}
