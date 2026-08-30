using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using focus_desktop.Services;
using Forms = System.Windows.Forms;
using WpfButton = System.Windows.Controls.Button;

namespace focus_desktop;

public partial class MainWindow : Window
{
    private readonly AppOptions _options;
    private readonly FocusModeService _focus;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // ---- 计时器（Focuser pomodoro.rs 模型：时间戳 + 冻结剩余） ----
    private DateTime _phaseStart;
    private TimeSpan _elapsedWhenPaused;
    private bool _timerRunning;
    private bool _countdownMode;
    private int _countdownMinutes = 25;

    // ---- Web 层 ----
    private WebTabService? _web;
    private readonly Dictionary<string, WpfButton> _tabButtons = new();
    private System.Windows.Forms.Panel? _hostPanel;

    // ---- 文件浏览 ----
    private string _filesRoot;
    private string _currentDir;

    private bool _recoveryExitDone;

    public MainWindow(AppOptions options, FocusModeService focus)
    {
        _options = options;
        _focus = focus;
        InitializeComponent();

        _filesRoot = AppSettings.LoadOrDefault().StudyFolder;
        _currentDir = _filesRoot;

        // 铺满主屏（单显示器项目，按用户决策不做多屏）
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        // 预览模式：普通可切换窗口（不置顶、不覆盖全屏体验），给用户调配置用
        if (options.Preview)
        {
            Topmost = false;
            Width = Math.Min(1280, SystemParameters.PrimaryScreenWidth - 80);
            Height = Math.Min(800, SystemParameters.PrimaryScreenHeight - 80);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        else
        {
            Topmost = true;
        }

        if (options.Dev || options.Preview)
        {
            DevBadge.Visibility = Visibility.Visible;
            DevBadgeText.Text = options.Preview ? "预览" : "DEV";
        }

        _clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
        _clock.Start();
        ClockText.Text = DateTime.Now.ToString("HH:mm:ss");

        _timer.Tick += (_, _) =>
        {
            if (_timerRunning)
            {
                var elapsed = _elapsedWhenPaused + (DateTime.Now - _phaseStart);
                if (_countdownMode)
                {
                    var remain = TimeSpan.FromMinutes(_countdownMinutes) - elapsed;
                    TimerBig.Text = remain > TimeSpan.Zero ? remain.ToString(@"hh\:mm\:ss") : "00:00:00";
                    if (remain <= TimeSpan.Zero)
                    {
                        _timerRunning = false;
                        _timer.Stop();
                        TimerHint.Text = "倒计时结束 · 空格重新开始";
                    }
                }
                else
                {
                    TimerBig.Text = elapsed.ToString(@"hh\:mm\:ss");
                }
            }
        };

        Loaded += async (_, _) => await InitAsync();
        SourceInitialized += (_, _) => { };
        Closing += (_, e) =>
        {
            if (!_recoveryExitDone)
            {
                try { _focus.Exit(); } catch { }
                _recoveryExitDone = true;
            }
        };
        Closed += (_, _) => { try { _web?.Dispose(); } catch { } };

        // 声音：Core Audio 简版（IAudioEndpointVolume）
        VolumeHelper.Init();
        VolumeSlider.Value = VolumeHelper.Get();
    }

    private async Task InitAsync()
    {
        if (_options.Smoke)
        {
            // smoke 模式也初始化 Web 层（验证 WebView2 环境创建 + 四站 Tab 不炸），
            // 但停留时间拉长让页面加载完成
            try
            {
                _web = new WebTabService();
                await _web.EnsureEnvironmentAsync();
                App.SmokeLog("smoke: web environment created");
                WebTabService.Blocked += host => App.SmokeLog($"smoke: blocked {host}");

                _hostPanel = new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Fill };
                WfHost.Child = _hostPanel;

                await _web.CreateTabAsync("bili", "Bilibili", "https://www.bilibili.com", _hostPanel);
                await _web.CreateTabAsync("chatgpt", "ChatGPT", "https://chatgpt.com", _hostPanel);
                await _web.CreateTabAsync("gemini", "Gemini", "https://aistudio.google.com", _hostPanel);
                await _web.CreateTabAsync("deepseek", "DeepSeek", "https://chat.deepseek.com", _hostPanel);
                App.SmokeLog("smoke: 4 web tabs created");

                BuildTabBar();
            }
            catch (Exception ex)
            {
                App.SmokeLog($"smoke: web init failed: {ex.Message}");
                CrashReporter.Write(ex, "smoke-web-init");
            }

            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            t.Tick += (_, _) => { t.Stop(); Application.Current.Shutdown(); };
            t.Start();
            return;
        }

        try
        {
            _web = new WebTabService();
            await _web.EnsureEnvironmentAsync();
            WebTabService.Blocked += host => Dispatcher.Invoke(() => ShowBlocked(host));
            WebTabService.TitleChanged += (id, title) => Dispatcher.Invoke(() =>
            {
                if (_tabButtons.TryGetValue(id, out var btn))
                    btn.Content = title.Length > 18 ? title[..18] : title;
            });

            // 所有 WebView2 控件挂到同一个 WinForms Panel 上，Tab 切换 = 显隐控件
            _hostPanel = new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Fill };
            WfHost.Child = _hostPanel;

            await _web.CreateTabAsync("bili", "Bilibili", "https://www.bilibili.com", _hostPanel);
            await _web.CreateTabAsync("chatgpt", "ChatGPT", "https://chatgpt.com", _hostPanel);
            await _web.CreateTabAsync("gemini", "Gemini", "https://aistudio.google.com", _hostPanel);
            await _web.CreateTabAsync("deepseek", "DeepSeek", "https://chat.deepseek.com", _hostPanel);
            await _web.CreateTabAsync("pdf", "PDF", "", _hostPanel);

            BuildTabBar();
        }
        catch (Exception ex)
        {
            App.SmokeLog($"web init failed: {ex.Message}");
            CrashReporter.Write(ex, "web-init");
        }

        // 首次设置模式：config 不存在 → 进入设置（学习目录+登录+退出语）
        if (!AppSettings.Exists())
        {
            ShowSetupHint();
        }
    }

    // ---------------- Tab 条 ----------------

    private void BuildTabBar()
    {
        TabBar.Children.Clear();
        _tabButtons.Clear();
        foreach (var t in _web!.Tabs)
        {
            var btn = new Button
            {
                Content = t.Title,
                FontSize = 13,
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Background = (Brush)FindResource("PanelBrush"),
                Foreground = (Brush)FindResource("MutedBrush"),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            var id = t.Id;
            btn.Click += (_, _) => ActivateTab(id);
            _tabButtons[id] = btn;
            TabBar.Children.Add(btn);
        }
        ActivateTab("home");
    }

    private void ActivateTab(string id)
    {
        HomeView.Visibility = id == "home" ? Visibility.Visible : Visibility.Collapsed;
        FilesView.Visibility = id == "files" ? Visibility.Visible : Visibility.Collapsed;
        WebHost.Visibility = id is "bili" or "chatgpt" or "gemini" or "deepseek" or "pdf" ? Visibility.Visible : Visibility.Collapsed;

        _web?.Activate(id);

        foreach (var (tid, btn) in _tabButtons)
        {
            var active = tid == id;
            btn.Background = active ? (Brush)FindResource("BgBrush") : (Brush)FindResource("PanelBrush");
            btn.Foreground = active ? (Brush)FindResource("FgBrush") : (Brush)FindResource("MutedBrush");
        }
    }

    // ---------------- 导航 ----------------

    private void Nav_Files_Click(object sender, RoutedEventArgs e)
    {
        ActivateTab("files");
        RenderFiles(); // 进入即渲染（旧版漏了这行 → 首次进入永远空白）
    }
    private void Nav_Bili_Click(object sender, RoutedEventArgs e) => ActivateTab("bili");
    private void Nav_ChatGPT_Click(object sender, RoutedEventArgs e) => ActivateTab("chatgpt");
    private void Nav_Gemini_Click(object sender, RoutedEventArgs e) => ActivateTab("gemini");
    private void Nav_DeepSeek_Click(object sender, RoutedEventArgs e) => ActivateTab("deepseek");

    // ---------------- 文件浏览 ----------------

    private void Files_Root_Click(object sender, RoutedEventArgs e) { _currentDir = _filesRoot; RenderFiles(); }
    private void Files_Back_Click(object sender, RoutedEventArgs e)
    {
        var parent = Path.GetDirectoryName(_currentDir);
        if (parent != null && IsUnderRoot(parent)) { _currentDir = parent; RenderFiles(); }
    }

    /// <summary>选择学习文件夹（系统目录选择对话框 → 写入 config.json 立即生效）。</summary>
    private void Files_Choose_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择学习文件夹（focus-desktop 只浏览这个目录）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        // 初始定位：当前配置目录存在则从它开始，否则从 D:\ 开始
        try
        {
            if (Directory.Exists(_filesRoot))
            {
                dlg.InitialDirectory = _filesRoot;
                dlg.SelectedPath = _filesRoot;
            }
            else
            {
                dlg.InitialDirectory = @"D:\";
                dlg.SelectedPath = @"D:\";
            }
        }
        catch { }

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK
            && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
        {
            // 写入配置 + 立即刷新文件页
            var cfg = AppSettings.LoadOrDefault();
            cfg.StudyFolder = dlg.SelectedPath;
            cfg.Save();
            _filesRoot = dlg.SelectedPath;
            _currentDir = dlg.SelectedPath;
            ShowBlocked($"学习文件夹已设为：{dlg.SelectedPath}");
            RenderFiles();
        }
    }

    private bool IsUnderRoot(string dir)
    {
        var root = Path.GetFullPath(_filesRoot).TrimEnd('\\', '/');
        var d = Path.GetFullPath(dir).TrimEnd('\\', '/');
        return d == root || d.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RenderFiles();

    private void RenderFiles()
    {
        FileList.Items.Clear();
        FilesPath.Text = _currentDir; // 顶栏显示当前目录
        if (!Directory.Exists(_filesRoot))
        {
            FileList.Items.Add(new TextBlock
            {
                Text = $"学习目录不存在：{_filesRoot}\n请点右上「选择文件夹」重新指定。",
                Foreground = (Brush)FindResource("DangerBrush"),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        var keyword = SearchBox.Text.Trim();
        try
        {
            var dirs = Directory.EnumerateDirectories(_currentDir);
            var files = Directory.EnumerateFiles(_currentDir);
            if (keyword.Length > 0)
            {
                dirs = dirs.Where(d => Path.GetFileName(d).Contains(keyword, StringComparison.OrdinalIgnoreCase));
                files = files.Where(f => Path.GetFileName(f).Contains(keyword, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var d in dirs.OrderBy(d => Path.GetFileName(d)))
            {
                var item = MakeFileItem("📁", Path.GetFileName(d), () => { _currentDir = d; RenderFiles(); });
                FileList.Items.Add(item);
            }
            foreach (var f in files.OrderBy(f => Path.GetFileName(f)))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                var icon = ext switch
                {
                    ".pdf" => "📕",
                    ".doc" or ".docx" or ".wps" => "📘",
                    ".ppt" or ".pptx" => "📙",
                    ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" => "🖼️",
                    ".txt" or ".md" => "📄",
                    _ => "📄",
                };
                var path = f;
                var item = MakeFileItem(icon, Path.GetFileName(f), () => OpenFile(path));
                FileList.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            FileList.Items.Add(new TextBlock { Text = $"无法读取目录：{ex.Message}", Foreground = (Brush)FindResource("DangerBrush") });
        }
    }

    private FrameworkElement MakeFileItem(string icon, string name, Action open)
    {
        var btn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = icon, FontSize = 18, Margin = new Thickness(0, 0, 10, 0) },
                    new TextBlock { Text = name, FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                }
            },
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(10, 8, 10, 8),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        btn.Click += (_, _) => open();
        return btn;
    }

    private void OpenFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".pdf" || ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" || ext is ".txt" or ".md")
        {
            // 内置打开：WebView2 file:/// 导航（PDF 走内置查看器）
            var tab = _web?.Tabs.FirstOrDefault(t => t.Id == "pdf");
            if (tab != null)
            {
                tab.View.CoreWebView2.Navigate(new Uri(path).AbsoluteUri);
                ActivateTab("pdf");
            }
        }
        // 其他格式 V1 不支持（spec §5）：docx/pptx 提示
        else
        {
            ShowBlocked($"V1 内置支持 PDF/图片/TXT，暂不支持 {ext}。请先转 PDF 放入学习目录。");
        }
    }

    // ---------------- 音量 ----------------

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volumeReady) VolumeHelper.Set((int)e.NewValue);
    }

    private bool _volumeReady;

    // ---------------- 退出验证（Step 6） ----------------

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        // 预览模式/开发模式：直接退出（给用户调整配置用，无摩擦）
        if (_options.Dev || _options.Preview)
        {
            _focus.Exit();
            Application.Current.Shutdown();
            return;
        }
        // 正式模式：独立置顶窗口验证退出语
        // （旧版是主窗口内嵌 Grid 弹窗：Grid.Row 缺失被压进顶栏 + airspace 被网页盖住 →
        //  2026-08-30 用户"退不出来"事故根因，改为独立 Window 一并解决两个问题）
        var cfg = AppSettings.LoadOrDefault();
        var dlg = new ExitWindow(cfg.ExitPhrase) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _focus.Exit();
            Application.Current.Shutdown();
        }
    }

    // ---------------- 键盘：空格计时 / Esc ----------------

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == System.Windows.Input.Key.Space && HomeView.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            ToggleTimer();
        }
        else if (e.Key == System.Windows.Input.Key.R && HomeView.Visibility == Visibility.Visible
                 && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control
                 && _options.Dev)
        {
            e.Handled = true;
            ResetTimer();
        }
    }

    private void ToggleTimer()
    {
        if (_timerRunning)
        {
            _elapsedWhenPaused += DateTime.Now - _phaseStart;
            _timerRunning = false;
            TimerHint.Text = "已暂停 · 空格继续";
        }
        else
        {
            _phaseStart = DateTime.Now;
            _timerRunning = true;
            _timer.Start();
            TimerHint.Text = "计时中 · 空格暂停";
        }
    }

    private void ResetTimer()
    {
        _timerRunning = false;
        _elapsedWhenPaused = TimeSpan.Zero;
        TimerBig.Text = "00:00:00";
        TimerHint.Text = "已重置 · 空格开始";
    }

    // ---------------- 其他 ----------------

    private void ShowBlocked(string message)
    {
        BlockedText.Text = message;
        BlockedBar.Visibility = Visibility.Visible;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        t.Tick += (_, _) => { t.Stop(); BlockedBar.Visibility = Visibility.Collapsed; };
        t.Start();
    }

    private void ShowSetupHint()
    {
        // 首次运行（Setup 模式，未锁定）：常驻横幅 + 开始专注按钮
        var banner = new Border
        {
            Background = (Brush)FindResource("AccentSoft"),
            BorderBrush = (Brush)FindResource("AccentBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20, 12, 20, 12),
            Margin = new Thickness(0, 0, 0, 24),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock
        {
            Text = "首次设置：请到各标签页登录网站（登录态会保存）。准备好后 →",
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("FgBrush"),
        });
        var startBtn = new Button
        {
            Content = "开始专注",
            FontSize = 14,
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(14, 0, 0, 0),
            Background = (Brush)FindResource("AccentBrush"),
            Foreground = Brushes.Black,
            FontWeight = FontWeights.Bold,
            Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(0),
        };
        startBtn.Click += (_, _) =>
        {
            FirstRunSetup.CompleteSetup();
            banner.Visibility = Visibility.Collapsed;
            _focus.Enter(); // 从这一刻起锁定
            ShowBlocked("已进入专注模式");
        };
        sp.Children.Add(startBtn);
        banner.Child = sp;
        // 插到 HomeView 顶部
        if (HomeView is StackPanel sp2)
        {
            sp2.Children.Insert(0, banner);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        App.SmokeLog("window: source initialized");
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // 显式夺取前台：Windows ForegroundLockTimeout 可能阻止新进程抢焦点
        Activate();
        var hwnd = new WindowInteropHelper(this).Handle;
        SetForegroundWindow(hwnd);
        App.SmokeLog("window: content rendered, foreground claimed");
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_CLOSE = 0x0010;
        if (msg == WM_CLOSE && _focus.IsActive)
        {
            handled = true; // 锁定期间不响应系统关闭（退出只能走软件内按钮）
        }
        return IntPtr.Zero;
    }
}
