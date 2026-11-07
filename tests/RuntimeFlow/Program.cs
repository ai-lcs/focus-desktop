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
// hook, with production recovery and watchdog. Never uses a user application.
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
                Title = "Disposable flow fixture", WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, WindowState = WindowState.Maximized,
                Content = new System.Windows.Controls.TextBox { Text = "Unsaved synthetic fixture draft" },
            };
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(120) };
            timeout.Tick += (_, _) => fixture.Close();
            timeout.Start();
            fixtureApp.Run(fixture);
            return 0;
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "portable.flag"), "");
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "runtime-progress.log"), "Main reached\n");
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(120));
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
        DesktopSessionLease? desktopLease = null;
        var formal = e.Args.Contains("--formal");
        var focus = new FocusModeService();
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
            if (e.Args.Contains("--hotkey-only"))
            {
                var type = typeof(MainWindow).Assembly.GetType("focus_desktop.DockWindow")!;
                var dockOnly = (Window)Activator.CreateInstance(type, main, focus, Array.Empty<ExternalAppEntry>(), new KeyboardGuardService(), false)!;
                try
                {
                    if (!(bool)Call(dockOnly, "TryShowDock", false)!) throw new Exception("Dock hotkey registration failed");
                    var handle = new WindowInteropHelper(main).Handle;
                    if (RegisterHotKey(handle, 0x5D02, 0x4003, 0x79))
                    {
                        UnregisterHotKey(handle, 0x5D02);
                        throw new Exception("Dock did not reserve hotkey");
                    }
                    var conflictingDock = (Window)Activator.CreateInstance(type, main, focus, Array.Empty<ExternalAppEntry>(), new KeyboardGuardService(), false)!;
                    try
                    {
                        if ((bool)Call(conflictingDock, "TryShowDock", false)!) throw new Exception("Conflicting Dock unexpectedly succeeded");
                    }
                    finally { conflictingDock.Close(); }
                    Call(dockOnly, "ReturnToFocusDesk");
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (!dockOnly.Topmost || !main.IsVisible) throw new Exception("Return did not restore Dock/main");
                }
                finally { dockOnly.Close(); }
                var target = new WindowInteropHelper(main).Handle;
                if (!RegisterHotKey(target, 0x5D02, 0x4003, 0x79)) throw new Exception("Return hotkey leaked after close");
                UnregisterHotKey(target, 0x5D02);
                Console.WriteLine("PASS native hotkey reservation, conflicting Dock refusal, return method, and release; no physical keyboard input");
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
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "FlowFixture.exe");
            File.Copy(Environment.ProcessPath!, fixturePath, true);
            var fixtureEntry = ExternalAppCatalog.Resolve(fixturePath);
            externalFixture = Process.Start(new ProcessStartInfo(fixturePath) { UseShellExecute = false });
            for (var attempt = 0; attempt < 100; attempt++)
            {
                externalFixture!.Refresh();
                if (externalFixture.MainWindowHandle != IntPtr.Zero) break;
                await Task.Delay(50);
            }
            var dockType = typeof(MainWindow).Assembly.GetType("focus_desktop.DockWindow")!;
            Window dock;
            if (formal)
            {
                if (!DesktopSessionCoordinator.TryAcquire(out desktopLease, out var reason)) throw new Exception(reason);
                settings.ExternalApps = new() { fixtureEntry };
                settings.Save();
                Call(main, "ConfigureDock", settings, true);
                focus.Enter();
                dock = (Window)Field(focus, "_dock")!;
                if (IsWindowVisible(FindWindow("Shell_TrayWnd", null))) throw new Exception("Formal taskbar did not hide");
                if (!GetWindowRect(new WindowInteropHelper(main).Handle, out var mainBounds)
                    || !GetWindowRect(new WindowInteropHelper(dock).Handle, out var dockBounds)) throw new Exception("Window bounds unavailable");
                var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
                if (dockBounds.Top != screen.Top || dockBounds.Bottom != screen.Bottom
                    || mainBounds.Top != screen.Top || mainBounds.Bottom != screen.Bottom
                    || mainBounds.Left != dockBounds.Right || mainBounds.Right != screen.Right)
                    throw new Exception("Formal main/Dock leave a gap or overlap");
                Console.WriteLine($"PASS formal native bounds: Dock bottom={dockBounds.Bottom}, main bottom={mainBounds.Bottom}, screen bottom={screen.Bottom}; system taskbar hidden");
            }
            else dock = (Window)Activator.CreateInstance(dockType, main, focus, new[] { fixtureEntry }, new KeyboardGuardService(), false)!;
            try
            {
                if (!formal && !(bool)Call(dock, "TryShowDock", false)!) throw new Exception("Dock/hotkey registration failed");
                if (e.Args.Contains("--observe"))
                {
                    var returnReceived = false;
                    HwndSourceHook observeHotkey = (IntPtr window, int message, IntPtr key, IntPtr parameter, ref bool handled) =>
                    {
                        if (message == 0x0312 && key.ToInt32() == 0x5D01)
                        {
                            returnReceived = true;
                            Console.WriteLine("OBSERVE: native WM_HOTKEY received by test Dock");
                        }
                        return IntPtr.Zero;
                    };
                    var source = HwndSource.FromHwnd(new WindowInteropHelper(dock).Handle);
                    source.AddHook(observeHotkey);
                    await (Task)Call(dock, "ActivateEntryAsync", fixtureEntry)!;
                    Console.WriteLine("OBSERVE: disposable full-screen fixture active; waiting 60 seconds for physical Ctrl+Alt+F10");
                    await Task.Delay(60000);
                    source.RemoveHook(observeHotkey);
                    if (!returnReceived || !main.IsActive || !dock.Topmost)
                        throw new Exception($"Physical return not confirmed: hotkeyReceived={returnReceived}, mainActive={main.IsActive}, dockTopmost={dock.Topmost}");
                    Console.WriteLine("PASS observed physical hotkey returned to Focus Desk and restored Dock");
                }
                var hwnd = new WindowInteropHelper(dock).Handle;
                if (RegisterHotKey(hwnd, 0x5D02, 0x4003, 0x79))
                {
                    UnregisterHotKey(hwnd, 0x5D02);
                    throw new Exception("Return hotkey is not reserved by Dock");
                }
                Call(main, "ToggleTimer");
                var phaseStart = Field(main, "_phaseStart");
                HardFocus.Enable(true);
                lastTick = Stopwatch.GetTimestamp();
                heartbeat.Start();
                for (var cycle = 0; cycle < 40; cycle++)
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
                            if (!((ExternalAppWindowService)Field(dock, "_windows")!).IsExternalWindowForeground)
                                throw new Exception("Native fixture activation was refused or lost");
                            externalSwitches.Add(Stopwatch.GetElapsedTime(externalStart).TotalMilliseconds);
                            Call(dock, "ReturnToFocusDesk");
                            await Task.Delay(20);
                        }
                    }
                }
                heartbeat.Stop();
                if (!(bool)Field(main, "_timerRunning")! || !Equals(phaseStart, Field(main, "_phaseStart")) || !HardFocus.Active)
                    throw new Exception("Timer or Hard Focus state changed during switching");
            }
            finally { if (formal) focus.Exit(); else dock.Close(); }
            if (formal && (!IsWindowVisible(FindWindow("Shell_TrayWnd", null)) || focus.HasRecoveryResponsibility))
                throw new Exception("Formal taskbar/AppBar recovery was not confirmed");
            if (formal) Console.WriteLine("PASS formal exit: taskbar visible, AppBar/work area recovered, recovery marker clean");
            var releaseWindow = new WindowInteropHelper(main).Handle;
            if (!RegisterHotKey(releaseWindow, 0x5D02, 0x4003, 0x79)) throw new Exception("Dock leaked the return hotkey");
            UnregisterHotKey(releaseWindow, 0x5D02);
            for (var index = 0; index < views.Length; index++)
            {
                if (!ReferenceEquals(views[index], web.Tabs[index].View)) throw new Exception("WebView recreated during return");
                var view = views[index]!;
                if (tokens[index] != await view.CoreWebView2.ExecuteScriptAsync("window.flowToken")) throw new Exception("Page reloaded");
                if (await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('#draft').value") != "\"preserved\"") throw new Exception("Draft lost");
                if (await view.CoreWebView2.ExecuteScriptAsync("document.cookie.includes('flow=preserved')") != "true") throw new Exception("Cookie lost");
            }
            static double Percentile(List<double> values, double fraction) => values.Order().ElementAt((int)((values.Count - 1) * fraction));
            var report = new { FormalMode = formal, PhysicalHotkeyCheck = e.Args.Contains("--observe"), SwitchCount = switches.Count, SwitchP95Ms = Percentile(switches, .95), SwitchMaxMs = switches.Max(), ExternalSwitchCount = externalSwitches.Count, ExternalSwitchP95Ms = Percentile(externalSwitches, .95), ExternalSwitchMaxMs = externalSwitches.Max(), HeartbeatP95Ms = Percentile(ticks, .95), HeartbeatMaxMs = ticks.Max(), WebViewsPreserved = true, PageDraftsCookiesPreserved = true, TimerAndHardFocusPreserved = true, HotkeyReservedAndReleased = true, Mode = "Local pages and disposable full-screen fixture; switch handlers called directly; observe mode additionally requires native hotkey input" };
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
                externalFixture.CloseMainWindow();
                if (!externalFixture.WaitForExit(3000)) externalFixture.Kill();
                externalFixture.Dispose();
            }
            main?.Close();
            desktopLease?.Dispose();
            cancellation.Cancel();
            server.Stop();
            Shutdown();
        }
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

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? name);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect bounds);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
}
