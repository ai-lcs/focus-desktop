using System.IO;
namespace focus_desktop.Services;

/// <summary>
/// 原子写入（Umbra.Core/AtomicFile.cs 模式，MIT）：
/// 直接 WriteAllText 是 truncate-then-write，进程若在写入中途被杀会留下半个 JSON，
/// 下次 Load 只能吞异常回退默认值 = 数据静默丢失。先写 .tmp 再原子替换，文件系统层面
/// 要么旧内容完整保留，要么新内容完整生效，不存在中间态。
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
