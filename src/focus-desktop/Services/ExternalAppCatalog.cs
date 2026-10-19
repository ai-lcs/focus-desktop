using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace focus_desktop.Services;

public sealed record ExternalAppScanError(string Path, string Message);

public sealed record ExternalAppScanResult(
    IReadOnlyList<ExternalAppEntry> Entries,
    IReadOnlyList<ExternalAppScanError> Errors);

/// <summary>Discovers, resolves, and starts explicitly selected Shell application entries.</summary>
public static class ExternalAppCatalog
{
    private const string AppUserModelIdMarker = "!";
    private const uint GpsDefault = 0;
    private const int StgmRead = 0;

    private static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid PropertyStoreInterfaceId = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly Guid ApplicationActivationManagerClassId = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");
    private static readonly PropertyKey LinkTargetParsingPathKey = new(
        new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), 2);

    /// <summary>Scans both Windows desktop known folders without blocking the WPF dispatcher.</summary>
    public static Task<ExternalAppScanResult> ScanDesktopAsync(CancellationToken cancellationToken = default) =>
        RunOnStaAsync(() => ScanDesktop(cancellationToken), cancellationToken, "External app desktop scan");

    /// <summary>Resolves one user-selected shortcut without blocking the WPF dispatcher.</summary>
    public static Task<ExternalAppEntry> ResolveAsync(string path, CancellationToken cancellationToken = default) =>
        RunOnStaAsync(() => Resolve(path), cancellationToken, "External app shortcut resolve");

    /// <summary>Resolves a selected .lnk or .exe path, preserving the shortcut's launch data.</summary>
    public static ExternalAppEntry Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("请选择快捷方式或应用程序。", nameof(path));

        var fullPath = Path.GetFullPath(path.Trim());
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("所选文件不存在。", fullPath);

        if (string.Equals(Path.GetExtension(fullPath), ".lnk", StringComparison.OrdinalIgnoreCase))
            return ResolveShortcut(fullPath);

        if (string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
            return ResolveExecutable(fullPath);

        throw new InvalidDataException("请选择 .lnk 快捷方式或 .exe 应用程序。普通网页快捷方式和文档不受支持。");
    }

    /// <summary>
    /// Starts the frozen executable and its arguments through ShellExecute-compatible
    /// ProcessStartInfo, or activates a packaged app by its frozen AUMID. Shortcut-only
    /// options such as RunAs/show-command are not replayed. Packaged activation may return
    /// null after a successful hand-off; invalid entries throw a retryable error.
    /// </summary>
    public static Process? Launch(ExternalAppEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.TryValidate(out var validationError))
            throw new InvalidDataException(validationError);

        if (!string.IsNullOrWhiteSpace(entry.ShortcutPath) && File.Exists(entry.ShortcutPath))
        {
            ExternalAppEntry currentShortcut;
            try { currentShortcut = Resolve(entry.ShortcutPath); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "原快捷方式已失效或无法解析，未启动。请恢复原快捷方式，或按 README 的重新配置流程更换名单。", ex);
            }

            if (!IsSameFrozenLaunchDefinition(entry, currentShortcut))
                throw new InvalidOperationException(
                    "快捷方式的应用目标、参数或工作目录已更改，未启动。请恢复原快捷方式，或按 README 的重新配置流程更换名单。");
        }

        if (entry.Kind == ExternalAppEntry.PackagedAppKind)
            return ActivatePackagedApp(entry.AppUserModelId!, entry.Arguments);

        var executable = entry.ExecutablePath!;
        if (!File.Exists(executable))
            throw new FileNotFoundException(
                "应用程序已不存在，未启动。请恢复应用安装；更换软件需按 README 的重新配置流程更换名单。", executable);

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Arguments = entry.Arguments ?? "",
            WorkingDirectory = string.IsNullOrWhiteSpace(entry.WorkingDirectory)
                ? Path.GetDirectoryName(executable) ?? ""
                : entry.WorkingDirectory,
        };
        return Process.Start(startInfo);
    }

    internal static bool IsSameFrozenLaunchDefinition(ExternalAppEntry frozen, ExternalAppEntry resolved)
    {
        var frozenWorkingDirectory = frozen.Kind == ExternalAppEntry.PackagedAppKind
            ? frozen.WorkingDirectory
            : EffectiveWorkingDirectory(frozen);
        var resolvedWorkingDirectory = resolved.Kind == ExternalAppEntry.PackagedAppKind
            ? resolved.WorkingDirectory
            : EffectiveWorkingDirectory(resolved);
        if (!string.Equals(frozen.Kind, resolved.Kind, StringComparison.Ordinal)
            || !string.Equals(frozen.Id, resolved.Id, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(frozen.Arguments ?? "", resolved.Arguments ?? "", StringComparison.Ordinal)
            || !SamePath(frozenWorkingDirectory, resolvedWorkingDirectory))
            return false;

        if (frozen.Kind == ExternalAppEntry.PackagedAppKind)
            return string.Equals(frozen.AppUserModelId, resolved.AppUserModelId, StringComparison.OrdinalIgnoreCase);

        return SamePath(frozen.ExecutablePath, resolved.ExecutablePath);
    }

    private static bool SamePath(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
            return string.IsNullOrWhiteSpace(first) && string.IsNullOrWhiteSpace(second);
        return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    }

    private static string? EffectiveWorkingDirectory(ExternalAppEntry entry) =>
        string.IsNullOrWhiteSpace(entry.WorkingDirectory)
            ? Path.GetDirectoryName(entry.ExecutablePath!)
            : entry.WorkingDirectory;

    private static ExternalAppScanResult ScanDesktop(CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, ExternalAppEntry>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<ExternalAppScanError>();
        var desktopFolders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };

        foreach (var folder in desktopFolders.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(folder)) continue;

            string[] shortcuts;
            try { shortcuts = Directory.GetFiles(folder, "*.lnk", SearchOption.TopDirectoryOnly); }
            catch (Exception ex)
            {
                errors.Add(new ExternalAppScanError(folder, $"无法读取此桌面目录：{ex.Message}"));
                continue;
            }

            foreach (var shortcut in shortcuts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var entry = ResolveShortcut(shortcut);
                    entries.TryAdd(entry.Id, entry);
                }
                catch (Exception ex)
                {
                    errors.Add(new ExternalAppScanError(shortcut, FriendlyError(ex)));
                }
            }
        }

        var ordered = entries.Values
            .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].Order = i;
        return new ExternalAppScanResult(ordered, errors);
    }

    private static Task<T> RunOnStaAsync<T>(Func<T> operation, CancellationToken cancellationToken, string threadName)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(operation());
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })
        {
            IsBackground = true,
            Name = threadName,
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
    }

    private static ExternalAppEntry ResolveShortcut(string shortcutPath)
    {
        var targetParsingPath = ReadTargetParsingPath(shortcutPath);
        var (targetPath, arguments, workingDirectory) = ReadShortcutFields(shortcutPath);
        var displayName = Path.GetFileNameWithoutExtension(shortcutPath);
        if (string.IsNullOrWhiteSpace(displayName)) displayName = "未命名应用";

        var appUserModelId = ExtractAppUserModelId(targetParsingPath);
        if (appUserModelId != null)
        {
            var packagedExpandedWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? null
                : Environment.ExpandEnvironmentVariables(workingDirectory);
            var packagedNormalizedWorkingDirectory = string.IsNullOrWhiteSpace(packagedExpandedWorkingDirectory)
                ? null
                : Path.GetFullPath(packagedExpandedWorkingDirectory);
            return new ExternalAppEntry
            {
                Id = ExternalAppEntry.CreatePackagedStableId(appUserModelId, arguments, packagedNormalizedWorkingDirectory),
                DisplayName = displayName,
                Kind = ExternalAppEntry.PackagedAppKind,
                ShortcutPath = shortcutPath,
                Arguments = arguments,
                WorkingDirectory = packagedNormalizedWorkingDirectory,
                AppUserModelId = appUserModelId,
            };
        }

        var executable = !string.IsNullOrWhiteSpace(targetParsingPath) ? targetParsingPath : targetPath;
        if (!string.IsNullOrWhiteSpace(executable)) executable = Environment.ExpandEnvironmentVariables(executable);
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable))
            throw new InvalidDataException("快捷方式没有可识别的应用程序目标。");
        if (!string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("快捷方式目标不是受支持的 .exe 应用程序。");
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable))
            throw new FileNotFoundException("快捷方式指向的应用程序不存在。", executable);

        var normalizedArguments = arguments ?? "";
        var expandedWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? null
            : Environment.ExpandEnvironmentVariables(workingDirectory);
        var normalizedWorkingDirectory = string.IsNullOrWhiteSpace(expandedWorkingDirectory)
            ? Path.GetDirectoryName(executable) ?? ""
            : Path.GetFullPath(expandedWorkingDirectory);
        return new ExternalAppEntry
        {
            Id = ExternalAppEntry.CreateWin32StableId(executable, normalizedArguments, normalizedWorkingDirectory),
            DisplayName = displayName,
            Kind = ExternalAppEntry.ShellLinkKind,
            ShortcutPath = shortcutPath,
            ExecutablePath = executable,
            Arguments = arguments,
            WorkingDirectory = normalizedWorkingDirectory,
        };
    }

    private static ExternalAppEntry ResolveExecutable(string executablePath)
    {
        var displayName = Path.GetFileNameWithoutExtension(executablePath);
        var fullPath = Path.GetFullPath(executablePath);
        return new ExternalAppEntry
        {
            Id = ExternalAppEntry.CreateWin32StableId(fullPath, "", Path.GetDirectoryName(fullPath)),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "未命名应用" : displayName,
            Kind = ExternalAppEntry.ExecutableKind,
            ExecutablePath = fullPath,
            WorkingDirectory = Path.GetDirectoryName(fullPath),
            Arguments = "",
        };
    }

    private static string? ReadTargetParsingPath(string shortcutPath)
    {
        var iid = PropertyStoreInterfaceId;
        var hr = SHGetPropertyStoreFromParsingName(shortcutPath, IntPtr.Zero, GpsDefault, ref iid, out var propertyStorePointer);
        if (hr < 0 || propertyStorePointer == IntPtr.Zero) return null;

        IPropertyStore? propertyStore = null;
        try
        {
            propertyStore = (IPropertyStore)Marshal.GetObjectForIUnknown(propertyStorePointer);
            var key = LinkTargetParsingPathKey;
            hr = propertyStore.GetValue(ref key, out var value);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            try
            {
                return value.ValueType == 31 && value.StringValue != IntPtr.Zero
                    ? Marshal.PtrToStringUni(value.StringValue)
                    : null;
            }
            finally { PropVariantClear(ref value); }
        }
        finally
        {
            if (propertyStore != null && Marshal.IsComObject(propertyStore)) Marshal.ReleaseComObject(propertyStore);
            Marshal.Release(propertyStorePointer);
        }
    }

    private static (string? TargetPath, string? Arguments, string? WorkingDirectory) ReadShortcutFields(string shortcutPath)
    {
        var shellLinkType = Type.GetTypeFromCLSID(ShellLinkClassId, throwOnError: true)!;
        var instance = Activator.CreateInstance(shellLinkType)
            ?? throw new InvalidOperationException("无法读取快捷方式。");
        try
        {
            ((IPersistFile)instance).Load(shortcutPath, StgmRead);
            var link = (IShellLinkW)instance;
            var target = new StringBuilder(32768);
            var arguments = new StringBuilder(32768);
            var workingDirectory = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            link.GetArguments(arguments, arguments.Capacity);
            link.GetWorkingDirectory(workingDirectory, workingDirectory.Capacity);
            return (EmptyToNull(target.ToString()), EmptyToNull(arguments.ToString()), EmptyToNull(workingDirectory.ToString()));
        }
        finally
        {
            if (Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
        }
    }

    private static Process? ActivatePackagedApp(string appUserModelId, string? arguments)
    {
        object? instance = null;
        try
        {
            var activationType = Type.GetTypeFromCLSID(ApplicationActivationManagerClassId, throwOnError: true)!;
            instance = Activator.CreateInstance(activationType)
                ?? throw new InvalidOperationException("Windows 应用激活服务不可用。");
            var manager = (IApplicationActivationManager)instance;
            var hr = manager.ActivateApplication(appUserModelId, arguments ?? "", 0, out var processId);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            try { return processId == 0 ? null : Process.GetProcessById(checked((int)processId)); }
            catch (ArgumentException) { return null; } // Shell 已接手，但目标进程可能立即转交/退出。
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            throw new InvalidOperationException(
                "Windows 无法激活此打包应用。请恢复应用安装；更换软件需按 README 的重新配置流程更换名单。", ex);
        }
        finally
        {
            if (instance != null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
        }
    }

    private static string? ExtractAppUserModelId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        const string appsFolderPrefix = "shell:AppsFolder\\";
        var candidate = value.StartsWith(appsFolderPrefix, StringComparison.OrdinalIgnoreCase)
            ? value[appsFolderPrefix.Length..]
            : value;
        return IsAppUserModelId(candidate) ? candidate : null;
    }

    private static bool IsAppUserModelId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains(AppUserModelIdMarker, StringComparison.Ordinal)
        && value.IndexOf(AppUserModelIdMarker, StringComparison.Ordinal) == value.LastIndexOf(AppUserModelIdMarker, StringComparison.Ordinal)
        && !value.Contains('\\')
        && !value.Contains('/')
        && !Path.IsPathRooted(value);

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string FriendlyError(Exception ex) => ex switch
    {
        FileNotFoundException => "快捷方式指向的应用已不存在。",
        InvalidDataException => ex.Message,
        UnauthorizedAccessException => "没有权限读取此快捷方式。",
        COMException => "Windows Shell 无法解析此快捷方式。",
        _ => $"无法解析此快捷方式：{ex.Message}",
    };

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetPropertyStoreFromParsingName(
        string path, IntPtr bindContext, uint flags, ref Guid interfaceId, out IntPtr propertyStore);

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, int mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            uint options,
            out uint processId);
        [PreserveSig]
        int ActivateForFile(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr itemArray,
            [MarshalAs(UnmanagedType.LPWStr)] string verb,
            out uint processId);
        [PreserveSig]
        int ActivateForProtocol(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr itemArray,
            out uint processId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort ValueType;
        [FieldOffset(8)] public IntPtr StringValue;
    }
}
