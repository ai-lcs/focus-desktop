import re
p = r'D:/focus-desktop/src/focus-desktop/MainWindow.xaml.cs'
src = open(p, encoding='utf-8').read()

# ===== 1) InitAsync：懒加载（注册不建控件）+ 崩溃自愈接线 + Runtime 缺失提示 =====
old = '''        try
        {
            _web = new WebTabService();
            await _web.EnsureEnvironmentAsync();
            WebTabService.Blocked += host => Dispatcher.Invoke(() => ShowBlocked($"已拦截：{host}"));
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
        }'''
new = '''        try
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
        }'''
assert old in src, "InitAsync web block not found"
src = src.replace(old, new)

# smoke 分支保持急切创建（高危路径必须被测）——改为注册后逐个 Ensure
old = '''                _hostPanel = new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Fill };
                WfHost.Child = _hostPanel;

                await _web.CreateTabAsync("bili", "Bilibili", "https://www.bilibili.com", _hostPanel);
                await _web.CreateTabAsync("chatgpt", "ChatGPT", "https://chatgpt.com", _hostPanel);
                await _web.CreateTabAsync("gemini", "Gemini", "https://aistudio.google.com", _hostPanel);
                await _web.CreateTabAsync("deepseek", "DeepSeek", "https://chat.deepseek.com", _hostPanel);
                App.SmokeLog("smoke: 4 web tabs created");'''
new = '''                _hostPanel = new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Fill };
                WfHost.Child = _hostPanel;

                // smoke 仍全量急切创建（高危路径必须被测）
                _web.RegisterTab("bili", "Bilibili", "https://www.bilibili.com");
                _web.RegisterTab("chatgpt", "ChatGPT", "https://chatgpt.com");
                _web.RegisterTab("gemini", "Gemini", "https://aistudio.google.com");
                _web.RegisterTab("deepseek", "DeepSeek", "https://chat.deepseek.com");
                foreach (var t in _web.Tabs.ToList())
                    await _web.EnsureTabAsync(t.Id, _hostPanel);
                App.SmokeLog("smoke: 4 web tabs created");'''
assert old in src, "smoke web block not found"
src = src.replace(old, new)

# ===== 2) 字段：_everActivated 记录激活过的 Tab（崩溃重建依据）+ PDF 计数 =====
old = '''    // ---- Web 层 ----
    private WebTabService? _web;
    private readonly Dictionary<string, WpfButton> _tabButtons = new();
    private System.Windows.Forms.Panel? _hostPanel;
    private string _activeTab = "home";'''
new = '''    // ---- Web 层 ----
    private WebTabService? _web;
    private readonly Dictionary<string, WpfButton> _tabButtons = new();
    private System.Windows.Forms.Panel? _hostPanel;
    private string _activeTab = "home";
    private readonly HashSet<string> _everActivated = new();
    private int _pdfCount; // PDF 多开计数'''
assert old in src
src = src.replace(old, new)

# ===== 3) OnTabTitleChanged：新结构（按钮 Content 是 Border 不是字符串）=====
old = '''            WebTabService.TitleChanged += (id, title) => Dispatcher.Invoke(() =>
            {
                if (_tabButtons.TryGetValue(id, out var btn))
                    btn.Content = title.Length > 18 ? title[..18] : title;
            });'''
# 上面已被 1) 替换掉，加独立方法
anchor = '''    private WpfButton MakeTabButton(string id, string title, bool closable)'''
addition = '''    private void OnTabTitleChanged(string id, string title)
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

''' + anchor
assert anchor in src
src = src.replace(anchor, addition, 1)

# ===== 4) OpenFile：PDF/图片/TXT 多开（每次新 Tab）=====
old = '''    private void OpenFile(string path)
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
        else
        {
            ShowBlocked($"V1 内置支持 PDF/图片/TXT，暂不支持 {ext}。请先转 PDF 放入学习目录。");
        }
    }'''
new = '''    private async void OpenFile(string path)
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
    }'''
assert old in src, "OpenFile not found"
src = src.replace(old, new)

# ===== 5) ActivateTab 里记录 _everActivated =====
old = '''        _web?.Activate(id);
        RefreshTabVisuals();
    }'''
new = '''        if (isWeb) _everActivated.Add(id);
        _web?.Activate(id);
        RefreshTabVisuals();
    }'''
assert old in src
src = src.replace(old, new)

# ===== 6) Ctrl+Tab / Ctrl+数字 快捷键 =====
old = '''        else if (e.Key == System.Windows.Input.Key.R && HomeView.Visibility == Visibility.Visible'''
new = '''        else if (e.Key == System.Windows.Input.Key.Tab
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
        else if (e.Key == System.Windows.Input.Key.R && HomeView.Visibility == Visibility.Visible'''
assert old in src
src = src.replace(old, new)

open(p, 'w', encoding='utf-8').write(src)
print("MainWindow.xaml.cs: lazy load + pdf multi-open + ctrl+tab + error card done")
