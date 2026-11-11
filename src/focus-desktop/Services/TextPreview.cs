using System.IO;
using System.Net;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace focus_desktop.Services;

internal static class TextPreview
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseAutoLinks().DisableHtml().Build();

    internal static string Render(string path, string studyFolder)
    {
        if (!UrlFilter.IsPathUnderDirectory(path, studyFolder))
            throw new UnauthorizedAccessException("文件不在学习目录内。");
        var (text, encoding) = Decode(File.ReadAllBytes(path));
        if (text.Contains('\0')) throw new InvalidDataException("文本包含 NUL 字符；请另存为 UTF-8 或带 BOM 的 UTF-16 后重试。");
        string body;
        if (string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase))
        {
            var document = Markdown.Parse(text, Pipeline);
            foreach (var link in document.Descendants<LinkInline>())
            {
                if (link.Url == null || link.Url.StartsWith('#')) continue;
                if (!Uri.TryCreate(new Uri(path), link.Url, out var uri)) { link.Url = ""; continue; }
                if (!link.IsImage) { link.Url = uri.AbsoluteUri; continue; }
                // Images are embedded only after checking the actual local target; no remote requests.
                var extension = uri.IsFile ? Path.GetExtension(uri.LocalPath).ToLowerInvariant() : "";
                if (uri.IsFile && UrlFilter.IsPathUnderDirectory(uri.LocalPath, studyFolder)
                    && File.Exists(uri.LocalPath) && extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp")
                {
                    var mime = extension is ".jpg" or ".jpeg" ? "jpeg" : extension[1..];
                    link.Url = $"data:image/{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(uri.LocalPath))}";
                }
                else link.Url = "data:,";
            }
            body = document.ToHtml(Pipeline);
        }
        else body = $"<pre class=plain><code>{WebUtility.HtmlEncode(text)}</code></pre>";
        return Page(body, encoding);
    }

    internal static (string Text, string Encoding) Decode(byte[] bytes)
    {
        // BOM detection is explicit, and every decoder rejects malformed input.
        foreach (var encoding in new Encoding[] { new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true),
                     new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true) })
        {
            var preamble = encoding.GetPreamble();
            if (bytes.AsSpan().StartsWith(preamble))
                return (encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length), encoding.WebName);
        }
        try { return (new UTF8Encoding(false, true).GetString(bytes), "UTF-8"); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var chinese = Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return (chinese.GetString(bytes), "GB18030（兼容 GBK）");
        }
    }

    internal static string Error(Exception error) => Page($"<h1>无法读取文本</h1><p>{WebUtility.HtmlEncode(error.Message)}</p>", "请检查文件编码或访问权限");

    private static string Page(string body, string encoding) => $$"""
        <!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
        <meta name="color-scheme" content="light"><style>
        html,body{background:#fff;color:#202124;color-scheme:light}body{font:17px/1.75 'Segoe UI','Microsoft YaHei',sans-serif;margin:0;padding:28px 36px;overflow-wrap:anywhere}
        article{max-width:1100px;margin:auto}pre{color:#202124;overflow:auto;padding:16px;background:#f5f6f8;border-radius:8px;font:15px/1.65 Consolas,'Microsoft YaHei',monospace}
        pre.plain{white-space:pre-wrap;tab-size:4;background:#fff;padding:0;font:17px/1.75 Consolas,'Microsoft YaHei',monospace}
        h1,h2,h3{line-height:1.35;margin-top:1.3em}table{border-collapse:collapse;display:block;overflow:auto}th,td{border:1px solid #ccd1d8;padding:8px 14px}th{background:#f5f6f8}blockquote{border-left:4px solid #ccd1d8;margin-left:0;padding-left:18px}a{color:#1659a6}img{max-width:100%}code{font-family:Consolas,'Microsoft YaHei',monospace}footer{color:#68707b;font-size:12px;margin-top:32px}
        </style></head><body><article>{{body}}<footer>编码：{{WebUtility.HtmlEncode(encoding)}}</footer></article></body></html>
        """;
}
