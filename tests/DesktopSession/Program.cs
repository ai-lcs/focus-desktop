using focus_desktop.Services;
using System.Diagnostics;

if (args.Length == 2 && args[0] == "--mutex-probe")
    return ProbeMutex(args[1]);

var failures = new List<string>();
Run("taskbar retry failure preserves dirty recovery marker", TestTaskbarFailure);
Run("watchdog rechecks generation before committing clean", TestGenerationChangesBeforeClean);
Run("watchdog rechecks generation before showing taskbar", TestGenerationChangesBeforeShow);
Run("watchdog restores and commits current session", TestValidRecovery);
Run("session mutex ownership is cross-process and thread affine", TestMutexOwnership);
Run("abandoned mutex ownership is reported after acquisition", TestAbandonedMutex);
Console.WriteLine($"{(failures.Count == 0 ? "PASS" : "FAIL")}: {6 - failures.Count}/6");
foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

void Run(string name, Action test)
{
    try { test(); }
    catch (Exception ex) { failures.Add($"{name}: {ex.Message}"); }
}

void TestTaskbarFailure()
{
    var attempts = 0;
    var cleaned = 0;
    var session = new RecoverySessionSnapshot(true, "generation", 123, 456);
    var result = DesktopSessionRecoveryProtocol.TryRunWatchdogRecovery(
        () => session,
        "generation", 123, 456,
        (_, _) => true,
        () => TaskbarRestorePolicy.TryRestore(_ =>
        {
            attempts++;
            return false;
        }),
        _ => true,
        _ => { cleaned++; return true; });
    Equal(false, result);
    Equal(3, attempts);
    Equal(0, cleaned);
}

void TestGenerationChangesBeforeClean()
{
    var oldSession = new RecoverySessionSnapshot(true, "old-generation", 123, 456);
    var newSession = new RecoverySessionSnapshot(true, "new-generation", 999, 1000);
    var reads = 0;
    var shown = 0;
    var confirmed = 0;
    var cleaned = 0;
    var result = DesktopSessionRecoveryProtocol.TryRunWatchdogRecovery(
        () => ++reads <= 3 ? oldSession : newSession,
        "old-generation", 123, 456,
        (_, _) => true,
        () => { shown++; return true; },
        _ => { confirmed++; return true; },
        _ => { cleaned++; return true; });
    Equal(false, result);
    Equal(1, shown);
    Equal(1, confirmed);
    Equal(0, cleaned);
}

void TestGenerationChangesBeforeShow()
{
    var oldSession = new RecoverySessionSnapshot(true, "old-generation", 123, 456);
    var newSession = new RecoverySessionSnapshot(true, "new-generation", 999, 1000);
    var reads = 0;
    var shown = 0;
    var cleaned = 0;
    var result = DesktopSessionRecoveryProtocol.TryRunWatchdogRecovery(
        () => ++reads <= 1 ? oldSession : newSession,
        "old-generation", 123, 456,
        (_, _) => true,
        () => { shown++; return true; },
        _ => true,
        _ => { cleaned++; return true; });
    Equal(false, result);
    Equal(0, shown);
    Equal(0, cleaned);
}

void TestValidRecovery()
{
    var session = new RecoverySessionSnapshot(true, "generation", 123, 456);
    var shown = 0;
    var confirmed = 0;
    var cleaned = 0;
    var result = DesktopSessionRecoveryProtocol.TryRunWatchdogRecovery(
        () => session,
        "generation", 123, 456,
        (pid, ticks) => pid == 123 && ticks == 456,
        () => { shown++; return true; },
        _ => { confirmed++; return true; },
        _ => { cleaned++; return true; });
    Equal(true, result);
    Equal(1, shown);
    Equal(1, confirmed);
    Equal(1, cleaned);
}

void TestMutexOwnership()
{
    var name = $"focus-desktop-ownership-test-{Guid.NewGuid():N}";
    if (!DesktopSessionCoordinator.TryAcquireNamed(name, out var lease, out var reason))
        throw new InvalidOperationException(reason);
    var heldLease = lease!;
    using (heldLease)
    {
        Equal(true, DesktopSessionCoordinator.IsHeldByCurrentThread);
        Equal(2, RunMutexProbe(name));

        Exception? crossThreadFailure = null;
        var releaseThread = new Thread(() =>
        {
            try { heldLease.Dispose(); }
            catch (Exception ex) { crossThreadFailure = ex; }
        });
        releaseThread.Start();
        releaseThread.Join();
        if (crossThreadFailure is not InvalidOperationException)
            throw new InvalidOperationException("cross-thread release was not rejected");
        Equal(true, heldLease.IsHeldByCurrentThread);
    }
    Equal(0, RunMutexProbe(name));

    if (!DesktopSessionCoordinator.TryAcquireNamed(name, out var reopened, out reason))
        throw new InvalidOperationException(reason);
    var reopenedLease = reopened!;
    using (reopenedLease) Equal(true, reopenedLease.IsHeldByCurrentThread);
    Equal(0, RunMutexProbe(name));
}

void TestAbandonedMutex()
{
    var name = $"focus-desktop-abandoned-test-{Guid.NewGuid():N}";
    using var abandonedMutex = new Mutex(false, name);
    var worker = new Thread(() =>
    {
        if (!abandonedMutex.WaitOne(0)) throw new InvalidOperationException("test owner did not acquire mutex");
        // The thread exits without ReleaseMutex so WaitOne reports abandonment to the next owner.
    });
    worker.Start();
    worker.Join();

    if (!DesktopSessionCoordinator.TryAcquireNamed(name, out var lease, out var reason))
        throw new InvalidOperationException(reason);
    var abandonedLease = lease!;
    using (abandonedLease) Equal(true, abandonedLease.WasAbandoned);
}

int RunMutexProbe(string name)
{
    var processPath = Environment.ProcessPath
        ?? throw new InvalidOperationException("current test executable path is unavailable");
    using var process = Process.Start(new ProcessStartInfo
    {
        FileName = processPath,
        Arguments = $"--mutex-probe {name}",
        UseShellExecute = false,
        CreateNoWindow = true,
    }) ?? throw new InvalidOperationException("could not start mutex probe process");
    process.WaitForExit();
    return process.ExitCode;
}

int ProbeMutex(string name)
{
    if (!DesktopSessionCoordinator.TryAcquireNamed(name, out var lease, out _)) return 2;
    using (lease!) return 0;
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"expected {expected}, got {actual}");
}
