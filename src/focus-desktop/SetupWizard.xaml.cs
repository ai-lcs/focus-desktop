using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using focus_desktop.Services;
using Forms = System.Windows.Forms;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfCursors = System.Windows.Input.Cursors;
using Cursors = System.Windows.Input.Cursors;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace focus_desktop;

/// <summary>
/// Public v1 首次安装配置向导（4 步 + 底部导航）。
/// 独立全屏 Window，由 App 在启动时盖在 MainWindow 之上（不进锁定）。
/// 全程 GUI：草稿在内存，只有「完成并开始使用」才原子写 config.json + setup_done.flag；
/// 中途关闭应用不写任何配置，下次启动幂等重进向导。
/// </summary>
public partial class SetupWizard : Window
{
    /// <summary>向导已完成（config 已原子写入、setup_done.flag 已写）。App 层据此进入登录引导。</summary>
    public event Action<AppSettings>? Completed;

    private readonly AppSettings _draft;      // 内存草稿（AppSettings.LoadOrDefault() 初始化，提交时才落盘）
    private AppSettings? _previewCfg;         // 瞬态预览态（真预览 F2）：Save 被抑制，改动仅作用内存、预览后即弃
    private readonly Dictionary<string, WpfCheckBox> _presetChecks = new(StringComparer.OrdinalIgnoreCase);
    private int _step = 1;
    private bool _completed;
    private (int Work, int Short, int Long, int Cycles) _pomodoro = (25, 5, 15, 4);
    private Window? _returnButton;            // 「预览首页」时左上角的返回向导浮动窗

    /// <summary>preset 展示元数据（图标 + 一句话描述）。</summary>
    private static readonly Dictionary<string, (string Icon, string Desc)> PresetMeta = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bili"] = ("🎬", "学习视频与课程"),
        ["chatgpt"] = ("💬", "AI 对话助手"),
        ["gemini"] = ("✨", "谷歌 AI 工作台"),
        ["deepseek"] = ("🐋", "深度求索 AI 助手"),
    };

    public SetupWizard(AppSettings draft, Window owner)
    {
        _draft = draft;
        Owner = owner;
        InitializeComponent();
        InitDraftValues();
        BuildPresetCards();
        RefreshCustomList();
        GoToStep(1);
    }

    // ---------------- 初始化 ----------------

    private void InitDraftValues()
    {
        FolderBox.Text = _draft.StudyFolder;
        QuoteBox.Text = _draft.FocusQuote;
        ExitPhraseBox.Text = _draft.ExitPhrase;
        PomoWorkBox.Text = (_draft.PomodoroWorkMinutes ?? 25).ToString();
        PomoShortBox.Text = (_draft.PomodoroShortBreakMinutes ?? 5).ToString();
        PomoLongBox.Text = (_draft.PomodoroLongBreakMinutes ?? 15).ToString();
        PomoCyclesBox.Text = (_draft.PomodoroCyclesUntilLong ?? 4).ToString();
    }

    /// <summary>按 SiteCatalog 目录生成 4 张 preset 卡片；legacy 配置按旧白名单推导勾选。</summary>
    private void BuildPresetCards()
    {
        var legacy = _draft.IsLegacyConfig();
        foreach (var id in SiteCatalog.DefaultPresetIds)
        {
            var def = SiteCatalog.BuiltInSites[id];
            var meta = PresetMeta.TryGetValue(id, out var m) ? m : ("🌐", "");
            var card = MakePresetCard(def, m.Icon, m.Desc);
            if (legacy)
            {
                // legacy：默认值 + 已有白名单推导勾选（某 preset 的域命中旧白名单即勾上）
                card.IsChecked = def.WhitelistDomains.Any(d =>
                    _draft.Whitelist.Any(w => SiteCatalog.SameOrSubdomain(
                        SiteCatalog.NormalizeDomain(w), SiteCatalog.NormalizeDomain(d))));
            }
            _presetChecks[id] = card;
            PresetPanel.Children.Add(card);
        }
    }

    /// <summary>已勾选 preset 的域名全集（白名单+登录域）——供 ParseCustomInput 冲突检查。</summary>
    private static SiteCatalog.SiteDef? BuiltInSitesPreset(string tabKey) =>
        SiteCatalog.BuiltInSites.TryGetValue(tabKey, out var def) && def.IsPreset ? def : null;

    private WpfCheckBox MakePresetCard(SiteCatalog.SiteDef def, string icon, string desc)
    {
        var card = new Border
        {
            Width = 248,
            Height = 118,
            CornerRadius = new CornerRadius(12),
            BorderBrush = MakeBrush("#4A505A"),
            BorderThickness = new Thickness(1),
            Background = (Brush)FindResource("WzCardGradient"),
        };

        var sp = new StackPanel();
        var row1 = new StackPanel { Orientation = Orientation.Horizontal };
        row1.Children.Add(new TextBlock
        {
            Text = icon,
            FontSize = 26,
            Margin = new Thickness(14, 12, 10, 0),
            Foreground = (Brush)FindResource("WzText"),
        });
        row1.Children.Add(new TextBlock
        {
            Text = def.Title,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 18, 14, 0),
            Foreground = (Brush)FindResource("WzText"),
        });
        sp.Children.Add(row1);
        sp.Children.Add(new TextBlock
        {
            Text = desc,
            FontSize = 12,
            Margin = new Thickness(14, 4, 14, 0),
            Foreground = (Brush)FindResource("WzMuted"),
        });
        sp.Children.Add(new TextBlock
        {
            Text = def.Url,
            FontSize = 11,
            Margin = new Thickness(14, 6, 14, 12),
            Foreground = (Brush)FindResource("WzMuted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        card.Child = sp;

        var check = new WpfCheckBox
        {
            Content = card,
            IsChecked = true,
            Margin = new Thickness(4),
            Cursor = Cursors.Hand,
            Foreground = (Brush)FindResource("WzText"),
        };
        // T6 UIA 契约：preset 勾选框显式 id（SetupPreset_<tabkey>）
        System.Windows.Automation.AutomationProperties.SetAutomationId(check, $"SetupPreset_{def.TabKey}");
        check.Checked += (_, _) => card.Opacity = 1.0;
        check.Unchecked += (_, _) => card.Opacity = 0.5;
        return check;
    }

    /// <summary>自定义站点列表（添加成功项 / 可删除）。</summary>
    private void RefreshCustomList()
    {
        CustomListPanel.Children.Clear();
        if (_draft.Sites == null || _draft.Sites.Count == 0)
        {
            CustomListPanel.Children.Add(new TextBlock
            {
                Text = "还没有自定义网站。",
                FontSize = 13,
                Margin = new Thickness(4, 10, 0, 0),
                Foreground = (Brush)FindResource("WzMuted"),
            });
            return;
        }
        foreach (var s in _draft.Sites)
        {
            var row = new Border
            {
                Background = (Brush)FindResource("WzCardBg"),
                CornerRadius = new CornerRadius(8),
                BorderBrush = (Brush)FindResource("WzCardBorder"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 6, 0, 0),
                Padding = new Thickness(14, 10, 14, 10),
            };
            // T6 UIA 契约：每个已添加站点行可定位（SetupCustomItem_<id>）
            System.Windows.Automation.AutomationProperties.SetAutomationId(row, $"SetupCustomItem_{s.Id}");
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleTb = new TextBlock
            {
                Text = s.Title ?? s.Url ?? s.Id,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("WzText"),
            };
            var urlTb = new TextBlock
            {
                Text = s.Url ?? "",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("WzMuted"),
            };
            Grid.SetColumn(urlTb, 1);
            var del = new Button
            {
                Content = "✕",
                FontSize = 13,
                Padding = new Thickness(8, 4, 8, 4),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Foreground = (Brush)FindResource("WzMuted"),
                ToolTip = "删除该网站",
            };
            Grid.SetColumn(del, 2);
            var item = s;
            del.Click += (_, _) => { _draft.Sites!.Remove(item); RefreshCustomList(); };

            grid.Children.Add(titleTb);
            grid.Children.Add(urlTb);
            grid.Children.Add(del);
            row.Child = grid;
            CustomListPanel.Children.Add(row);
        }
    }

    // ---------------- 步骤导航 ----------------

    private void GoToStep(int step)
    {
        _step = step;
        Step1Panel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;

        UpdateStepIndicator(Step1Indicator, step == 1);
        UpdateStepIndicator(Step2Indicator, step == 2);
        UpdateStepIndicator(Step3Indicator, step == 3);

        BackButton.IsEnabled = step > 1;
        BackButton.Opacity = step > 1 ? 1.0 : 0.4;
        PrimaryButton.Content = step == 3 ? "完成并开始使用" : "下一步";
    }

    private void UpdateStepIndicator(TextBlock tb, bool active)
    {
        tb.Foreground = active ? (Brush)FindResource("WzStepActive") : (Brush)FindResource("WzStepInactive");
        tb.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 1) GoToStep(_step - 1);
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case 1: if (ValidateStep1()) GoToStep(2); break;
            case 2: if (ValidateStep2()) GoToStep(3); break;
            case 3: if (ValidateStep3()) CommitAndFinish(); break; // v1.0.2：背景步骤已移除，专注设置即末步（校验仍生效）
        }
    }

    // ---------------- 各步校验 ----------------

    private bool ValidateStep1()
    {
        var folder = FolderBox.Text.Trim();
        if (folder.Length == 0)
        {
            ShowError(FolderError, "请选择一个学习文件夹。");
            return false;
        }
        // 安全红线：学习目录绝不允许位于运行数据目录（%LOCALAPPDATA%\focus-desktop 或 portable 数据目录）
        // 内/相等——卸载会 DelTree 整个数据目录，「绝不碰学习文件」的承诺会因此失效（v1.0.2 审计）。
        try
        {
            var dataDir = System.IO.Path.GetFullPath(Paths.DataDir)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var full = System.IO.Path.GetFullPath(folder)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            if (full.Equals(dataDir, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(dataDir + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                ShowError(FolderError, "学习文件夹不能放在应用数据目录内（卸载时会连同清除）。请换一个位置。");
                return false;
            }
        }
        catch
        {
            ShowError(FolderError, "路径无法解析，请重新选择。");
            return false;
        }
        HideError(FolderError);
        return true;
    }

    private bool ValidateStep2()
    {
        var total = _presetChecks.Values.Count(c => c.IsChecked == true) + (_draft.Sites?.Count ?? 0);
        if (total == 0)
        {
            ShowError(SitesError, "至少保留 1 个站点才能继续。");
            return false;
        }
        HideError(SitesError);
        return true;
    }

    private bool ValidateStep3()
    {
        if (string.IsNullOrWhiteSpace(QuoteBox.Text))
        {
            ShowError(FocusError, "专注语不能为空。");
            return false;
        }
        if (string.IsNullOrWhiteSpace(ExitPhraseBox.Text))
        {
            ShowError(FocusError, "退出输入词不能为空。");
            return false;
        }
        if (!TryParseInt(PomoWorkBox.Text, out var work) || work < 1 || work > 180)
        {
            ShowError(FocusError, "工作时长需为 1–180 的整数。");
            return false;
        }
        if (!TryParseInt(PomoShortBox.Text, out var sh) || sh < 1 || sh > 60)
        {
            ShowError(FocusError, "短休息需为 1–60 的整数。");
            return false;
        }
        if (!TryParseInt(PomoLongBox.Text, out var lg) || lg < 1 || lg > 120)
        {
            ShowError(FocusError, "长休息需为 1–120 的整数。");
            return false;
        }
        if (!TryParseInt(PomoCyclesBox.Text, out var cy) || cy < 1 || cy > 12)
        {
            ShowError(FocusError, "长休息间隔需为 1–12 的整数。");
            return false;
        }
        _pomodoro = (work, sh, lg, cy);
        HideError(FocusError);
        return true;
    }

    private static bool TryParseInt(string? text, out int value) =>
        int.TryParse(text?.Trim(), out value);

    // ---------------- 第 1 步：学习文件夹 ----------------

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new Forms.FolderBrowserDialog
        {
            Description = "选择学习文件夹",
            ShowNewFolderButton = true,
        };
        // 默认目录可能尚不存在：只回填已存在的路径（FolderBrowserDialog 不接受不存在路径）
        var current = FolderBox.Text.Trim();
        if (current.Length > 0 && Directory.Exists(current)) dlg.SelectedPath = current;

        // Owner 用向导窗（当前唯一可交互的主界面）；避免无主对话框跑到主窗后面
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (dlg.ShowDialog(new HwndWrapper(hwnd)) == Forms.DialogResult.OK)
        {
            FolderBox.Text = dlg.SelectedPath;
            HideError(FolderError);
        }
    }

    // ---------------- 第 2 步：自定义网站 ----------------

    private void AddCustomSite_Click(object sender, RoutedEventArgs e)
    {
        App.SmokeLog($"wizard add-custom: url=[{CustomUrlBox.Text}] title=[{CustomTitleBox.Text}]");
        HideError(CustomError);
        // 简称必填且 ≤8 字符（v1.0.3 用户指示：杜绝「notebooklm.google.com」式超长按钮，保持首页对称）
        var title = CustomTitleBox.Text.Trim();
        if (title.Length == 0)
        {
            ShowError(CustomError, "请填写网站简称（将显示在首页和标签栏）。");
            return;
        }
        if (title.Length > 8)
        {
            ShowError(CustomError, "简称不能超过 8 个字符，请换一个更短的。");
            return;
        }
        // 已勾选 preset 的域也参与冲突检查（勾选状态是独立 UI 状态，不在 _draft.Sites 里——
        // 不传则 sub.bilibili.com 之类撞勾选 B 站的输入漏检，T6 实测定罪）
        var checkedPresetDomains = _presetChecks
            .Where(kv => kv.Value.IsChecked == true && BuiltInSitesPreset(kv.Key) != null)
            .SelectMany(kv => BuiltInSitesPreset(kv.Key)!.WhitelistDomains)
            .Concat(_presetChecks
                .Where(kv => kv.Value.IsChecked == true && BuiltInSitesPreset(kv.Key) != null)
                .SelectMany(kv => BuiltInSitesPreset(kv.Key)!.LoginDomains));
        var entry = SiteCatalog.ParseCustomInput(CustomUrlBox.Text, CustomTitleBox.Text, _draft, checkedPresetDomains);
        App.SmokeLog($"wizard add-custom: parse result = {(entry == null ? "null" : entry.Id)}");
        if (entry == null)
        {
            // 统一提示，不区分原因细节（非 http(s)/无 host/IP/域冲突/URL 重复）
            ShowError(CustomError, "网址无效或与已有站点重复");
            return;
        }
        _draft.Sites ??= new List<SiteCatalog.SiteEntry>();
        _draft.Sites.Add(entry);
        CustomUrlBox.Clear();
        CustomTitleBox.Clear();
        HideError(SitesError);
        RefreshCustomList();
    }

    // ---------------- 预览首页（无副作用） ----------------

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        PreviewDraft(); // 真预览（F2）：草稿态复制进瞬态预览态 → MainWindow 实时换肤；不写盘、可回退
        Hide();
        ShowReturnButton();
    }

    /// <summary>
    /// 真预览（F2）：把当前向导草稿拷贝成瞬态 AppSettings（IsTransient=true → Save 拒绝落盘），
    /// 调 MainWindow.ApplyConfigPreview 立即换肤首页/番茄钟/文件根。预览期再次点「预览首页」刷新为新草稿态；
    /// 「返回向导」或完成提交后预览态即弃。全程零磁盘副作用。
    /// </summary>
    private void PreviewDraft()
    {
        // 若用户从预览返回又改了草稿再预览：基于最新草稿重建（旧预览态被弃）
        var preview = new AppSettings
        {
            IsTransient = true,
            StudyFolder = FolderBox.Text.Trim().Length > 0 ? FolderBox.Text.Trim() : _draft.StudyFolder,
            FocusQuote = QuoteBox.Text.Trim(),
            ExitPhrase = ExitPhraseBox.Text.Trim(),
            PomodoroWorkMinutes = _pomodoro.Work,
            PomodoroShortBreakMinutes = _pomodoro.Short,
            PomodoroLongBreakMinutes = _pomodoro.Long,
            PomodoroCyclesUntilLong = _pomodoro.Cycles,
            BackgroundImage = null, // 背景图预览走独立通道（源图未导入 assets，config 语义不可表达）
            Sites = BuildPreviewSites(),
        };
        SiteCatalog.ComputeEffectiveDomains(preview, out var wl, out var ld);
        preview.Whitelist = wl;
        preview.LoginDomains = ld;

        _previewCfg = preview;
        if (Owner is MainWindow main) main.ApplyConfigPreview(preview);
    }

    /// <summary>预览站点集：勾选 preset（id 引用）+ 草稿中已加 custom（引用语义，与提交同一份列表）。</summary>
    private List<SiteCatalog.SiteEntry> BuildPreviewSites()
    {
        var sites = new List<SiteCatalog.SiteEntry>();
        foreach (var id in SiteCatalog.DefaultPresetIds)
        {
            if (_presetChecks[id].IsChecked == true)
                sites.Add(new SiteCatalog.SiteEntry { Id = id });
        }
        if (_draft.Sites != null) sites.AddRange(_draft.Sites);
        return sites;
    }

    /// <summary>左上角「返回向导」浮动窗（代码构建，独立 Topmost 小窗盖在 MainWindow 上）。</summary>
    private void ShowReturnButton()
    {
        if (_returnButton == null)
        {
            var host = new Border
            {
                Background = MakeBrush("#2B2F37"),
                BorderBrush = MakeBrush("#4A505A"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 9, 16, 9),
                Cursor = Cursors.Hand,
            };
            host.Child = new TextBlock
            {
                Text = "← 返回向导",
                FontSize = 14,
                Foreground = MakeBrush("#F0F4FF"),
            };
            host.MouseLeftButtonUp += (_, _) => ExitPreview();
            _returnButton = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Width = 132,
                Height = 44,
                Left = (Owner?.Left ?? 0) + 16,
                Top = (Owner?.Top ?? 0) + 14,
                Content = host,
                Title = "SetupBackToWizardButton",
            };
            // T6 UIA 契约：返回浮动窗可被自动化定位（Name 走 Title）
            System.Windows.Automation.AutomationProperties.SetAutomationId(_returnButton, "SetupBackToWizardButton");
        }
        _returnButton.Show();
        _returnButton.Activate();
    }

    private void ExitPreview()
    {
        _returnButton?.Hide();
        Show();
        Activate();
    }

    // ---------------- 完成并开始使用（原子提交） ----------------

    private void CommitAndFinish()
    {
        var folder = FolderBox.Text.Trim();
        try
        {
            Directory.CreateDirectory(folder); // 提交时创建（默认目录可能不存在）
        }
        catch
        {
            ShowError(FolderError, "无法创建该文件夹，请换一个位置。");
            GoToStep(1);
            return;
        }

        var final = new AppSettings
        {
            SchemaVersion = 2,
            Configured = true,
            StudyFolder = folder,
            ExitPhrase = ExitPhraseBox.Text.Trim(),
            FocusQuote = QuoteBox.Text.Trim(),
            PomodoroWorkMinutes = _pomodoro.Work,
            PomodoroShortBreakMinutes = _pomodoro.Short,
            PomodoroLongBreakMinutes = _pomodoro.Long,
            PomodoroCyclesUntilLong = _pomodoro.Cycles,
            BackgroundImage = null, // v1.0.2 背景图功能已移除（用户指示）：字段保留兼容旧 config 读取，不再写入
            Sites = new List<SiteCatalog.SiteEntry>(),
        };
        // 勾选的 preset（只存 id，域名真相在代码）+ 自定义条目（草稿里已含全字段）
        foreach (var id in SiteCatalog.DefaultPresetIds)
        {
            if (_presetChecks[id].IsChecked == true)
                final.Sites.Add(new SiteCatalog.SiteEntry { Id = id });
        }
        if (_draft.Sites != null) final.Sites.AddRange(_draft.Sites);

        // 白名单/登录域按最终站点集重算，落盘后运行时（WebTabService/UrlFilter）直接可用
        SiteCatalog.ComputeEffectiveDomains(final, out var wl, out var ld);
        final.Whitelist = wl;
        final.LoginDomains = ld;

        final.Save();                   // 原子写 config.json（中途退出永不落盘）
        FirstRunSetup.CompleteSetup();  // 保持 setup_done.flag 语义
        _completed = true;
        Completed?.Invoke(final);
        Close();
    }

    /// <summary>未完成就关闭（Alt+F4 / 进程退出前窗口被关）→ 不写 config，直接结束应用（幂等重进）。</summary>
    protected override void OnClosed(EventArgs e)
    {
        if (!_completed)
        {
            try { Application.Current.Shutdown(); } catch { }
        }
        base.OnClosed(e);
    }

    // ---------------- 工具 ----------------

    private static void ShowError(TextBlock tb, string message)
    {
        tb.Text = message;
        tb.Visibility = Visibility.Visible;
    }

    private static void HideError(TextBlock tb)
    {
        tb.Text = "";
        tb.Visibility = Visibility.Collapsed;
    }

    private static SolidColorBrush MakeBrush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    private sealed class HwndWrapper : Forms.IWin32Window
    {
        private readonly IntPtr _h;
        public HwndWrapper(IntPtr h) => _h = h;
        public IntPtr Handle => _h;
    }
}
