using System.Management;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using focus_desktop;
using focus_desktop.Services;

internal sealed partial class RuntimeApp
{
    private static async Task VerifySlowLaunchAsync(MainWindow main, FocusModeService focus)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "FlowFixture.exe");
        File.Copy(Environment.ProcessPath!, fixturePath, true);
        var entry = ExternalAppCatalog.Resolve(fixturePath);
        entry.Arguments = Environment.ProcessId + " --delay-window=14000";
        entry.Id = (string)typeof(ExternalAppEntry).GetMethod("CreateWin32StableId", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object?[] { entry.ExecutablePath, entry.Arguments, entry.WorkingDirectory })!;
        var dockType = typeof(MainWindow).Assembly.GetType("focus_desktop.DockWindow")!;
        using var keyboard = new KeyboardGuardService();
        var dock = (Window)Activator.CreateInstance(dockType, main, focus, new[] { entry }, keyboard, false)!;
        var originalProcesses = System.Diagnostics.Process.GetProcessesByName("FlowFixture").Select(process => process.Id).ToHashSet();
        try
        {
            Check((bool)Call(dock, "TryShowDock", false)!, "slow-launch Dock opens");
            Call(dock, "ReturnHome");
            var launch = (Task)Call(dock, "ActivateEntryAsync", entry)!;
            await Task.Delay(250);
            Check(((TextBlock)Field(dock, "_status")!).Text == "", "launch adds no starting message");
            await launch;
            Check(((TextBlock)Field(dock, "_status")!).Text == "" && !main.Topmost,
                "successful Shell launch without a window after 12 seconds is not a timeout error or forced return");
            var retry = (Task)Call(dock, "ActivateEntryAsync", entry)!;
            System.Diagnostics.Process[] fixtures = Array.Empty<System.Diagnostics.Process>();
            for (var attempt = 0; attempt < 150; attempt++)
            {
                foreach (var process in fixtures) process.Dispose();
                fixtures = System.Diagnostics.Process.GetProcessesByName("FlowFixture").Where(process => !originalProcesses.Contains(process.Id)).ToArray();
                if (fixtures.Length == 1 && fixtures[0].MainWindowHandle != IntPtr.Zero) break;
                await Task.Delay(100);
            }
            Check(fixtures.Length == 1 && fixtures[0].MainWindowHandle != IntPtr.Zero,
                "slow application becomes available and stays alive after the initial observation");
            await retry;
            await (Task)Call(dock, "ActivateEntryAsync", entry)!;
            Check(((ExternalAppWindowService)Field(dock, "_windows")!).IsExternalWindowForeground,
                "next pinned-entry click switches to the late window: " + ((TextBlock)Field(dock, "_status")!).Text);
            Check(System.Diagnostics.Process.GetProcessesByName("FlowFixture").Count(process => !originalProcesses.Contains(process.Id)) == 1,
                "switching reuses the late application instead of launching another process");
            foreach (var process in fixtures) process.Dispose();
        }
        finally
        {
            dock.Close();
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("FlowFixture").Where(process => !originalProcesses.Contains(process.Id)))
            {
                using (process)
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(3000)) process.Kill();
                }
            }
        }
    }

    private static async Task VerifySystemControlsAsync(MainWindow main, bool observe)
    {
        var controls = Field(main, "_systemControls")!;
        var host = (FrameworkElement)Call(main, "DetachSystemControls")!;
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new Grid { Background = System.Windows.Media.Brushes.DarkSlateGray };
        sidebar.Children.Add(host);
        host.VerticalAlignment = VerticalAlignment.Bottom;
        root.Children.Add(sidebar);
        var outside = new TextBlock { Text = "网页 / 其他软件区域", Margin = new Thickness(16) };
        Grid.SetColumn(outside, 1);
        root.Children.Add(outside);
        var window = new Window { Title = "Focus Desk 侧栏调节验收", Width = 440, Height = 540, Content = root };
        window.Show();
        var popup = (Popup)Field(controls, "_popup")!;
        var inline = (Border)Field(controls, "_inlineHost")!;
        var launchers = (StackPanel)controls.GetType().GetProperty("Launchers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controls)!;
        var speaker = (System.Windows.Controls.Button)launchers.Children[0];
        var sun = (System.Windows.Controls.Button)launchers.Children[1];
        var capture = typeof(VolumeHelper).GetMethod("CaptureState", BindingFlags.Static | BindingFlags.NonPublic)!;
        var restore = typeof(VolumeHelper).GetMethod("RestoreState", BindingFlags.Static | BindingFlags.NonPublic)!;
        var original = capture.Invoke(null, null)!;
        var originalVolume = VolumeHelper.Get();
        var mute = (System.Windows.Controls.Button)Field(controls, "_mute")!;
        var audio = (StackPanel)Field(controls, "_audio")!;
        var light = (StackPanel)Field(controls, "_lighting")!;
        var panel = (Border)Field(controls, "_panel")!;
        var volumeSlider = (Slider)Field(controls, "_volume")!;
        var brightnessSlider = (Slider)Field(controls, "_light")!;
        Rect SliderBounds(Slider slider) => new(slider.TranslatePoint(new System.Windows.Point(), panel), slider.RenderSize);
        try
        {
            var initialMute = VolumeHelper.IsMuted();
            speaker.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var origin = panel.TranslatePoint(new System.Windows.Point(), sidebar);
            Check(audio.Visibility == Visibility.Visible && light.Visibility == Visibility.Hidden && !popup.IsOpen
                && origin.X >= 0 && origin.X + panel.ActualWidth <= 96.1, "speaker opens only volume inside sidebar");
            Check(VolumeHelper.IsMuted() == initialMute, "outer speaker does not change native mute");
            var volumePanelSize = panel.RenderSize;
            var volumeSliderBounds = SliderBounds(volumeSlider);
            speaker.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(inline.Visibility == Visibility.Collapsed, "outer speaker closes volume");
            sun.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            window.UpdateLayout();
            Check(panel.RenderSize == volumePanelSize && SliderBounds(brightnessSlider) == volumeSliderBounds,
                "sidebar panels have identical dimensions and slider alignment");
            Check(light.Visibility == Visibility.Visible && audio.Visibility == Visibility.Hidden, "sun opens only brightness");
            speaker.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(audio.Visibility == Visibility.Visible && light.Visibility == Visibility.Hidden, "switching icons replaces the panel");
            mute.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(VolumeHelper.IsMuted() != initialMute, "inner speaker changes verified system-wide playback mute");
            mute.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(VolumeHelper.IsMuted() == initialMute, "inner speaker toggles native mute back");
            var targetVolume = originalVolume == 100 ? 99 : originalVolume + 1;
            VolumeHelper.Set(targetVolume);
            Check(VolumeHelper.Get() == targetVolume, "volume writes and reads the current default output");
            VolumeHelper.Set(originalVolume);
            Console.WriteLine("Active output endpoints verified: " + ((System.Collections.ICollection)capture.Invoke(null, null)!).Count);
            if (observe)
            {
                Console.WriteLine("SYSTEM_CONTROLS_UI_READY " + Environment.ProcessId);
                await WaitForFeatureSignal("system-controls-done");
            }
        }
        finally
        {
            VolumeHelper.Set(originalVolume);
            restore.Invoke(null, new[] { original });
            sidebar.Children.Clear();
            host.VerticalAlignment = VerticalAlignment.Stretch;
            Call(main, "RestoreSystemControls");
            window.Close();
        }
        Check(!popup.IsOpen && ReferenceEquals(((Border)Field(main, "SystemControlsHost")!).Child, host),
            "closing the sidebar restores the same controls to the top bar");
        sun.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(popup.IsOpen && inline.Visibility == Visibility.Collapsed,
            "without a sidebar the sun icon still opens the controls from the top bar");
        panel.UpdateLayout();
        var brightnessPopupSize = panel.RenderSize;
        var brightnessPopupSliderBounds = SliderBounds(brightnessSlider);
        sun.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(!popup.IsOpen, "top bar sun closes its brightness panel");
        speaker.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        panel.UpdateLayout();
        Check(panel.RenderSize == brightnessPopupSize && SliderBounds(volumeSlider) == brightnessPopupSliderBounds,
            "top bar panels have identical dimensions and slider alignment");
        Check(popup.IsOpen && audio.Visibility == Visibility.Visible && light.Visibility == Visibility.Hidden,
            "top bar speaker opens only volume");
        speaker.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(!popup.IsOpen, "top bar speaker closes its volume panel");
        Call(controls, "Close");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    private static async Task VerifyTextPreviewsAsync(WebTabService web, System.Windows.Forms.Panel panel, string study)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var plain = "\n\n中文阅读测试 <原样文本> & symbols\n第二行\t缩进\n" + new string('长', 3000);
        File.WriteAllBytes(Path.Combine(study, "pixel.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII="));
        var outside = Path.Combine(study, "..", "outside.png");
        File.Copy(Path.Combine(study, "pixel.png"), outside, true);
        var markdown = "# 中文标题\n\n- 列表一\n- 列表二\n\n| 名称 | 数量 |\n| --- | --- |\n| 中文 | 2 |\n\n```cs\n<代码> & symbols\n```\n\n<script>window.injected=1</script>\n\n![远程图片](https://example.invalid/pixel.png)\n\n[危险链接](javascript:alert(1))\n\n![本地](pixel.png)\n\n![越界](../outside.png)";
        var cases = new[] {
            ("utf8.txt", plain, (Encoding)new UTF8Encoding(false)),
            ("utf16.txt", plain, Encoding.Unicode),
            ("gbk.txt", plain, Encoding.GetEncoding(936)),
            ("阅读测试.md", markdown, (Encoding)new UTF8Encoding(false)),
        };
        foreach (var (name, text, encoding) in cases)
        {
            var path = Path.Combine(study, name);
            File.WriteAllText(path, text, encoding);
            var id = "preview-" + name;
            web.RegisterTab(id, name, new Uri(path).AbsoluteUri);
            var tab = await web.EnsureTabAsync(id, panel);
            var core = tab.View!.CoreWebView2;
            var remoteRequests = 0;
            core.AddWebResourceRequestedFilter("https://example.invalid/*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) => { if (e.Request.Uri.StartsWith("https://example.invalid/")) remoteRequests++; };
            for (var attempt = 0; attempt < 200; attempt++)
            {
                if (await core.ExecuteScriptAsync("!!document.querySelector('article footer')") == "true") break;
                await Task.Delay(50);
            }
            Check(await core.ExecuteScriptAsync("getComputedStyle(document.body).backgroundColor") == "\"rgb(255, 255, 255)\"", name + " has a white rendered background");
            if (name.EndsWith(".txt"))
                Check(JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.querySelector('pre.plain').textContent")) == plain,
                    name + " preserves Chinese, special characters, tabs, newlines and long lines");
            else
            {
                Check(await core.ExecuteScriptAsync("document.querySelector('img[alt=本地]').src.startsWith('data:image/png;base64,') && document.querySelector('img[alt=越界]').src === 'data:,'") == "true", "local images stay inside the study directory");
                Check(await core.ExecuteScriptAsync("document.querySelector('h1').textContent === '中文标题' && document.querySelectorAll('li').length === 2 && !!document.querySelector('table') && document.querySelector('pre code').textContent.includes('<代码> & symbols')") == "true", "Markdown headings, lists, tables and code render in WebView");
                Check(await core.ExecuteScriptAsync("!window.injected && !document.querySelector('script')") == "true" && remoteRequests == 0, "Markdown raw HTML/scripts and automatic remote images are blocked");
                await core.ExecuteScriptAsync("document.querySelector('a').click()");
                Check(await core.ExecuteScriptAsync("!window.injected") == "true", "unsafe Markdown link cannot execute a script");
            }
            Check(tab.View.DefaultBackgroundColor.ToArgb() == System.Drawing.Color.White.ToArgb(), name + " uses a white native WebView background");
        }
        var invalid = Path.Combine(study, "invalid.txt");
        File.WriteAllBytes(invalid, new byte[] { 0x81 });
        web.RegisterTab("invalid-encoding", "Invalid encoding", new Uri(invalid).AbsoluteUri);
        var invalidTab = await web.EnsureTabAsync("invalid-encoding", panel);
        for (var i = 0; i < 200; i++)
        {
            if (await invalidTab.View!.CoreWebView2.ExecuteScriptAsync("document.body && document.body.innerText.includes('无法读取文本')") == "true") break;
            await Task.Delay(50);
        }
        Check(await invalidTab.View!.CoreWebView2.ExecuteScriptAsync("document.body.innerText.includes('无法读取文本')") == "true", "malformed encoding displays an error instead of dropping characters");
    }

    private async Task RunFeatureTestsAsync(MainWindow main, FocusModeService focus, string study, AppSettings settings, string[] args)
    {
        VolumeHelper.TryRead(out var originalVolume, out var originalMute);
        using var brightnessQuery = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightness WHERE Active = TRUE");
        using var monitors = brightnessQuery.Get();
        var originalBrightness = monitors.Cast<ManagementObject>().Select(m => Convert.ToInt32(m["CurrentBrightness"])).FirstOrDefault(-1);
        File.WriteAllText(Path.Combine(Paths.DataDir, "hardware-before.json"), JsonSerializer.Serialize(new { Volume = originalVolume, Mute = originalMute, Brightness = originalBrightness }));
        try
        {
            var catalog = await ExternalAppCatalog.ScanDesktopAsync();
            Check(catalog.Entries.Count > 0, "Windows application catalog is populated");
            Check(catalog.Entries.All(entry => entry.TryValidate(out _)), $"all {catalog.Entries.Count} discovered entries validate");
            Check(!catalog.Entries.Any(entry => catalog.Entries.Any(other => !ReferenceEquals(entry, other) && entry.HasSameWindowMatchIdentity(other))), "catalog has one entry per EXE/AUMID");
            foreach (var office in new[] { "Word", "Excel", "PowerPoint" })
                Check(catalog.Entries.Any(entry => entry.DisplayName == office), office + " discovered without desktop shortcuts");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try { await ExternalAppCatalog.ScanDesktopAsync(cancellation.Token); throw new Exception("Scan was not cancelled"); }
                catch (OperationCanceledException) { Console.WriteLine("PASS cancelled scan produces no stale results"); }
            }

            var selection = new ExternalAppSelectionControl();
            var first = ExternalAppCatalog.Resolve(Environment.ProcessPath!);
            first.DisplayName = "同名应用";
            var second = ExternalAppCatalog.Resolve(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"));
            second.DisplayName = "同名应用";
            var add = selection.GetType().GetMethod("AddEntry", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Check((bool)add.Invoke(selection, new object?[] { first, null })! && (bool)add.Invoke(selection, new object?[] { second, null })!, "same display name does not merge different executable identities");
            var duplicate = ExternalAppCatalog.Resolve(Environment.ProcessPath!);
            duplicate.Arguments = "--different";
            duplicate.Id = (string)typeof(ExternalAppEntry).GetMethod("CreateWin32StableId", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object?[] { duplicate.ExecutablePath, duplicate.Arguments, duplicate.WorkingDirectory })!;
            Check(!(bool)add.Invoke(selection, new object?[] { duplicate, null })!, "same executable with different launch arguments is not duplicated");
            var chooser = new Window { Title = "Focus Desk 应用选择验收", Width = 850, Height = 680,
                Background = System.Windows.Media.Brushes.DarkSlateGray, Content = selection };
            chooser.Show();
            await selection.InitializeAsync();
            var selectedOffice = catalog.Entries.First(entry => entry.DisplayName == "Word");
            var check = FindChecks(selection).First(box => System.Windows.Automation.AutomationProperties.GetAutomationId(box) == "ExternalApp_" + selectedOffice.Id);
            check.IsChecked = true;
            var search = (System.Windows.Controls.TextBox)Field(selection, "SearchBox")!;
            search.Text = "Excel";
            await (Task)Call(selection, "RefreshAsync")!;
            Check(selection.GetSelectedEntries().Single().Id == selectedOffice.Id, "search and refresh preserve selected launch definition");
            search.Text = "";
            if (args.Contains("--feature-ui"))
            {
                Console.WriteLine("FEATURE_UI_READY " + Environment.ProcessId);
                await WaitForFeatureSignal("selection-done");
            }
            chooser.Close();
            selection.CancelPendingOperations();
            WebTabService? web = null;
            for (var i = 0; i < 200; i++)
            {
                web = Field(main, "_web") as WebTabService;
                if (web != null) break;
                await Task.Delay(50);
            }
            var panel = (System.Windows.Forms.Panel)Field(main, "_hostPanel")!;
            await VerifyTextPreviewsAsync(web!, panel, study);
            var controls = Field(main, "_systemControls")!;
            var apply = controls.GetType().GetMethod("ApplyBrightness", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var stateType = controls.GetType().Assembly.GetType("focus_desktop.Services.BrightnessState")!;
            apply.Invoke(controls, new[] { Activator.CreateInstance(stateType, new object?[] { null, "合成：不支持亮度" }) });
            Check(!((Slider)Field(controls, "_light")!).IsEnabled && ((TextBlock)Field(controls, "_lightText")!).Text == "不可用",
                "unsupported brightness disables slider and displays no fake percent");
            if (!args.Contains("--feature-ui")) return;

            Console.WriteLine("NO_DOCK_UI_READY");
            await WaitForFeatureSignal("no-dock-done");
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "FlowFixture.exe");
            File.Copy(Environment.ProcessPath!, fixturePath, true);
            settings.ExternalApps = new() { ExternalAppCatalog.Resolve(fixturePath) };
            settings.Save();
            focus.ConfigureDock(main, settings.ExternalApps, false);
            Check(focus.ShowDevelopmentDock(false), "development Dock opens with control launchers");
            Console.WriteLine("DOCK_UI_READY");
            await WaitForFeatureSignal("dock-done");
            focus.Exit();
        }
        finally
        {
            if (args.Contains("--feature-ui"))
            {
                VolumeHelper.Set(originalVolume);
                if (VolumeHelper.IsMuted() != originalMute) VolumeHelper.ToggleMute();
                if (originalBrightness >= 0)
                {
                    var service = Field(Field(main, "_systemControls")!, "_brightness")!;
                    await (Task)service.GetType().GetMethod("SetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, new object[] { originalBrightness })!;
                }
                Console.WriteLine($"RESTORED volume={VolumeHelper.Get()} mute={VolumeHelper.IsMuted()} brightness={originalBrightness}");
            }
        }
    }

    private static IEnumerable<System.Windows.Controls.CheckBox> FindChecks(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is System.Windows.Controls.CheckBox check) yield return check;
            foreach (var descendant in FindChecks(child)) yield return descendant;
        }
    }

    private static async Task WaitForFeatureSignal(string name)
    {
        var path = Path.Combine(Paths.DataDir, name);
        for (var i = 0; i < 1200; i++)
        {
            if (File.Exists(path) && File.ReadAllText(path).Trim() == Environment.ProcessId.ToString()) return;
            await Task.Delay(500);
        }
        throw new TimeoutException(name);
    }
}
