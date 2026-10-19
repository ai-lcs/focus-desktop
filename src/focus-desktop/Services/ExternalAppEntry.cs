using System.IO;
using System.Text;

namespace focus_desktop.Services;

/// <summary>
/// A frozen launch entry. Window ownership is matched by AppUserModelId for packaged
/// apps or ExecutablePath for Win32 apps, never by a title or process display name.
/// </summary>
public sealed class ExternalAppEntry
{
    public const string ShellLinkKind = "ShellLink";
    public const string ExecutableKind = "Executable";
    public const string PackagedAppKind = "PackagedApp";

    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? ShortcutPath { get; set; }
    public string? ExecutablePath { get; set; }
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? AppUserModelId { get; set; }
    public int Order { get; set; }

    public bool TryValidate(out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(DisplayName) || Order < 0)
        {
            error = "外部应用入口缺少标识、名称或有效顺序。";
            return false;
        }

        if (Kind == ShellLinkKind)
        {
            if (!IsFullyQualifiedFile(ShortcutPath, ".lnk")
                || !IsExecutablePath(ExecutablePath)
                || !string.IsNullOrWhiteSpace(AppUserModelId))
            {
                error = "快捷方式入口缺少有效的 .lnk 路径或冻结的 .exe 目标身份。";
                return false;
            }
        }
        else if (Kind == ExecutableKind)
        {
            if (!IsExecutablePath(ExecutablePath)
                || !string.IsNullOrWhiteSpace(ShortcutPath)
                || !string.IsNullOrWhiteSpace(AppUserModelId))
            {
                error = "可执行文件入口缺少程序路径或包含了不匹配的打包应用身份。";
                return false;
            }
        }
        else if (Kind == PackagedAppKind)
        {
            if (!IsAppUserModelId(AppUserModelId)
                || !string.IsNullOrWhiteSpace(ExecutablePath)
                || (!string.IsNullOrWhiteSpace(ShortcutPath) && !IsFullyQualifiedFile(ShortcutPath, ".lnk")))
            {
                error = "打包应用入口缺少有效的 AppUserModelId 或包含了不匹配的程序路径。";
                return false;
            }
        }
        else
        {
            error = "外部应用入口类型无法识别。";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(WorkingDirectory) && !Path.IsPathFullyQualified(WorkingDirectory))
        {
            error = "外部应用入口的工作目录必须是绝对路径。";
            return false;
        }

        string expectedId;
        try
        {
            expectedId = Kind == PackagedAppKind
                ? CreatePackagedStableId(AppUserModelId!, Arguments, WorkingDirectory)
                : CreateWin32StableId(ExecutablePath!, Arguments, WorkingDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            error = $"外部应用入口包含无法规范化的身份字段：{ex.Message}";
            return false;
        }

        if (!string.Equals(Id, expectedId, StringComparison.OrdinalIgnoreCase))
        {
            error = "外部应用入口的冻结身份与启动字段不一致。";
            return false;
        }

        return true;
    }

    internal static string CreateStableId(string identityKind, string identity)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"{identityKind}:{Convert.ToHexString(bytes.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    internal static string CreateWin32StableId(string executablePath, string? arguments, string? workingDirectory)
    {
        var executable = NormalizePath(executablePath);
        var working = string.IsNullOrWhiteSpace(workingDirectory)
            ? Path.GetDirectoryName(executable) ?? ""
            : NormalizePath(workingDirectory);
        return CreateStableId(ExecutableKind, ComposeIdentity(
            executable.ToUpperInvariant(), arguments ?? "", working.ToUpperInvariant()));
    }

    internal static string CreatePackagedStableId(string appUserModelId, string? arguments, string? workingDirectory)
    {
        var working = string.IsNullOrWhiteSpace(workingDirectory) ? "" : NormalizePath(workingDirectory);
        return CreateStableId(PackagedAppKind, ComposeIdentity(
            appUserModelId.ToUpperInvariant(), arguments ?? "", working.ToUpperInvariant()));
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path.Trim());

    private static string ComposeIdentity(params string[] fields)
    {
        var identity = new StringBuilder();
        foreach (var field in fields)
            identity.Append(field.Length).Append(':').Append(field);
        return identity.ToString();
    }

    private static bool IsExecutablePath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.IsPathFullyQualified(path)
        && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsFullyQualifiedFile(string? path, string extension) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.IsPathFullyQualified(path)
        && string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);

    private static bool IsAppUserModelId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && !value.Any(char.IsWhiteSpace)
        && value.Contains('!')
        && value.IndexOf('!') == value.LastIndexOf('!')
        && !value.Contains('\\')
        && !value.Contains('/')
        && !Path.IsPathRooted(value);
}
