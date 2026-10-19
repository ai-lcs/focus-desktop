using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace FocusDesk.DockTestFixture;

internal sealed class FixtureWindow : Window
{
    private readonly FixtureOptions _options;
    private readonly int _windowIndex;
    private readonly TextBox _draftBox;
    private bool _isDirty = true;

    public FixtureWindow(FixtureOptions options, int windowIndex)
    {
        _options = options;
        _windowIndex = windowIndex;
        var process = Process.GetCurrentProcess();
        var executable = process.MainModule?.FileName ?? Environment.ProcessPath ?? "(unknown)";
        var title = options.IsChild ? "Dock 测试子进程" : $"Dock 测试应用 {windowIndex}";

        Title = title;
        Width = 760;
        Height = 540;
        MinWidth = 540;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (options.StartMinimized)
        {
            ShowActivated = false;
            WindowState = WindowState.Minimized;
        }
        Background = new SolidColorBrush(Color.FromRgb(25, 31, 42));
        Foreground = Brushes.White;

        _draftBox = new TextBox
        {
            Name = "FixtureDraftTextBox",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 180,
            Margin = new Thickness(0, 8, 0, 12),
            Text = "这是可丢弃的 Dock 测试草稿。可在此输入中文和空格。"
        };
        System.Windows.Automation.AutomationProperties.SetName(_draftBox, "可丢弃的未保存测试文本");

        var reportFile = Path.Combine(_options.ReportDirectory,
            $"fixture-{process.Id}-window-{windowIndex}.json");
        var saveFile = Path.Combine(_options.ReportDirectory,
            $"saved-{process.Id}-window-{windowIndex}.txt");
        var report = new FixtureReport(
            process.Id,
            executable,
            Environment.CurrentDirectory,
            _options.OwnerProcessId,
            Environment.GetCommandLineArgs(),
            title,
            DateTimeOffset.UtcNow);
        File.WriteAllText(reportFile, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        });
        root.Children.Add(new TextBlock
        {
            Text = $"PID: {process.Id}\n程序: {executable}\n工作目录: {Environment.CurrentDirectory}\n" +
                   $"报告: {reportFile}\n保存草稿: {saveFile}\n父 PID: {_options.OwnerProcessId?.ToString() ?? "(无)"}",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(184, 198, 218))
        });
        root.Children.Add(_draftBox);
        root.Children.Add(new TextBlock
        {
            Text = "输入内容后按 Alt+F4 或点关闭按钮，会出现保存/放弃/继续编辑对话框。",
            Foreground = new SolidColorBrush(Color.FromRgb(184, 198, 218)),
            Margin = new Thickness(0, 0, 0, 12)
        });
        root.Children.Add(new TextBlock
        {
            Text = "关闭当前主窗口不会主动结束另一个 Dock 测试子进程。",
            Foreground = new SolidColorBrush(Color.FromRgb(184, 198, 218))
        });

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = root
        };
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isDirty)
            return;

        e.Cancel = true;
        var dialog = new SavePromptWindow { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        if (dialog.Choice == SaveChoice.Save)
        {
            var file = Path.Combine(_options.ReportDirectory,
                $"saved-{Process.GetCurrentProcess().Id}-window-{_windowIndex}.txt");
            File.WriteAllText(file, _draftBox.Text);
        }

        _isDirty = false;
        Dispatcher.BeginInvoke(new Action(Close));
    }
}

internal sealed record FixtureReport(
    int ProcessId,
    string ExecutablePath,
    string CurrentDirectory,
    int? OwnerProcessId,
    string[] Arguments,
    string WindowTitle,
    DateTimeOffset StartedAtUtc);

internal enum SaveChoice
{
    Save,
    Discard
}

internal sealed class SavePromptWindow : Window
{
    public SaveChoice Choice { get; private set; } = SaveChoice.Discard;

    public SavePromptWindow()
    {
        Title = "保存 Dock 测试草稿？";
        Width = 390;
        Height = 180;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        AutomationProperties.SetName(this, "保存测试草稿");

        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock
        {
            Text = "草稿尚未保存。要保存到夹具输出目录吗？",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18)
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        var save = new Button { Content = "保存", Width = 82, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        AutomationProperties.SetAutomationId(save, "FixtureSaveButton");
        save.Click += (_, _) => { Choice = SaveChoice.Save; DialogResult = true; };

        var discard = new Button { Content = "放弃", Width = 82, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetAutomationId(discard, "FixtureDiscardButton");
        discard.Click += (_, _) => { Choice = SaveChoice.Discard; DialogResult = true; };

        var stay = new Button { Content = "继续编辑", Width = 92, IsCancel = true };
        AutomationProperties.SetAutomationId(stay, "FixtureStayButton");
        stay.Click += (_, _) => DialogResult = false;

        buttons.Children.Add(save);
        buttons.Children.Add(discard);
        buttons.Children.Add(stay);
        content.Children.Add(buttons);
        Content = content;
    }
}
