namespace focus_desktop.Services;

/// <summary>
/// 硬性专注模式（用户 2026-08-31 需求）：
/// - 番茄钟开关开启后：退出弹窗输入框禁用（退出按钮可点但永远无法通过验证=无法退出）；
///   番茄钟重置禁用（防逃避）；开关自身锁定（防自我解除）。
/// - 唯一出口：当前专注段跑完自动进入休息时解除——"硬性专注的目的是撑完一个番茄"。
/// - 红线：Ctrl+Alt+Del / 任务管理器照常（产品原则：行为摩擦，不是系统锁死）；
///   预览/DEV 模式不启用真正的锁定（预览是调配置用的）。
/// </summary>
public static class HardFocus
{
    /// <summary>当前是否处于硬性专注（对退出验证生效）。</summary>
    public static bool Active { get; private set; }

    public static event Action? Changed;

    /// <summary>进入硬性专注。预览/DEV 模式传入 enforce=false（仅演示 UI 态）。</summary>
    public static void Enable(bool enforce)
    {
        Active = enforce;
        Changed?.Invoke();
    }

    /// <summary>解除（番茄钟专注段完成自动触发；也是唯一正常出口）。</summary>
    public static void Release()
    {
        Active = false;
        Changed?.Invoke();
    }

    /// <summary>退出弹窗是否应禁用输入（预览模式演示态：显示提示但不禁用，方便用户随时退出预览）。</summary>
    public static bool BlocksExitInput(bool isPreview) => isPreview ? false : Active;
}
