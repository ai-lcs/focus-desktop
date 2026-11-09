using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using focus_desktop;
using focus_desktop.Services;

// Uses its own build directory/profile, local pages and disposable fixture.
// --formal temporarily hides the taskbar and registers a real AppBar/keyboard
// hook, with production recovery and watchdog. --existing-app only adjusts an explicitly selected running application.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--watchdog")
            return WatchdogService.RunLoop(int.Parse(args[1]), long.Parse(args[2]), args[3]);
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "FlowFixture")
        {
            var fixtureApp = new System.Windows.Application();
            var fixture = new Window
            {
                Title = "Disposable flow fixture", WindowStyle = WindowStyle.SingleBorderWindow,
                ResizeMode = ResizeMode.CanResize, WindowState = WindowState.Maximized,
                Content = new System.Windows.Controls.TextBox { Text = "Unsaved synthetic fixture draft" },
            };
            fixture.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(fixture).Handle)!.AddHook(
                (IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled) =>
                {
                    if (message is 0x0200 or 0x00A0 or 0x0201 or 0x00A1)
                    {
                        NativeMethods.GetCursorPos(out var point);
                        if (point.Y >= System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Bottom - 2)
                            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "edge-input-events.jsonl"),
                                JsonSerializer.Serialize(new { Message = message, X = point.X, Y = point.Y, Ticks = Stopwatch.GetTimestamp() }) + "\n");
                    }
                    return IntPtr.Zero;
                });
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(args.Contains("--edge-input") ? 900 : 180) };
            if (args.Length >= 1 && uint.TryParse(args[0], out var parentId))
                fixture.Activated += (_, _) => NativeMethods.AllowSetForegroundWindow(parentId);
            timeout.Tick += (_, _) => fixture.Close();
            timeout.Start();
            fixtureApp.Run(fixture);
            return 0;
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "portable.flag"), "");
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "runtime-progress.log"), "Main reached\n");
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(args.Contains("--edge-input") ? 900 : 180));
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "runtime-progress.log"), "TIMEOUT\n");
            Environment.Exit(3);
        });
        Console.WriteLine("RuntimeFlow: starting isolated application");
        var app = new RuntimeApp();
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "runtime-progress.log"), "App created\n");
        var product = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "ProductApp.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = new XElement(presentation + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            product.Root!.Element(presentation + "Application.Resources")!.Elements());
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "runtime-progress.log"), "Resources loaded\n");
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Run();
        return app.Result;
    }
}

internal sealed class RuntimeApp : System.Windows.Application
{
    public int Result { get; private set; } = 1;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, PrivateInstance)!.GetValue(instance);
    private static object? Call(object instance, string name, params object[] arguments) =>
        instance.GetType().GetMethod(name, PrivateInstance | BindingFlags.Public)!.Invoke(instance, arguments);

    protected override async void OnStartup(StartupEventArgs e)
    {
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "runtime-progress.log"), "Startup reached\n");
        Console.WriteLine("RuntimeFlow: startup");
        MainWindow? main = null;
        Process? externalFixture = null;
        var ownsFixture = false;
        DesktopSessionLease? desktopLease = null;
        var formal = e.Args.Contains("--formal");
        var focus = new FocusModeService();
        var originalTaskbarLayered = GetWindowLongPtr(FindWindow("Shell_TrayWnd", null), -20).ToInt64() & 0x80000;
        var originalTaskbarState = (int)typeof(TaskbarService).GetMethod("ReadShellState", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        using var server = new TcpListener(IPAddress.Loopback, 0);
        using var cancellation = new CancellationTokenSource();
        server.Start();
        _ = Serve(server, cancellation.Token);
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var ticks = new List<double>();
        var switches = new List<double>();
        var externalSwitches = new List<double>();
        var lastTick = Stopwatch.GetTimestamp();
        var heartbeat = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(10) };
        heartbeat.Tick += (_, _) => { ticks.Add(Stopwatch.GetElapsedTime(lastTick).TotalMilliseconds); lastTick = Stopwatch.GetTimestamp(); };
        try
        {
            Paths.EnsureDirectories();
            if (e.Args.Contains("--edge-input"))
                foreach (var name in new[] { "edge-start", "edge-done", "edge-main-ready", "edge-external-ready" })
                    File.Delete(Path.Combine(Paths.DataDir, name));
            File.Delete(Path.Combine(AppContext.BaseDirectory, "edge-input-events.jsonl"));
            Console.WriteLine($"RuntimeFlow: profile {Paths.DataDir}");
            var study = Path.Combine(Paths.DataDir, "study");
            Directory.CreateDirectory(study);
            File.WriteAllText(Path.Combine(study, "sample.txt"), "synthetic study file");
            var settings = new AppSettings
            {
                Configured = true, ExternalAppsConfirmed = true, ExternalApps = new(), StudyFolder = study,
                Sites = new()
                {
                    new() { Id = "runtime-a", Title = "Test A", Url = $"http://localhost:{port}/a", Domains = new() { "localhost" } },
                    new() { Id = "runtime-b", Title = "Test B", Url = $"http://localhost:{port}/b", Domains = new() { "localhost" } },
                },
            };
            settings.Save();
            WebTabService.ProcessFailed += kind => Console.Error.WriteLine($"RuntimeFlow WebView process failure: {kind}");
            Console.WriteLine("RuntimeFlow: constructing main window");
            main = new MainWindow(new AppOptions(true, false, false, true), focus);
            main.Show();
            Console.WriteLine("RuntimeFlow: main window shown");
            if (e.Args.Contains("--skip-apps"))
            {
                var wizard = new SetupWizard(settings, main);
                wizard.Show();
                Call(wizard, "GoToStep", 4);
                var skip = (System.Windows.Controls.Button)Field(wizard, "SkipAppsButton")!;
                if (skip.Visibility != Visibility.Visible || !skip.IsEnabled) throw new Exception("Skip apps is not available");
                if (e.Args.Contains("--observe")) await Task.Delay(30000);
                skip.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                var saved = AppSettings.LoadOrDefault();
                if (!saved.IsConfigured() || saved.ExternalAppsConfirmed != true || saved.ExternalApps?.Count != 0)
                    throw new Exception("Skip apps did not freeze an empty application list");
                focus.ConfigureDock(main, saved.ExternalApps, formal);
                if (formal)
                {
                    if (!DesktopSessionCoordinator.TryAcquire(out desktopLease, out var skipReason)) throw new Exception(skipReason);
                    await focus.EnterAsync();
                    if (!focus.IsActive || IsWindowVisible(FindWindow("Shell_TrayWnd", null)))
                        throw new Exception("No-Dock focus did not hide the taskbar");
                    focus.Exit();
                    if (focus.HasRecoveryResponsibility || !IsWindowVisible(FindWindow("Shell_TrayWnd", null))
                        || originalTaskbarState != (int)typeof(TaskbarService).GetMethod("ReadShellState", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!
                        || (GetWindowLongPtr(FindWindow("Shell_TrayWnd", null), -20).ToInt64() & 0x80000) != originalTaskbarLayered)
                        throw new Exception("No-Dock exit did not restore the taskbar state");
                    Console.WriteLine("PASS no-Dock formal focus and taskbar recovery");
                }
                if (!focus.ShowDevelopmentDock(false) || Field(focus, "_dock") != null)
                    throw new Exception("Empty application list created a Dock");
                var clockRegion = (FrameworkElement)Field(main, "InlineClockRegion")!;
                if (clockRegion.Visibility != Visibility.Visible || clockRegion.Width != 96 || clockRegion.Height != 44)
                    throw new Exception("No-Dock clock region is missing or misaligned");
                Console.WriteLine("PASS wizard skip freezes empty list; no Dock; top-left clock retained");
                Result = 0;
                return;
            }
            WebTabService? web = null;
            for (var attempt = 0; attempt < 300; attempt++)
            {
                web = Field(main, "_web") as WebTabService;
                if (web?.Tabs.Count == 2) break;
                await Task.Delay(100);
            }
            if (web?.Tabs.Count != 2) throw new Exception("Web initialization did not finish");
            var panel = (System.Windows.Forms.Panel)Field(main, "_hostPanel")!;
            foreach (var tab in web.Tabs.ToArray())
            {
                var ready = await web.EnsureTabAsync(tab.Id, panel);
                if (ready.View == null) throw new Exception("Concurrent WebView creation returned an empty view");
                if (ready.View.CoreWebView2 == null) throw new Exception("Created WebView has no CoreWebView2");
                var core = ready.View.CoreWebView2;
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    if (await core.ExecuteScriptAsync("!!document.querySelector('#draft')") == "true") break;
                    await Task.Delay(100);
                }
                await core.ExecuteScriptAsync("document.querySelector('#draft').value='preserved';document.cookie='flow=preserved';window.flowToken=crypto.randomUUID()");
            }
            var views = web.Tabs.Select(t => t.View).ToArray();
            var tokens = new List<string>();
            foreach (var view in views) tokens.Add(await view!.CoreWebView2.ExecuteScriptAsync("window.flowToken"));
            var existingPath = e.Args.FirstOrDefault(arg => arg.StartsWith("--existing-app="))?[15..];
            var fixturePath = existingPath ?? Path.Combine(AppContext.BaseDirectory, "FlowFixture.exe");
            if (existingPath == null) File.Copy(Environment.ProcessPath!, fixturePath, true);
            var entryPath = e.Args.FirstOrDefault(arg => arg.StartsWith("--entry-executable="))?[19..];
            var fixtureEntry = ExternalAppCatalog.Resolve(entryPath ?? fixturePath);
            if (existingPath == null)
            {
                externalFixture = Process.Start(new ProcessStartInfo(fixturePath) { UseShellExecute = false, Arguments = Environment.ProcessId.ToString() + (e.Args.Contains("--edge-input") ? " --edge-input" : "") });
                ownsFixture = true;
            }
            else externalFixture = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(fixturePath))
                .FirstOrDefault(process => string.Equals(process.MainModule?.FileName, fixturePath, StringComparison.OrdinalIgnoreCase)
                    && process.MainWindowHandle != IntPtr.Zero) ?? throw new Exception("Explicit existing app is not running; test never launches it");
            for (var attempt = 0; attempt < 100; attempt++)
            {
                externalFixture!.Refresh();
                if (externalFixture.MainWindowHandle != IntPtr.Zero) break;
                await Task.Delay(50);
            }
            var originalPlacement = new NativePlacement { Length = Marshal.SizeOf<NativePlacement>() };
            if (!GetWindowPlacement(externalFixture!.MainWindowHandle, ref originalPlacement)) throw new Exception("Original external placement unavailable");
            var dockType = typeof(MainWindow).Assembly.GetType("focus_desktop.DockWindow")!;
            Window dock;
            if (formal)
            {
                if (!DesktopSessionCoordinator.TryAcquire(out desktopLease, out var reason)) throw new Exception(reason);
                settings.ExternalApps = new() { fixtureEntry };
                settings.Save();
                Call(main, "ConfigureDock", settings, true);
                await focus.EnterAsync();
                dock = (Window)Field(focus, "_dock")!;
                if (IsWindowVisible(FindWindow("Shell_TrayWnd", null))) throw new Exception("Formal taskbar did not hide");
                if (!GetWindowRect(new WindowInteropHelper(main).Handle, out var mainBounds)
                    || !GetWindowRect(new WindowInteropHelper(dock).Handle, out var dockBounds)) throw new Exception("Window bounds unavailable");
                var topWindow = (Window)Field(dock, "_topBar")!;
                if (!GetWindowRect(new WindowInteropHelper(topWindow).Handle, out var topBounds) || topBounds.Top != 0 || topBounds.Left != dockBounds.Right) throw new Exception($"Top bar bounds invalid: {topBounds.Left},{topBounds.Top},{topBounds.Right},{topBounds.Bottom}; dockRight={dockBounds.Right}");
                var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
                if (dockBounds.Top != screen.Top || dockBounds.Bottom != screen.Bottom
                    || mainBounds.Top != topBounds.Bottom || mainBounds.Bottom != screen.Bottom
                    || mainBounds.Left != dockBounds.Right || mainBounds.Right != screen.Right)
                    throw new Exception("Formal main/Dock leave a gap or overlap");
                Console.WriteLine($"PASS formal native bounds: Dock bottom={dockBounds.Bottom}, main bottom={mainBounds.Bottom}, screen bottom={screen.Bottom}; system taskbar hidden");
            }
            else dock = (Window)Activator.CreateInstance(dockType, main, focus, new[] { fixtureEntry }, new KeyboardGuardService(), false)!;
            try
            {
                if (!formal && !(bool)Call(dock, "TryShowDock", false)!) throw new Exception("Dock registration failed");
                Call(main, "ActivateTab", web.Tabs[0].Id);
                Call(dock, "ReturnHome");
                var returnButton = (System.Windows.Controls.Button)Field(dock, "_returnButton")!;
                if (System.Windows.Automation.AutomationProperties.GetItemStatus(returnButton) != "当前使用")
                    throw new Exception("Return button has no active indication");
                var dockRoot = (System.Windows.Controls.Grid)dock.Content;
                var clockCell = (FrameworkElement)dockRoot.Children[0];
                if (clockCell.ActualHeight != 44 || clockCell.ActualWidth != dock.ActualWidth)
                    throw new Exception("Clock cell does not align with the top/side bars");
                if (((FrameworkElement)Field(main, "InlineClockRegion")!).Visibility != Visibility.Collapsed)
                    throw new Exception("Clock duplicated while Dock is present");
                if ((string)Field(main, "_activeTab")! != "home") throw new Exception("Dock return button did not navigate home");
                Call(main, "ActivateTab", web.Tabs[1].Id);
                var menu = (System.Windows.Controls.Primitives.Popup)Call(main, "ShowNewTabMenu")!;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var addButton = (System.Windows.Controls.Button)Field(main, "_addTabBtn")!;
                if (!ReferenceEquals(menu.PlacementTarget, addButton)) throw new Exception("New-tab menu is not anchored to +");
                var buttonPoint = addButton.PointToScreen(new System.Windows.Point(0, addButton.ActualHeight));
                var menuPoint = menu.Child.PointToScreen(new System.Windows.Point(0, 0));
                if (Math.Abs(menuPoint.X - buttonPoint.X) > 4 || Math.Abs(menuPoint.Y - buttonPoint.Y) > 4)
                    throw new Exception($"New-tab menu position differs from +: {menuPoint} vs {buttonPoint}");
                Console.WriteLine("PASS Dock return navigates home; new-tab menu opens directly beneath +");
                if (e.Args.Contains("--observe-menu")) await Task.Delay(30000);
                menu.IsOpen = false;
                if (e.Args.Contains("--edge-input"))
                {
                    File.WriteAllText(Path.Combine(Paths.DataDir, "edge-main-ready"), Environment.ProcessId.ToString());
                    await WaitForEdgeSignal("edge-start");
                }
                else if (e.Args.Contains("--observe")) await Task.Delay(60000);
                var hwnd = new WindowInteropHelper(dock).Handle;
                Call(main, "ToggleTimer");
                var phaseStart = Field(main, "_phaseStart");
                HardFocus.Enable(true);
                lastTick = Stopwatch.GetTimestamp();
                heartbeat.Start();
                for (var cycle = 0; cycle < (entryPath == null ? 40 : 1); cycle++)
                {
                    foreach (var id in new[] { web.Tabs[0].Id, web.Tabs[1].Id, "files", "home" })
                    {
                        var start = Stopwatch.GetTimestamp();
                        Call(main, "ActivateTab", id);
                        if (id == "files") Call(main, "RenderFiles");
                        Call(main, "PrepareForExternalWindow");
                        Call(dock, "ReturnToFocusDesk");
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        switches.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                        await Task.Delay(15);
                        if (id == "home")
                        {
                            var externalStart = Stopwatch.GetTimestamp();
                            await (Task)Call(dock, "ActivateEntryAsync", fixtureEntry)!;
                            for (var settle = 0; settle < 15
                                && !((ExternalAppWindowService)Field(dock, "_windows")!).IsExternalWindowForeground; settle++)
                                await Task.Delay(16);
                            if (!((ExternalAppWindowService)Field(dock, "_windows")!).IsExternalWindowForeground)
                                throw new Exception($"Native fixture activation failed: mainActive={main.IsActive}, dockActive={dock.IsActive}; " + ((System.Windows.Controls.TextBlock)Field(dock, "_status")!).Text
                                    + "; " + Field(Field(dock, "_windows")!, "_layout")!.GetType().GetProperty("LastFailure", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Field(Field(dock, "_windows")!, "_layout")));
                            externalFixture!.Refresh();
                            var entryButtons = (Dictionary<string, System.Windows.Controls.Button>)Field(dock, "_entryButtons")!;
                            if (System.Windows.Automation.AutomationProperties.GetItemStatus(entryButtons[fixtureEntry.Id]) != "当前使用"
                                || System.Windows.Automation.AutomationProperties.GetItemStatus(returnButton) != "")
                                throw new Exception("Sidebar active indication does not follow application switching");
                            var topBar = (Window)Field(dock, "_topBar")!;
                            if (!GetWindowRect(new WindowInteropHelper(topBar).Handle, out var topRect)
                                || DwmGetWindowAttribute(GetForegroundWindow(), 9, out var appRect, Marshal.SizeOf<NativeRect>()) != 0)
                                throw new Exception("External visible frame not available");
                            var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
                            if (formal && (appRect.Left != topRect.Left || appRect.Top != topRect.Bottom
                                || appRect.Right != screen.Right || appRect.Bottom != screen.Bottom))
                                throw new Exception($"External frame leaves gap: {appRect.Left},{appRect.Top},{appRect.Right},{appRect.Bottom}");
                            var topWindow = (Window)Field(dock, "_topBar")!;
                            if (!topWindow.IsVisible || !topWindow.Topmost || !dock.Topmost) throw new Exception("Persistent bars disappeared");
                            externalSwitches.Add(Stopwatch.GetElapsedTime(externalStart).TotalMilliseconds);
                            if (entryPath != null)
                            {
                                await Task.Delay(15000);
                                if (!((ExternalAppWindowService)Field(dock, "_windows")!).IsExternalWindowForeground)
                                    throw new Exception("Launcher application lost window identity after 15 seconds");
                                Console.WriteLine("PASS launcher entry reused actual application window and retained identity for 15 seconds");
                            }
                            if (cycle == 0 && formal && e.Args.Contains("--reveal-regression"))
                            {
                                heartbeat.Stop();
                                var tray = FindWindow("Shell_TrayWnd", null);
                                for (var reveal = 0; reveal < 20; reveal++)
                                {
                                    ShowWindow(tray, 8); // Explorer's hover reveal must not expose or hit-test the tray.
                                    await Task.Delay(50);
                                    if (!(bool)typeof(TaskbarService).GetMethod("IsRevealSuppressed", BindingFlags.Static | BindingFlags.NonPublic, null, Type.EmptyTypes, null)!.Invoke(null, null)!)
                                        throw new Exception("Taskbar reveal suppression was lost");
                                    var bottomHit = WindowFromPoint(new NativePoint { X = screen.Width / 2, Y = screen.Bottom - 1 });
                                    if (GetAncestor(bottomHit, 2) == tray) throw new Exception("Invisible taskbar still intercepts the screen edge");
                                }
                                Console.WriteLine("PASS 20 forced taskbar reveals: alpha zero retained and bottom hit-test passes through");
                                lastTick = Stopwatch.GetTimestamp(); heartbeat.Start();
                            }
                            if (cycle == 0 && e.Args.Contains("--edge-input"))
                            {
                                heartbeat.Stop();
                                File.WriteAllText(Path.Combine(Paths.DataDir, "edge-external-ready"), Environment.ProcessId.ToString());
                                await WaitForEdgeSignal("edge-done");
                                if (!(bool)typeof(TaskbarService).GetMethod("IsRevealSuppressed", BindingFlags.Static | BindingFlags.NonPublic, null, Type.EmptyTypes, null)!.Invoke(null, null)!)
                                    throw new Exception("Taskbar suppression lost during actual bottom-edge input");
                                Console.WriteLine("PASS taskbar suppression retained after actual edge input");
                                lastTick = Stopwatch.GetTimestamp(); heartbeat.Start();
                            }
                            if (cycle == 0 && e.Args.Contains("--observe-external")) { heartbeat.Stop(); Console.WriteLine("OBSERVE external viewport for 60 seconds"); await Task.Delay(60000); lastTick = Stopwatch.GetTimestamp(); heartbeat.Start(); }
                            Call(dock, "ReturnToFocusDesk");
                            await Task.Delay(20);
                        }
                    }
                }
                heartbeat.Stop();
                if (!(bool)Field(main, "_timerRunning")! || !Equals(phaseStart, Field(main, "_phaseStart")) || !HardFocus.Active)
                    throw new Exception("Timer or Hard Focus state changed during switching");
            }
            finally
            {
                if (formal) focus.Exit();
                else
                {
                    dock.Close();
                    // Development Dock does not fit/track external placements; undo test activation itself.
                    if (!ownsFixture) SetWindowPlacement(externalFixture!.MainWindowHandle, ref originalPlacement);
                }
            }
            if (formal && (!IsWindowVisible(FindWindow("Shell_TrayWnd", null)) || focus.HasRecoveryResponsibility))
                throw new Exception("Formal taskbar/AppBar recovery was not confirmed");
            if (formal && originalTaskbarState != (int)typeof(TaskbarService).GetMethod("ReadShellState", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!)
                throw new Exception("Original taskbar auto-hide/always-on-top state was not restored");
            for (var attempt = 0; attempt < 30; attempt++)
            {
                var restoredPlacement = new NativePlacement { Length = Marshal.SizeOf<NativePlacement>() };
                if (GetWindowPlacement(externalFixture!.MainWindowHandle, ref restoredPlacement)
                    && restoredPlacement.ShowCommand == originalPlacement.ShowCommand
                    && restoredPlacement.NormalPosition.Equals(originalPlacement.NormalPosition)) break;
                if (attempt == 29) throw new Exception($"Original external placement was not restored: before={originalPlacement.ShowCommand}/{originalPlacement.NormalPosition.Left},{originalPlacement.NormalPosition.Top},{originalPlacement.NormalPosition.Right},{originalPlacement.NormalPosition.Bottom}; after={restoredPlacement.ShowCommand}/{restoredPlacement.NormalPosition.Left},{restoredPlacement.NormalPosition.Top},{restoredPlacement.NormalPosition.Right},{restoredPlacement.NormalPosition.Bottom}");
                await Task.Delay(16);
            }
            if (formal && ((GetWindowLongPtr(FindWindow("Shell_TrayWnd", null), -20).ToInt64() & 0x80000) != originalTaskbarLayered
                || GetProp(FindWindow("Shell_TrayWnd", null), "FocusDesk.Dock.TaskbarReveal.v1") != IntPtr.Zero))
                throw new Exception("Taskbar appearance or suppression snapshot was not restored");
            Console.WriteLine("PASS external original placement restored after exit");
            if (formal) Console.WriteLine("PASS formal exit: taskbar visible and original settings restored, AppBar/work area recovered, recovery marker clean");
            for (var index = 0; index < views.Length; index++)
            {
                if (!ReferenceEquals(views[index], web.Tabs[index].View)) throw new Exception("WebView recreated during return");
                var view = views[index]!;
                if (tokens[index] != await view.CoreWebView2.ExecuteScriptAsync("window.flowToken")) throw new Exception("Page reloaded");
                if (await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('#draft').value") != "\"preserved\"") throw new Exception("Draft lost");
                if (await view.CoreWebView2.ExecuteScriptAsync("document.cookie.includes('flow=preserved')") != "true") throw new Exception("Cookie lost");
            }
            static double Percentile(List<double> values, double fraction) => values.Order().ElementAt((int)((values.Count - 1) * fraction));
            var report = new { FormalMode = formal, SwitchCount = switches.Count, SwitchP95Ms = Percentile(switches, .95), SwitchMaxMs = switches.Max(), ExternalSwitchCount = externalSwitches.Count, ExternalSwitchP95Ms = Percentile(externalSwitches, .95), ExternalSwitchMaxMs = externalSwitches.Max(), HeartbeatP95Ms = Percentile(ticks, .95), HeartbeatMaxMs = ticks.Max(), WebViewsPreserved = true, PageDraftsCookiesPreserved = true, TimerAndHardFocusPreserved = true, PersistentBarsRetained = true, Mode = existingPath == null ? "Local pages and disposable full-screen fixture; switch handlers called directly" : "Local pages and explicitly selected existing application; switch handlers called directly" };
            Console.WriteLine(JsonSerializer.Serialize(report));
            if (report.SwitchP95Ms > 150 || report.HeartbeatP95Ms > 75 || report.SwitchMaxMs > 500 || report.HeartbeatMaxMs > 500 || report.ExternalSwitchMaxMs > 1000)
                throw new Exception("Synthetic ready-state responsiveness exceeded threshold");
            Result = 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
        finally
        {
            heartbeat.Stop();
            if (formal && focus.HasRecoveryResponsibility) focus.Recover();
            HardFocus.Release();
            if (externalFixture != null)
            {
                if (ownsFixture)
                {
                    externalFixture.CloseMainWindow();
                    if (!externalFixture.WaitForExit(3000)) externalFixture.Kill();
                }
                externalFixture.Dispose();
            }
            main?.Close();
            desktopLease?.Dispose();
            cancellation.Cancel();
            server.Stop();
            Shutdown();
        }
    }

    private static async Task WaitForEdgeSignal(string name)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(600))
        {
            var signal = Path.Combine(Paths.DataDir, name);
            if (File.Exists(signal) && File.ReadAllText(signal).Trim() == Environment.ProcessId.ToString()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Actual input observation was not completed");
    }

    private static async Task Serve(TcpListener listener, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(token);
                using var stream = client.GetStream();
                var buffer = new byte[8192];
                if (await stream.ReadAsync(buffer, token) == 0) continue;
                var body = "<!doctype html><input id='draft'><p>Synthetic local continuity test</p>";
                var response = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
                await stream.WriteAsync(response, token);
            }
        }
        catch (Exception) when (token.IsCancellationRequested) { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? name);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flag);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", ExactSpelling = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr hwnd, string name);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref NativePlacement placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hwnd, ref NativePlacement placement);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePlacement { public int Length, Flags, ShowCommand; public NativePoint Min, Max; public NativeRect NormalPosition; }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect rect, int size);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
}

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern bool AllowSetForegroundWindow(uint processId);
}
