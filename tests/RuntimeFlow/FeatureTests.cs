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
