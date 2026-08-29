using System.IO;

namespace FocusDesktop;

/// <summary>
/// 原子写入（Umbra.Core/AtomicFile.cs 移植，MIT）。
/// tmp 写入后 Move 替换：崩溃时要么旧内容完整、要么新内容完整，绝无截断中间态。
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
