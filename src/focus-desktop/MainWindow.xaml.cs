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

    // ---- 自由计时器（时间戳 + 冻结剩余，Focuser 模型） ----
    private DateTime _phaseStart;
    private TimeSpan _elapsedWhenPaused;
    private bool _timerRunning;



    // ---- Web 层 ----
    private WebTabService? _web;
    private readonly Dictionary<string, WpfButton> _tabButtons = new();
    private System.Windows.Forms.Panel? _hostPanel;
    private string _activeTab = "home";
    private readonly HashSet<string> _everActivated = new();
    private int _pdfCount; // PDF 多开计数

    // ---- 文件浏览 ----
    private string _filesRoot;
    private string _currentDir;

    private bool _recoveryExitDone;

    public MainWindow(AppOptions options, FocusModeService focus)
    {
        _options = options;
        _focus = focus;
        InitializeComponent();

        var cfg = AppSettings.LoadOrDefault();
        _filesRoot = cfg.StudyFolder;
        _currentDir = _filesRoot;

        // 铺满主屏（单显示器项目，按用户决策不做多屏）。
        // 关键坑（2026-08-30 实测）：本机 WPF 逻辑尺寸≈物理尺寸的 2 倍（DPI 200% 感知），
        // 手动设 Width/Height=PrimaryScreen* 只改窗口物理矩形，内容仍按逻辑坐标布局
        // → 右栏卡片全部落在物理屏幕外（UIA 矩形 L=1656 > 屏宽 1280）。
        // 修法：Maximized 让 WPF/DWM 自己对齐屏幕，内容布局自适应；无边框全屏效果不变。
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0;
        Top = 0;
        // 预览模式：普通可切换窗口（不置顶、不覆盖全屏体验），给用户调配置用
        if (options.Preview)
        {
            Topmost = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Width = Math.Min(1280, SystemParameters.PrimaryScreenWidth - 80);
            Height = Math.Min(800, SystemParameters.PrimaryScreenHeight - 80);
        }
        else
        {
            Topmost = true;
            WindowState = WindowState.Maximized;
        }

        if (options.Dev || options.Preview)
        {
            DevBadge.Visibility = Visibility.Visible;
            DevBadgeText.Text = options.Preview ? "预览" : "DEV";
        }

        // 专注语（config.json focusQuote 可改）
        FocusQuote.Text = cfg.FocusQuote;

        // 番茄钟（桌面版设计控件，自带环形进度/模式按钮/蜂鸣）
        PomoControl.LoadConfig(cfg);

        // 时钟：顶栏小钟 + 首页大钟
        _clock.Tick += (_, _) =>
        {
            var now = DateTime.Now;
            BigClock.Text = now.ToString("HH:mm");
            ClockDate.Text = now.ToString("M月d日 dddd");
        };
        _clock.Start();
        var n = DateTime.Now;
        BigClock.Text = n.ToString("HH:mm");
        ClockDate.Text = n.ToString("M月d日 dddd");

        // 自由计时器 tick
        _timer.Tick += (_, _) =>
        {
            if (_timerRunning)
            {
                var elapsed = _elapsedWhenPaused + (DateTime.Now - _phaseStart);
                TimerBig.Text = elapsed.ToString(@"hh\:mm\:ss");
            }
        };

        Loaded += async (_, _) => await InitAsync();
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
        _volumeReady = true; // 先置位再设滑块值，避免构造期触发 Set
        VolumeSlider.Value = VolumeHelper.Get();
        VolumePct.Text = VolumeHelper.Get().ToString();
        MuteButton.Content = VolumeHelper.IsMuted() ? SPK_MUTE : SPK;
    }

    private async Task InitAsync()
    {
        if (_options.Smoke)
        {
            // smoke 模式也初始化 Web 层（验证 WebView2 环境创建 + 四站 Tab 不炸）
            try
            {
                _web = new WebTabService();
                await _web.EnsureEnvironmentAsync();
                App.SmokeLog("smoke: web environment created");
                WebTabService.Blocked += host => App.SmokeLog($"smoke: blocked {host}");

                _hostPanel = new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Fill };
                WfHost.Child = _hostPanel;

                // smoke 仍全量急切创建（高危路径必须被测）
                _web.RegisterTab("bili", "Bilibili", "https://www.bilibili.com");
                _web.RegisterTab("chatgpt", "ChatGPT", "https://chatgpt.com");
                _web.RegisterTab("gemini", "Gemini", "https://aistudio.google.com");
                _web.RegisterTab("deepseek", "DeepSeek", "https://chat.deepseek.com");
                foreach (var tab in _web.Tabs.ToList())
                    await _web.EnsureTabAsync(tab.Id, _hostPanel);
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
            WebTabService.Blocked += host => Dispatcher.Invoke(() => ShowBlocked($"已拦截：{host}"));
            WebTabService.TitleChanged += OnTabTitleChanged;
            WebTabService.Recovering += () => Dispatcher.InvokeAsync(async () =>
            {
                ShowBlocked("网页进程已重启，正在恢复标签页…");
                // 按原 URL 全量重建已激活过的 Tab
                if (_hostPanel != null)
                {
                    foreach (var t in _web.Tabs)
                    {
                        if (_everActivated.Contains(t.Id))
                        {
                            try { await _web.RecreateTabAsync(t.Id, _hostPanel); } catch { }
                        }
                    }
                    _web.Activate(_activeTab);
                    RefreshTabVisuals();
                }
            });

            // 懒加载：只注册元数据，首次激活才建控件（启动快 + 崩溃面小）
            _hostPanel = new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Fill };
            WfHost.Child = _hostPanel;

            _web.RegisterTab("bili", "Bilibili", "https://www.bilibili.com");
            _web.RegisterTab("chatgpt", "ChatGPT", "https://chatgpt.com");
            _web.RegisterTab("gemini", "Gemini", "https://aistudio.google.com");
            _web.RegisterTab("deepseek", "DeepSeek", "https://chat.deepseek.com");

            BuildTabBar();
        }
        catch (Exception ex)
        {
            App.SmokeLog($"web init failed: {ex.Message}");
            CrashReporter.Write(ex, "web-init");
            // Runtime 缺失/环境失败：中央友好卡片（不白屏死等）
            Dispatcher.Invoke(() => ShowWebErrorCard(ex.Message));
        }

        // 首次设置模式：config 不存在 → 进入设置（学习目录+登录+退出语）
        if (!AppSettings.Exists())
        {
            ShowSetupHint();
        }
    }

    // ---------------- 浏览器式 Tab 条（Home / 学习文件 / 网页 Tab 全在这切换） ----------------

    private void OnTabTitleChanged(string id, string title)
    {
        if (_tabButtons.TryGetValue(id, out var btn) && btn.Content is Border bg
            && bg.Child is System.Windows.Controls.Grid g && g.Children[0] is StackPanel sp
            && sp.Children[0] is TextBlock text)
        {
            var display = title.Length > 18 ? title[..18] : title;
            text.Text = display;
            text.ToolTip = title;
        }
    }

    /// <summary>Web 环境失败（如 Runtime 缺失）中央提示卡片。</summary>
    private void ShowWebErrorCard(string message)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("PanelBrush"),
            BorderBrush = (Brush)FindResource("DangerBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(28, 20, 28, 20),
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = "网页功能暂不可用",
            FontSize = 17, FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("FgBrush"), Margin = new Thickness(0, 0, 0, 8),
        });
        sp.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 13, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("MutedBrush"),
        });
        card.Child = sp;
        WebHost.Visibility = Visibility.Collapsed;
        // 挂到主 Grid（Row=1）中央
        if (Content is System.Windows.Controls.Grid root && root.Children.Count > 3)
            root.Children.Add(card);
        System.Windows.Controls.Grid.SetRow(card, 1);
    }

    private WpfButton MakeTabButton(string id, string title, bool closable)
    {
        // 浏览器式 Tab：圆角上缘 + 激活态顶部青绿下划线 + 可关闭 ✕（Chrome 视觉语言）
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var text = new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 150,
        };
        sp.Children.Add(text);

        if (closable)
        {
            var close = new WpfButton
            {
                Content = "\u2715",
                FontSize = 10,
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(7, 0, 0, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = (Brush)FindResource("MutedBrush"),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
            };
            close.Click += (_, _) => CloseTabById(id);
            sp.Children.Add(close);
        }

        var underline = new Border
        {
            Height = 2.5,
            Background = (Brush)FindResource("AccentBrush"),
            CornerRadius = new CornerRadius(1),
            Visibility = Visibility.Collapsed,
        };

        var grid = new System.Windows.Controls.Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sp.Margin = new Thickness(12, 7, closable ? 6 : 12, 6);
        System.Windows.Controls.Grid.SetRow(sp, 0);
        System.Windows.Controls.Grid.SetRow(underline, 1);
        grid.Children.Add(sp);
        grid.Children.Add(underline);

        var bg = new Border
        {
            CornerRadius = new CornerRadius(7, 7, 0, 0),
            Background = Brushes.Transparent,
            Child = grid,
        };

        var btn = new WpfButton
        {
            Content = bg,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 2, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(btn, $"tab_{id}");
        btn.Click += (_, _) => ActivateTab(id);
        return btn;
    }

    private void BuildTabBar()
    {
        TabBar.Children.Clear();
        _tabButtons.Clear();

        // 固定页（不可关）：Home + 学习文件
        _tabButtons["home"] = MakeTabButton("home", "\U0001F3E0 首页", false);
        TabBar.Children.Add(_tabButtons["home"]);
        _tabButtons["files"] = MakeTabButton("files", "\U0001F4C2 学习文件", false);
        TabBar.Children.Add(_tabButtons["files"]);

        // 网页 Tab（可关）
        if (_web != null)
        {
            foreach (var t in _web.Tabs)
            {
                var btn = MakeTabButton(t.Id, t.Title, true);
                _tabButtons[t.Id] = btn;
                TabBar.Children.Add(btn);
            }
        }
        ActivateTab("home");
    }

    /// <summary>注册新 Tab 并立即显示在 Tab 条（PDF 多开用）。</summary>
    private void AddWebTabButton(string id, string title)
    {
        if (_tabButtons.ContainsKey(id)) return;
        var btn = MakeTabButton(id, title, true);
        _tabButtons[id] = btn;
        TabBar.Children.Add(btn);
    }

    /// <summary>Tab 顺序（从 Tab 条 UIA id 还原）。</summary>
    private List<string> TabOrder() =>
        TabBar.Children.OfType<WpfButton>()
              .Select(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b)?[4..])
              .Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToList();

    private void CloseTabById(string id)
    {
        if (id is "home" or "files") return; // 固定页不可关
        var order = TabOrder();
        var pos = order.IndexOf(id);
        _web?.CloseTab(id);
        if (_tabButtons.Remove(id, out var btn) && btn.Parent is System.Windows.Controls.Panel parent)
            parent.Children.Remove(btn);
        var next = pos >= 0 && pos < order.Count - 1 ? order[pos + 1]
                 : pos > 0 ? order[pos - 1] : "home";
        if (_activeTab == id) ActivateTab(next);
        else RefreshTabVisuals();
    }

    private void RefreshTabVisuals()
    {
        foreach (var (tid, btn) in _tabButtons)
        {
            var active = tid == _activeTab;
            if (btn.Content is not Border bg) continue;
            bg.Background = active
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3B, 0x41, 0x4B))
                : Brushes.Transparent;
            if (bg.Child is System.Windows.Controls.Grid g
                && g.Children.Count == 2
                && g.Children[0] is StackPanel sp && sp.Children[0] is TextBlock text
                && g.Children[1] is Border underline)
            {
                text.Foreground = active ? (Brush)FindResource("FgBrush") : (Brush)FindResource("MutedBrush");
                underline.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private async void ActivateTab(string id)
    {
        _activeTab = id;
        HomeView.Visibility = id == "home" ? Visibility.Visible : Visibility.Collapsed;
        FilesView.Visibility = id == "files" ? Visibility.Visible : Visibility.Collapsed;
        var isWeb = _web != null && _web.Tabs.Any(t => t.Id == id);
        WebHost.Visibility = isWeb ? Visibility.Visible : Visibility.Collapsed;

        // 懒加载：首次激活才真正创建 WebView2 控件
        if (isWeb && _web != null && _hostPanel != null)
        {
            var info = _web.Tabs.First(t => t.Id == id);
            if (info.View == null)
            {
                if (_tabButtons.TryGetValue(id, out var btn0) && btn0.Content is Border b0
                    && b0.Child is System.Windows.Controls.Grid g0 && g0.Children[0] is StackPanel s0
                    && s0.Children[0] is TextBlock t0)
                    t0.Text = "加载中…";
                try { await _web.EnsureTabAsync(id, _hostPanel); }
                catch (Exception ex)
                {
                    ShowBlocked($"网页组件启动失败：{ex.Message}");
                    CrashReporter.Write(ex, $"lazy-tab-{id}");
                }
            }
        }

        if (isWeb) _everActivated.Add(id);
        if (isWeb) _everActivated.Add(id);
        _web?.Activate(id);
        RefreshTabVisuals();
    }

    // ---------------- 导航（首页按钮也走 ActivateTab） ----------------

    private void Nav_Files_Click(object sender, RoutedEventArgs e)
    {
        ActivateTab("files");
        RenderFiles();
    }
    private void Nav_Bili_Click(object sender, RoutedEventArgs e) => ActivateTab("bili");
    private void Nav_ChatGPT_Click(object sender, RoutedEventArgs e) => ActivateTab("chatgpt");
    private void Nav_Gemini_Click(object sender, RoutedEventArgs e) => ActivateTab("gemini");
    private void Nav_DeepSeek_Click(object sender, RoutedEventArgs e) => ActivateTab("deepseek");

    // ---------------- 自由计时器 ----------------

    private void Timer_Toggle_Click(object sender, RoutedEventArgs e) => ToggleTimer();
    private void Timer_Reset_Click(object sender, RoutedEventArgs e) => ResetTimer();

    private void ToggleTimer()
    {
        if (_timerRunning)
        {
            _elapsedWhenPaused += DateTime.Now - _phaseStart;
            _timerRunning = false;
            TimerBtnToggle.Content = "继续";
            TimerHint.Text = "已暂停";
        }
        else
        {
            _phaseStart = DateTime.Now;
            _timerRunning = true;
            _timer.Start();
            TimerBtnToggle.Content = "暂停";
            TimerHint.Text = "计时中";
        }
    }

    private void ResetTimer()
    {
        _timerRunning = false;
        _elapsedWhenPaused = TimeSpan.Zero;
        TimerBig.Text = "00:00:00";
        TimerBtnToggle.Content = "开始";
        TimerHint.Text = "空格 开始/暂停";
    }

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
        FilesPath.Text = _currentDir;
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
        var btn = new WpfButton
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

    private async void OpenFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".pdf" || ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" || ext is ".txt" or ".md")
        {
            if (_web == null || _hostPanel == null) { ShowBlocked("网页组件未就绪"); return; }

            // 同文件复用已开 Tab；否则新开（多开）
            var uri = new Uri(path).AbsoluteUri;
            var existing = _web.Tabs.FirstOrDefault(t =>
                t.Id.StartsWith("pdf-") && t.InitialUrl == uri);
            if (existing != null) { ActivateTab(existing.Id); return; }

            var id = $"pdf-{++_pdfCount}";
            var name = Path.GetFileNameWithoutExtension(path);
            _web.RegisterTab(id, name, uri);
            AddWebTabButton(id, name);
            await _web.EnsureTabAsync(id, _hostPanel);
            ActivateTab(id);
        }
        else
        {
            ShowBlocked($"V1 内置支持 PDF/图片/TXT，暂不支持 {ext}。请先转 PDF 放入学习目录。");
        }
    }

    // ---------------- 音量 ----------------

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volumeReady)
        {
            VolumeHelper.Set((int)e.NewValue);
            VolumePct.Text = ((int)e.NewValue).ToString();
            if (e.NewValue > 0) MuteButton.Content = SPK;
        }
    }

    /// <summary>静音/恢复（记住静音前音量）。</summary>
    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        var muted = VolumeHelper.ToggleMute();
        MuteButton.Content = muted ? SPK_MUTE : SPK;
    }

    private const string SPK = "🔊";
    private const string SPK_MUTE = "🔇";

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
        var cfg = AppSettings.LoadOrDefault();
        var dlg = new ExitWindow(cfg.ExitPhrase) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _focus.Exit();
            Application.Current.Shutdown();
        }
    }

    // ---------------- 键盘：空格计时 ----------------

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == System.Windows.Input.Key.Space && HomeView.Visibility == Visibility.Visible)
        {
            // 仅当焦点不在输入控件上时才当快捷键（避免在搜索框/退出框打空格误触）
            var focused = System.Windows.Input.FocusManager.GetFocusedElement(this) as System.Windows.DependencyObject;
            bool inInput = focused is System.Windows.Controls.TextBox or System.Windows.Controls.ComboBox;
            if (!inInput)
            {
                e.Handled = true;
                ToggleTimer();
            }
        }
        else if (e.Key == System.Windows.Input.Key.Tab
                 && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control)
        {
            // Ctrl+Tab：下一个 Tab（浏览器习惯）
            e.Handled = true;
            var order = TabOrder();
            if (order.Count > 1)
            {
                var i = order.IndexOf(_activeTab);
                var next = order[(i + 1) % order.Count];
                ActivateTab(next);
            }
        }
        else if (e.Key == System.Windows.Input.Key.D1
                 && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control)
        {
            e.Handled = true;
            var order = TabOrder();
            if (order.Count > 0) ActivateTab(order[0]);
        }
        else if (e.Key == System.Windows.Input.Key.D2
                 && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control)
        {
            e.Handled = true;
            var order = TabOrder();
            if (order.Count > 1) ActivateTab(order[1]);
        }
        else if (e.Key == System.Windows.Input.Key.R && HomeView.Visibility == Visibility.Visible
                 && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control
                 && _options.Dev)
        {
            e.Handled = true;
            ResetTimer();
        }
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
        var startBtn = new WpfButton
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
        // 插到 HomeView（现为 Grid）第一行上方：放进左列 StackPanel 顶部
        if (HomeView is Grid g && g.Children.Count > 0 && g.Children[0] is StackPanel left)
        {
            left.Children.Insert(0, banner);
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
