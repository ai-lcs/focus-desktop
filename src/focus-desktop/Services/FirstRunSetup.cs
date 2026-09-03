using System.IO;

namespace focus_desktop.Services;

/// <summary>
/// 首次运行 Setup 模式（spec §9）：
/// - 首次启动（无 setup_done 标志）：不进锁定，用户自由登录四站/调整设置
/// - 配置提交后仍处于登录引导态；点「开始专注」→ 写 setup_done → 立即进锁定
/// - setup_done 完成后每次启动：直接锁定
/// setup_done 独立于 config.json（config 可能被手改，setup_done 只关心"用过了没有"）
/// </summary>
public static class FirstRunSetup
{
    private static string SetupFile => Path.Combine(Paths.DataDir, "setup_done.flag");

    public static bool IsSetupComplete()
    {
        try { return File.Exists(SetupFile); }
        catch { return true; } // 判定失败按已完成处理（宁可锁也要保证恢复协议在跑）
    }

    public static void EnterSetupMode()
    {
        // Setup 模式本质 = 一个标志位，App 层读它决定是否 Enter()
        // 标志文件在 CompleteSetup() 时写入
    }

    public static void CompleteSetup()
    {
        Paths.EnsureDirectories();
        AtomicFile.WriteAllText(SetupFile, DateTimeOffset.Now.ToString("O"));
    }
}
