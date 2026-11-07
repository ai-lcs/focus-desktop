using System.Threading;

namespace focus_desktop.Services;

/// <summary>Serializes ownership of the interactive desktop session across Focus Desk paths.</summary>
public static class DesktopSessionCoordinator
{
    public const string MutexName = "focus-desktop-single-instance";

    private static readonly object LeaseGate = new();
    private static readonly Dictionary<Thread, int> LeaseCounts = new();

    public static bool IsHeldByCurrentThread
    {
        get
        {
            lock (LeaseGate) return LeaseCounts.ContainsKey(Thread.CurrentThread);
        }
    }

    /// <summary>Tries to acquire the session mutex without waiting or crossing an async boundary.</summary>
    public static bool TryAcquire(out DesktopSessionLease? lease, out string reason)
        => TryAcquireNamed(MutexName, out lease, out reason);

    internal static bool TryAcquireNamed(string mutexName, out DesktopSessionLease? lease, out string reason)
    {
        lease = null;
        reason = string.Empty;
        Mutex? mutex = null;
        var ownsMutex = false;
        var registered = false;
        var abandoned = false;

        try
        {
            mutex = new Mutex(initiallyOwned: false, name: mutexName);
            try
            {
                ownsMutex = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // WaitOne grants ownership before reporting abandonment.
                ownsMutex = true;
                abandoned = true;
            }

            if (!ownsMutex)
            {
                reason = "Focus Desk 会话或桌面恢复操作正在运行。";
                return false;
            }

            var ownerThread = Thread.CurrentThread;
            lock (LeaseGate)
            {
                LeaseCounts.TryGetValue(ownerThread, out var currentCount);
                LeaseCounts[ownerThread] = currentCount + 1;
                registered = true;
            }

            lease = new DesktopSessionLease(mutex, ownerThread, abandoned);
            mutex = null;
            ownsMutex = false;
            reason = abandoned ? "已取得会话协调权；上个持有线程异常退出，恢复记录必须重新核验。" : string.Empty;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            reason = "无法访问 Focus Desk 会话协调锁；为保护桌面状态，本次操作已拒绝。";
            return false;
        }
        catch (Exception ex)
        {
            reason = $"无法取得 Focus Desk 会话协调锁（{ex.GetType().Name}）；本次操作已拒绝。";
            return false;
        }
        finally
        {
            if (mutex != null)
            {
                if (registered) Unregister(Thread.CurrentThread);
                if (ownsMutex)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
                mutex.Dispose();
            }
        }
    }

    internal static void Unregister(Thread ownerThread)
    {
        lock (LeaseGate)
        {
            if (!LeaseCounts.TryGetValue(ownerThread, out var currentCount)) return;
            if (currentCount <= 1) LeaseCounts.Remove(ownerThread);
            else LeaseCounts[ownerThread] = currentCount - 1;
        }
    }
}

/// <summary>A thread-affine lease; acquire, use, and dispose it synchronously on one thread.</summary>
public sealed class DesktopSessionLease : IDisposable
{
    private Mutex? _mutex;
    private readonly Thread _ownerThread;

    internal DesktopSessionLease(Mutex mutex, Thread ownerThread, bool wasAbandoned)
    {
        _mutex = mutex;
        _ownerThread = ownerThread;
        WasAbandoned = wasAbandoned;
    }

    public bool WasAbandoned { get; }

    public bool IsHeldByCurrentThread =>
        _mutex != null && ReferenceEquals(_ownerThread, Thread.CurrentThread);

    public void Dispose()
    {
        var mutex = Volatile.Read(ref _mutex);
        if (mutex == null) return;
        if (!ReferenceEquals(_ownerThread, Thread.CurrentThread))
            throw new InvalidOperationException("DesktopSessionLease 必须由取得它的线程释放。请勿跨 async/线程边界持有。" );

        if (!ReferenceEquals(Interlocked.CompareExchange(ref _mutex, null, mutex), mutex)) return;
        try
        {
            mutex.ReleaseMutex();
        }
        finally
        {
            mutex.Dispose();
            DesktopSessionCoordinator.Unregister(_ownerThread);
        }
    }
}
