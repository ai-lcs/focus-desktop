using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace focus_desktop.Services;

public readonly record struct ExternalWindowIdentity(string? AppUserModelId, string? ExecutablePath);

/// <summary>Tracks which Dock launch request is still allowed to activate a window.</summary>
public sealed class DockSwitchIntentGate
{
    private readonly object _gate = new();
    private long _version;

    public long CurrentVersion
    {
        get { lock (_gate) return _version; }
    }

    public long Begin(string entryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        lock (_gate) return ++_version;
    }

    public bool IsCurrent(long token)
    {
        lock (_gate) return token == _version;
    }

    public void Cancel()
    {
        lock (_gate) _version++;
    }
}

/// <summary>Merges concurrent launch-and-wait requests for the same frozen entry.</summary>
public sealed class DockLaunchRequests
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Task<IntPtr?>> _pending = new(StringComparer.OrdinalIgnoreCase);

    public Task<IntPtr?> GetOrStart(string entryId, Func<Task<IntPtr?>> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        ArgumentNullException.ThrowIfNull(factory);

        lock (_gate)
        {
            if (_pending.TryGetValue(entryId, out var existing)) return existing;

            var completion = new TaskCompletionSource<IntPtr?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = completion.Task.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _pending.Add(entryId, completion.Task);
            _ = Task.Run(() => CompleteAsync(entryId, completion, factory));
            return completion.Task;
        }
    }

    private async Task CompleteAsync(
        string entryId,
        TaskCompletionSource<IntPtr?> completion,
        Func<Task<IntPtr?>> factory)
    {
        try
        {
            var result = await factory().ConfigureAwait(false);
            CompleteAndRemove(entryId, completion.Task, () => completion.TrySetResult(result));
        }
        catch (OperationCanceledException ex)
        {
            CompleteAndRemove(entryId, completion.Task, () => completion.TrySetCanceled(ex.CancellationToken));
        }
        catch (Exception ex)
        {
            CompleteAndRemove(entryId, completion.Task, () => completion.TrySetException(ex));
        }
    }

    private void CompleteAndRemove(string entryId, Task<IntPtr?> task, Action complete)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(entryId, out var current)
                && ReferenceEquals(current, task))
            {
                _pending.Remove(entryId);
            }
            complete();
        }
    }
}

public sealed record ExternalWindowSwitchResult(bool Activated, bool Superseded, string? Error)
{
    public long IntentToken { get; init; }
}

/// <summary>
/// Finds top-level application windows by frozen AUMID or executable path and remembers
/// foreground windows for the low-level keyboard guard. Window titles and process names
/// are deliberately not used as identity.
/// </summary>
public sealed class ExternalAppWindowService : IDisposable
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;
    private const int EventSystemForeground = 0x0003;
    private const int EventSystemMinimizeStart = 0x0016;
    private const int EventObjectDestroy = 0x8001;
    private const int ObjectIdWindow = 0;
    private const int ChildIdSelf = 0;
    private const uint WineventOutOfContext = 0x0000;
    private const uint GwOwner = 4;
    private const int GwExStyle = -20;
    private const long WsExNoActivate = 0x08000000L;
    private const int DwmwaCloaked = 14;
    private const int SwRestore = 9;
    private const int AppModelIdPropertyId = 5;

    private static readonly Guid PropertyStoreInterfaceId =
        new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly PropertyKey AppModelIdPropertyKey = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), AppModelIdPropertyId);

    private readonly IReadOnlyList<ExternalAppEntry> _entries;
    private readonly KeyboardGuardService _keyboard;
    private readonly DockSwitchIntentGate _intents = new();
    private readonly DockLaunchRequests _launches = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _windowStateGate = new();
    private readonly HashSet<IntPtr> _knownWindows = new();
    private readonly Dictionary<string, IntPtr> _recentWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly WinEventProc _winEventProc;
    private IntPtr _foregroundHook;
    private IntPtr _minimizeHook;
    private IntPtr _destroyHook;
    private string? _activeEntryId;
    private HashSet<IntPtr> _activeOwnerChain = new();
    private long _activeWindowRevision;
    private volatile bool _disposed;
    private long _foregroundEventVersion;
    private readonly ExternalWindowLayout _layout = new();
    internal DesktopRect? Viewport { get; set; }

    internal void CaptureCurrentPlacements()
    {
        var cache = new Dictionary<uint, ExternalWindowIdentity>();
        EnumWindows((hwnd, parameter) =>
        {
            if (!IsWindowVisible(hwnd) || IsCloaked(hwnd) || !IsEligibleWindow(hwnd)
                || GetWindow(hwnd, GwOwner) != IntPtr.Zero) return true;
            foreach (var entry in _entries)
            {
                if (!TryGetOwnerChain(hwnd, entry, cache, out var identities, out _)
                    || !MatchesOwnerChain(entry, identities)) continue;
                _layout.Capture(hwnd);
                break;
            }
            return true;
        }, IntPtr.Zero);
    }

    public ExternalAppWindowService(IReadOnlyList<ExternalAppEntry> entries, KeyboardGuardService keyboard)
    {
        _entries = entries.OrderBy(entry => entry.Order).ToArray();
        _keyboard = keyboard;
        _winEventProc = OnWinEvent;

        _foregroundHook = SetWinEventHook(
            EventSystemForeground, EventSystemForeground, IntPtr.Zero, _winEventProc, 0, 0, WineventOutOfContext);
        if (_foregroundHook == IntPtr.Zero)
            throw new InvalidOperationException($"无法监听应用窗口切换，Win32 错误码 {Marshal.GetLastWin32Error()}。");

        _minimizeHook = SetWinEventHook(
            EventSystemMinimizeStart, EventSystemMinimizeStart, IntPtr.Zero, _winEventProc, 0, 0, WineventOutOfContext);
        if (_minimizeHook == IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
            throw new InvalidOperationException($"无法监听应用窗口最小化，Win32 错误码 {Marshal.GetLastWin32Error()}。");
        }

        _destroyHook = SetWinEventHook(
            EventObjectDestroy, EventObjectDestroy, IntPtr.Zero, _winEventProc, 0, 0, WineventOutOfContext);
        if (_destroyHook == IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            UnhookWinEvent(_minimizeHook);
            _foregroundHook = IntPtr.Zero;
            _minimizeHook = IntPtr.Zero;
            throw new InvalidOperationException($"无法监听应用窗口关闭，Win32 错误码 {Marshal.GetLastWin32Error()}。");
        }
    }

    public bool IsExternalWindowForeground
    {
        get
        {
            var foreground = GetForegroundWindow();
            lock (_windowStateGate) return foreground != IntPtr.Zero && _knownWindows.Contains(foreground);
        }
    }

    public event Action<long, long>? ActiveExternalWindowUnavailable;

    public static bool IdentityMatches(ExternalAppEntry entry, ExternalWindowIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!string.IsNullOrWhiteSpace(entry.AppUserModelId))
        {
            return !string.IsNullOrWhiteSpace(identity.AppUserModelId)
                && string.Equals(entry.AppUserModelId.Trim(), identity.AppUserModelId.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        return !string.Equals(entry.Kind, ExternalAppEntry.PackagedAppKind, StringComparison.Ordinal)
            && PathsEqual(entry.ExecutablePath, identity.ExecutablePath);
    }

    public static bool MatchesOwnerChain(ExternalAppEntry entry, IReadOnlyList<ExternalWindowIdentity> ownerChain)
    {
        ArgumentNullException.ThrowIfNull(ownerChain);
        return ownerChain.Any(identity => IdentityMatches(entry, identity));
    }

    public long BeginIntent(string entryId) => _intents.Begin(entryId);

    public bool IsCurrentIntent(long intent) => !_disposed && _intents.IsCurrent(intent);

    public bool IsCurrentUnavailable(long revision, long intent)
    {
        if (_disposed || !_intents.IsCurrent(intent)) return false;
        lock (_windowStateGate)
        {
            if (_activeWindowRevision != revision || _activeEntryId != null) return false;
        }

        var foreground = GetForegroundWindow();
        foreach (var entry in _entries)
        {
            var processCache = new Dictionary<uint, ExternalWindowIdentity>();
            if (TryGetOwnerChain(foreground, entry, processCache, out var identities, out _)
                && MatchesOwnerChain(entry, identities)) return false;
        }
        return _intents.IsCurrent(intent);
    }

    public Task<ExternalWindowSwitchResult> ActivateAsync(ExternalAppEntry entry) =>
        ActivateAsync(entry, BeginIntent(entry.Id));

    public async Task<ExternalWindowSwitchResult> ActivateAsync(ExternalAppEntry entry, long intent)
    {
        if (_disposed) return MakeResult(false, true, null, intent);
        if (IsFocusDeskExecutable(entry.ExecutablePath))
            return MakeResult(false, false, "不能把 Focus Desk 自身作为外部应用启动。", intent);

        try
        {
            var window = await Task.Run(() => FindWindow(entry), _lifetime.Token).ConfigureAwait(false);
            if (!_intents.IsCurrent(intent)) return MakeResult(false, true, null, intent);
            if (window == IntPtr.Zero)
            {
                var launchTask = _launches.GetOrStart(entry.Id, () => LaunchAndWaitAsync(entry, _lifetime.Token));
                while (!launchTask.IsCompleted)
                {
                    if (!_intents.IsCurrent(intent)) return MakeResult(false, true, null, intent);
                    await Task.WhenAny(launchTask, Task.Delay(100, _lifetime.Token)).ConfigureAwait(false);
                }
                window = await launchTask.ConfigureAwait(false) ?? IntPtr.Zero;
            }

            if (!_intents.IsCurrent(intent)) return MakeResult(false, true, null, intent);
            if (window == IntPtr.Zero)
                return MakeResult(false, false, "等待应用窗口超时。可以稍后再次点击重试。", intent);

            var current = await Task.Run(() => FindWindow(entry, window), _lifetime.Token).ConfigureAwait(false);
            if (!_intents.IsCurrent(intent)) return MakeResult(false, true, null, intent);
            if (current == IntPtr.Zero)
                return MakeResult(false, false, "应用窗口已关闭或身份发生变化。可以再次点击重试。", intent);

            var attempt = await ActivateOnDispatcherAsync(entry, current, intent).ConfigureAwait(false);
            return MakeResult(attempt.Activated, attempt.Superseded, attempt.Error, intent);
        }
        catch (OperationCanceledException)
        {
            return MakeResult(false, true, null, intent);
        }
        catch (Exception ex)
        {
            return _intents.IsCurrent(intent)
                ? MakeResult(false, false, ex.Message, intent)
                : MakeResult(false, true, null, intent);
        }
    }

    private async Task<ActivationAttempt> ActivateOnDispatcherAsync(
        ExternalAppEntry entry, IntPtr hwnd, long intent)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
            return new(false, false, "Focus Desk 界面已退出，无法切换窗口。");

        var operation = dispatcher.InvokeAsync(async () =>
        {
            if (_disposed || !_intents.IsCurrent(intent)) return new ActivationAttempt(false, true, null);
            if (!IsWindow(hwnd)) return new ActivationAttempt(false, false, "应用窗口已关闭。可以再次点击重试。");
            if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SwRestore);
            if (Viewport is { } viewport && !await _layout.FitAsync(hwnd, viewport))
                return new ActivationAttempt(false, false, "应用未能完整铺满工作区，请检查窗口大小或权限。");
            if (_disposed || !_intents.IsCurrent(intent)) return new ActivationAttempt(false, true, null);
            if (!SetForegroundWindow(hwnd) && GetForegroundWindow() != hwnd)
                return new ActivationAttempt(false, false, "Windows 暂未允许切换到该窗口。请再点击一次应用入口。");
            RecordForegroundWindow(entry, hwnd);
            return new ActivationAttempt(true, false, null);
        }, DispatcherPriority.Input);

        return await (await operation.Task.ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static ExternalWindowSwitchResult MakeResult(
        bool activated, bool superseded, string? error, long intent) =>
        new(activated, superseded, error) { IntentToken = intent };

    private readonly record struct ActivationAttempt(bool Activated, bool Superseded, string? Error);

    public void CancelRequests() => _intents.Cancel();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _intents.Cancel();
        _lifetime.Cancel();
        _layout.Dispose();
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        if (_minimizeHook != IntPtr.Zero) UnhookWinEvent(_minimizeHook);
        if (_destroyHook != IntPtr.Zero) UnhookWinEvent(_destroyHook);
        _foregroundHook = IntPtr.Zero;
        _minimizeHook = IntPtr.Zero;
        _destroyHook = IntPtr.Zero;
        lock (_windowStateGate)
        {
            _knownWindows.Clear();
            _recentWindows.Clear();
            _activeOwnerChain.Clear();
            _activeEntryId = null;
            _activeWindowRevision++;
            PublishKnownWindowsLocked();
        }
        _lifetime.Dispose();
    }

    private async Task<IntPtr?> LaunchAndWaitAsync(ExternalAppEntry entry, CancellationToken cancellationToken)
    {
        using (await Task.Run(() => ExternalAppCatalog.Launch(entry), cancellationToken).ConfigureAwait(false)) { }

        var stopAt = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < stopAt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hwnd = await Task.Run(() => FindWindow(entry), cancellationToken).ConfigureAwait(false);
            if (hwnd != IntPtr.Zero) return hwnd;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private IntPtr FindWindow(ExternalAppEntry entry, IntPtr preferred = default, bool includeMinimized = true)
    {
        var processCache = new Dictionary<uint, ExternalWindowIdentity>();
        var candidates = new List<(IntPtr Handle, bool Modal, bool Recent, bool Minimized, List<IntPtr> Chain)>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsCloaked(hwnd) || !IsEligibleWindow(hwnd)) return true;
            if (!includeMinimized && IsIconic(hwnd)) return true;
            if (!TryGetOwnerChain(hwnd, entry, processCache, out var identities, out var handles)) return true;
            if (!MatchesOwnerChain(entry, identities)) return true;

            var modal = HasDisabledOwner(hwnd);
            bool recent;
            lock (_windowStateGate)
                recent = hwnd == preferred
                    || (_recentWindows.TryGetValue(entry.Id, out var previous) && previous == hwnd);
            candidates.Add((hwnd, modal, recent, IsIconic(hwnd), handles));
            return true;
        }, IntPtr.Zero);

        var selected = candidates
            .OrderByDescending(item => item.Modal)
            .ThenByDescending(item => item.Recent)
            .ThenBy(item => item.Minimized)
            .Select(item => (IntPtr?)item.Handle)
            .FirstOrDefault();
        if (selected is not IntPtr result) return IntPtr.Zero;

        var chosen = candidates.First(item => item.Handle == result);
        lock (_windowStateGate)
        {
            foreach (var handle in chosen.Chain) _knownWindows.Add(handle);
            PublishKnownWindowsLocked();
        }
        return result;
    }

    private bool TryGetOwnerChain(
        IntPtr hwnd,
        ExternalAppEntry entry,
        Dictionary<uint, ExternalWindowIdentity> processCache,
        out List<ExternalWindowIdentity> identities,
        out List<IntPtr> handles)
    {
        identities = new List<ExternalWindowIdentity>();
        handles = new List<IntPtr>();
        var current = hwnd;
        var visited = new HashSet<IntPtr>();
        for (var depth = 0; current != IntPtr.Zero && depth < 12 && visited.Add(current); depth++)
        {
            handles.Add(current);
            GetWindowThreadProcessId(current, out var processId);
            if (processId != 0)
            {
                if (!processCache.TryGetValue(processId, out var processIdentity))
                {
                    processIdentity = GetProcessIdentity(processId);
                    processCache[processId] = processIdentity;
                }

                var appUserModelId = processIdentity.AppUserModelId;
                if (!string.IsNullOrWhiteSpace(entry.AppUserModelId)
                    && !string.Equals(appUserModelId, entry.AppUserModelId, StringComparison.OrdinalIgnoreCase))
                {
                    appUserModelId = ReadWindowAppUserModelId(current) ?? appUserModelId;
                }
                identities.Add(new ExternalWindowIdentity(appUserModelId, processIdentity.ExecutablePath));
            }

            current = GetWindow(current, GwOwner);
        }

        return identities.Count > 0;
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (eventType == EventSystemForeground)
        {
            var version = Interlocked.Increment(ref _foregroundEventVersion);
            // Process identities and Shell property stores can be slow. Never run
            // them on the thread delivering window events (the WPF UI thread).
            _ = Task.Run(() => ProcessWindowEvent(hook, eventType, hwnd, objectId, childId,
                eventThread, eventTime, version));
            return;
        }
        ProcessWindowEvent(hook, eventType, hwnd, objectId, childId, eventThread, eventTime, 0);
    }

    private void ProcessWindowEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId,
        int childId, uint eventThread, uint eventTime, long foregroundVersion)
    {
        try
        {
            HandleWinEvent(hook, eventType, hwnd, objectId, childId, eventThread, eventTime, foregroundVersion);
        }
        catch (Exception ex)
        {
            if (foregroundVersion != 0 && foregroundVersion != Volatile.Read(ref _foregroundEventVersion)) return;
            CrashReporter.Write(ex, "external-app-winevent");
            FailClosedAfterWinEventError();
        }
    }

    private void HandleWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint eventThread, uint eventTime,
        long foregroundVersion)
    {
        if (_disposed || hwnd == IntPtr.Zero || objectId != ObjectIdWindow || childId != ChildIdSelf) return;

        if (eventType is EventObjectDestroy or EventSystemMinimizeStart)
        {
            var intent = _intents.CurrentVersion;
            string? activeEntryId;
            long revision;
            lock (_windowStateGate)
            {
                if (eventType == EventObjectDestroy)
                {
                    _knownWindows.Remove(hwnd);
                    foreach (var key in _recentWindows.Where(pair => pair.Value == hwnd).Select(pair => pair.Key).ToArray())
                        _recentWindows.Remove(key);
                    PublishKnownWindowsLocked();
                }
                if (!_activeOwnerChain.Contains(hwnd)) return;
                activeEntryId = _activeEntryId;
                revision = _activeWindowRevision;
            }
            if (activeEntryId != null) ScheduleActiveWindowCheck(activeEntryId, revision, intent, hwnd);
            return;
        }

        if (eventType != EventSystemForeground) return;
        if (foregroundVersion != Volatile.Read(ref _foregroundEventVersion) || GetForegroundWindow() != hwnd) return;
        var foregroundProcessCache = new Dictionary<uint, ExternalWindowIdentity>();
        foreach (var entry in _entries)
        {
            if (!TryGetOwnerChain(hwnd, entry, foregroundProcessCache, out var identities, out var handles)
                || !MatchesOwnerChain(entry, identities)) continue;

            lock (_windowStateGate)
            {
                if (_disposed || foregroundVersion != Volatile.Read(ref _foregroundEventVersion)
                    || GetForegroundWindow() != hwnd) return;
                _recentWindows[entry.Id] = hwnd;
                foreach (var handle in handles) _knownWindows.Add(handle);
                _activeEntryId = entry.Id;
                _activeOwnerChain = handles.ToHashSet();
                _activeWindowRevision++;
                PublishKnownWindowsLocked();
            }
            return;
        }

        string? previousEntry;
        long previousRevision;
        var currentIntent = _intents.CurrentVersion;
        lock (_windowStateGate)
        {
            if (_disposed || foregroundVersion != Volatile.Read(ref _foregroundEventVersion)
                || GetForegroundWindow() != hwnd) return;
            previousEntry = _activeEntryId;
            previousRevision = _activeWindowRevision;
        }
        if (previousEntry != null) ScheduleActiveWindowCheck(previousEntry, previousRevision, currentIntent, IntPtr.Zero);
    }

    private void FailClosedAfterWinEventError()
    {
        if (_disposed) return;
        try
        {
            lock (_windowStateGate)
            {
                _knownWindows.Clear();
                _recentWindows.Clear();
                _activeEntryId = null;
                _activeOwnerChain.Clear();
                _activeWindowRevision++;
                PublishKnownWindowsLocked();
            }
        }
        catch (Exception ex)
        {
            CrashReporter.Write(ex, "external-app-winevent-fail-closed");
        }
    }

    private void ScheduleActiveWindowCheck(string entryId, long revision, long intent, IntPtr unavailableWindow)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(160).ConfigureAwait(false);
            if (_disposed || !_intents.IsCurrent(intent)) return;

            ExternalAppEntry? entry;
            lock (_windowStateGate)
            {
                if (_activeWindowRevision != revision || !string.Equals(_activeEntryId, entryId, StringComparison.OrdinalIgnoreCase))
                    return;
                entry = _entries.FirstOrDefault(item => string.Equals(item.Id, entryId, StringComparison.OrdinalIgnoreCase));
                if (unavailableWindow == IntPtr.Zero && _activeOwnerChain.Any(hwnd => IsWindow(hwnd) && !IsIconic(hwnd)))
                {
                    _activeEntryId = null;
                    _activeOwnerChain.Clear();
                    _activeWindowRevision++;
                    return;
                }
            }
            if (entry == null) return;

            var foreground = GetForegroundWindow();
            var processCache = new Dictionary<uint, ExternalWindowIdentity>();
            if (foreground != IntPtr.Zero
                && TryGetOwnerChain(foreground, entry, processCache, out var identities, out _)
                && MatchesOwnerChain(entry, identities))
            {
                RecordForegroundWindow(entry, foreground);
                return;
            }

            long unavailableRevision;
            lock (_windowStateGate)
            {
                if (_activeWindowRevision != revision
                    || !string.Equals(_activeEntryId, entryId, StringComparison.OrdinalIgnoreCase)
                    || !_intents.IsCurrent(intent))
                    return;
                _activeEntryId = null;
                _activeOwnerChain.Clear();
                unavailableRevision = ++_activeWindowRevision;
            }
            ActiveExternalWindowUnavailable?.Invoke(unavailableRevision, intent);
        });
    }

    private void PublishKnownWindowsLocked() =>
        _keyboard.ReplaceAllowedExternalWindows(_knownWindows.ToArray());

    private void RecordForegroundWindow(ExternalAppEntry entry, IntPtr hwnd)
    {
        var processCache = new Dictionary<uint, ExternalWindowIdentity>();
        if (!TryGetOwnerChain(hwnd, entry, processCache, out var identities, out var handles)
            || !MatchesOwnerChain(entry, identities)) return;
        lock (_windowStateGate)
        {
            _recentWindows[entry.Id] = hwnd;
            foreach (var handle in handles) _knownWindows.Add(handle);
            _activeEntryId = entry.Id;
            _activeOwnerChain = handles.ToHashSet();
            _activeWindowRevision++;
            PublishKnownWindowsLocked();
        }
    }

    private static bool IsEligibleWindow(IntPtr hwnd)
    {
        if ((GetWindowLongPtr(hwnd, GwExStyle).ToInt64() & WsExNoActivate) != 0) return false;
        return GetWindowRect(hwnd, out var rectangle)
            && rectangle.Right > rectangle.Left
            && rectangle.Bottom > rectangle.Top;
    }

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, index)
            : new IntPtr(GetWindowLong32(hwnd, index));

    private static ExternalWindowIdentity GetProcessIdentity(uint processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return new(null, null);
        try
        {
            var path = new StringBuilder(32768);
            var pathLength = (uint)path.Capacity;
            var executablePath = QueryFullProcessImageName(handle, 0, path, ref pathLength)
                ? path.ToString()
                : null;
            var appUserModelId = GetApplicationUserModelId(handle);
            return new ExternalWindowIdentity(appUserModelId, executablePath);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? GetApplicationUserModelId(IntPtr processHandle)
    {
        uint length = 0;
        var result = GetApplicationUserModelId(processHandle, ref length, null);
        if (result != ErrorInsufficientBuffer || length == 0) return null;
        var value = new StringBuilder((int)length);
        result = GetApplicationUserModelId(processHandle, ref length, value);
        return result == 0 ? value.ToString() : null;
    }

    private static string? ReadWindowAppUserModelId(IntPtr hwnd)
    {
        IPropertyStore? store = null;
        PropVariant value = default;
        try
        {
            var iid = PropertyStoreInterfaceId;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) < 0 || store == null) return null;
            var key = AppModelIdPropertyKey;
            if (store.GetValue(ref key, out value) < 0) return null;
            if (PropVariantToStringAlloc(ref value, out var text) < 0 || text == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(text); }
            finally { Marshal.FreeCoTaskMem(text); }
        }
        catch
        {
            return null;
        }
        finally
        {
            try { PropVariantClear(ref value); } catch { }
            if (store != null && Marshal.IsComObject(store)) Marshal.ReleaseComObject(store);
        }
    }

    private bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasDisabledOwner(IntPtr hwnd)
    {
        var owner = GetWindow(hwnd, GwOwner);
        var visited = new HashSet<IntPtr>();
        for (var depth = 0; owner != IntPtr.Zero && depth < 12 && visited.Add(owner); depth++)
        {
            if (!IsWindowEnabled(owner)) return true;
            owner = GetWindow(owner, GwOwner);
        }
        return false;
    }

    private static bool IsFocusDeskExecutable(string? path) => PathsEqual(path, Environment.ProcessPath);

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
        public PropertyKey(Guid formatId, int propertyId) { FormatId = formatId; PropertyId = propertyId; }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int objectId,
        int childId, uint eventThread, uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr eventHook,
        WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr eventHook);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("kernel32.dll", EntryPoint = "OpenProcess", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags,
        StringBuilder imageName, ref uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetApplicationUserModelId", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder? appUserModelId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int valueSize);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? propertyStore);

    [DllImport("propsys.dll", PreserveSig = true)]
    private static extern int PropVariantToStringAlloc(ref PropVariant propVariant, out IntPtr value);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant propVariant);
}
