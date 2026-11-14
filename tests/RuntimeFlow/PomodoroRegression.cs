using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using focus_desktop;
using focus_desktop.Services;

internal static class PomodoroRegression
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static int Run()
    {
        try
        {
            foreach (var preview in new[] { false, true })
            foreach (var longBreak in new[] { false, true })
                CheckCycle(preview, longBreak);
            Console.WriteLine("PASS all four Hard Focus completion/pause/resume scenarios");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void CheckCycle(bool preview, bool longBreak)
    {
        var control = new PomodoroControl();
        T Field<T>(string name) => (T)typeof(PomodoroControl).GetField(name, Private)!.GetValue(control)!;
        void Call(string name) => typeof(PomodoroControl).GetMethod(name, Private)!.Invoke(control, null);
        var service = Field<PomodoroService>("_svc");
        var pause = Field<System.Windows.Controls.Button>("_btnPause");
        void ClickPause() => pause.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception($"preview={preview}, longBreak={longBreak}: {message}");
        }
        try
        {
            control.SetPreviewMode(preview);
            Call("ToggleHardFocus");
            service.WorkMinutes = 1;
            service.CyclesUntilLong = longBreak ? 1 : 4;
            Call("Start");
            Check(!pause.IsEnabled && pause.Opacity < 1, "pause must be disabled during Hard Focus work");
            ClickPause();
            Check(service.IsRunning, "the Hard Focus guard must reject pause");

            // Advance the production timer through completion without waiting or playing its completion beep.
            typeof(PomodoroControl).GetField("_lastFinished", Private)!.SetValue(control, true);
            typeof(PomodoroService).GetField("_phaseStart", Private)!.SetValue(service, DateTime.Now.AddMinutes(-2));
            service.OnSecond();
            Check(service.CurrentPhase == (longBreak ? PomodoroService.Phase.LongBreak : PomodoroService.Phase.ShortBreak), "work must advance to the expected break");
            Check(!HardFocus.Active && !Field<bool>("_hardWanted"), "Hard Focus must release on completion");
            Check(pause.IsEnabled && pause.Opacity == 1, "pause must be enabled and fully visible after Hard Focus completes");
            ClickPause();
            Check(!service.IsRunning && (string)pause.Content == "继续", "break must pause through the button");
            ClickPause();
            Check(service.IsRunning && (string)pause.Content == "暂停", "break must resume through the button");

            service.Skip();
            Check(service.CurrentPhase == PomodoroService.Phase.Work && pause.IsEnabled, "next ordinary work must allow pause");
            ClickPause();
            Check(!service.IsRunning, "next ordinary work must pause through the button");
            ClickPause();
            Check(service.IsRunning, "next ordinary work must resume through the button");
            Console.WriteLine($"PASS preview={preview}, longBreak={longBreak}: lock, completion, break pause/resume, next-work pause/resume");
        }
        finally
        {
            service.Reset();
            Field<DispatcherTimer>("_uiTimer").Stop();
            HardFocus.Release();
        }
    }
}
