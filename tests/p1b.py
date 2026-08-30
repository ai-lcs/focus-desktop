import re
p = r'D:/focus-desktop/src/focus-desktop/MainWindow.xaml.cs'
src = open(p, encoding='utf-8').read()

# ===== A) Tab 系统整段替换（MakeTabButton → 旧 ActivateTab 结束）=====
start = src.find('    private WpfButton MakeTabButton')
end_anchor = '''        foreach (var (tid, btn) in _tabButtons)
        {
            var active = tid == id;
            btn.Background = active ? (Brush)FindResource("BgBrush") : (Brush)FindResource("PanelBrush");
            btn.Foreground = active ? (Brush)FindResource("FgBrush") : (Brush)FindResource("MutedBrush");
        }
    }'''
end = src.find(end_anchor)
assert start >= 0 and end > start, f"anchors: start={start} end={end}"
end += len(end_anchor)

new_block = '''    private WpfButton MakeTabButton(string id, string title, bool closable)
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
                Content = "\\u2715",
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
        _tabButtons["home"] = MakeTabButton("home", "\\U0001F3E0 首页", false);
        TabBar.Children.Add(_tabButtons["home"]);
        _tabButtons["files"] = MakeTabButton("files", "\\U0001F4C2 学习文件", false);
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
        _web?.Activate(id);
        RefreshTabVisuals();
    }'''

src = src[:start] + new_block + src[end:]

open(p, 'w', encoding='utf-8').write(src)
print("A) tab system replaced OK,", len(src), "chars")
