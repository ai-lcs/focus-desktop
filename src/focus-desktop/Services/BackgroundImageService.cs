using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace focus_desktop.Services;

/// <summary>
/// 首页背景图服务：向导选图后 Import 复制进 assets/，运行时 Load 解码并铺到 HomeView 底层。
/// 任何失败一律静默回退纯色背景（不抛错、不打扰用户）。不做缩略图缓存（单图单用途）。
/// </summary>
public static class BackgroundImageService
{
    /// <summary>背景图资源目录（exe 旁 focus-desktop-data/assets/）。</summary>
    public static readonly string AssetsDir = Path.Combine(Paths.DataDir, "assets");

    /// <summary>导入大小上限 50MB（防超大图撑爆内存/磁盘）。</summary>
    private const long MaxImportBytes = 50L * 1024 * 1024;

    /// <summary>
    /// 把用户选中的图片复制进 AssetsDir，返回目标文件名（"bg"+原扩展名，如 bg.jpg）
    /// 供调用方存入 config.BackgroundImage。成功后清理多余的旧 bg.* 文件。
    /// 失败（源文件不存在/超 50MB/无扩展名/复制异常）返回 null。
    /// </summary>
    public static string? Import(string sourcePath)
    {
        try
        {
            var ext = Path.GetExtension(sourcePath);
            if (string.IsNullOrEmpty(ext)) return null; // 无扩展名无法形成 bg.* 目标名

            var fi = new FileInfo(sourcePath);
            if (!fi.Exists || fi.Length > MaxImportBytes) return null;

            Directory.CreateDirectory(AssetsDir);
            var targetName = "bg" + ext;
            var targetPath = Path.Combine(AssetsDir, targetName);

            File.Copy(sourcePath, targetPath, overwrite: true);

            // 清理旧图：只保留刚复制的那张（bg.* 全部视为候选；清理失败不阻塞导入）
            foreach (var old in Directory.EnumerateFiles(AssetsDir, "bg.*"))
            {
                if (!string.Equals(old, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(old); } catch { /* 忽略清理失败 */ }
                }
            }
            return targetName;
        }
        catch
        {
            return null; // 复制/IO 异常 → 静默失败
        }
    }

    /// <summary>
    /// 从 AssetsDir 加载背景图：BitmapImage + DecodePixelWidth=2560（200% DPI 物理宽上限，
    /// 防大图内存爆炸）+ Freeze()（跨线程只读共享）。
    /// fileName 为空/文件丢失/解码失败均返回 null（调用方保持纯色背景）。
    /// </summary>
    public static ImageSource? Load(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        try
        {
            var path = Path.GetFullPath(Path.Combine(AssetsDir, fileName));
            // 防越界：只认 assets/ 目录内的文件（config 只应存文件名）
            if (!path.StartsWith(Path.GetFullPath(AssetsDir), StringComparison.OrdinalIgnoreCase)) return null;
            if (!File.Exists(path)) return null;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // 解码后立即释放文件句柄
            bmp.DecodePixelWidth = 2560;                // 200% DPI 物理宽上限
            bmp.UriSource = new Uri(path);
            bmp.EndInit();                              // 无效图片在此抛异常 → catch → null
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null; // 解码失败/IO 异常 → 静默回退纯色
        }
    }

    /// <summary>
    /// 一行调用入口：Load 成功则给 image 设 Source 并显示 image+mask 两层，
    /// 失败保持两者 Collapsed（纯色现状）。
    /// </summary>
    public static void ApplyTo(System.Windows.Controls.Image image, System.Windows.Controls.Border mask, string? fileName)
    {
        var src = Load(fileName);
        if (src == null) return;
        image.Source = src;
        image.Visibility = Visibility.Visible;
        mask.Visibility = Visibility.Visible;
    }
}
