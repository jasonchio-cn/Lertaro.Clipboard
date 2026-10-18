using System.IO;
using System.Text;
using System.Text.Json;

namespace Lertaro.Plugins.Clipboard;

internal sealed record FilePreviewData(string Path, string Name, string Kind, long Size = 0,
    DateTime? Modified = null, string Text = "", bool Truncated = false, string[]? Children = null, string Error = "");

internal static class FilePreviewReader
{
    internal const int MaxTextChars = 100_000;
    internal const int MaxFolderEntries = 200;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".tif", ".tiff", ".webp", ".heic" };
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".log", ".md", ".json", ".xml", ".csv", ".tsv", ".ini", ".cfg", ".config", ".yaml", ".yml",
          ".cs", ".csproj", ".sln", ".py", ".js", ".jsx", ".ts", ".tsx", ".html", ".css", ".scss", ".sql",
          ".ps1", ".bat", ".cmd", ".sh", ".toml", ".rs", ".go", ".java", ".c", ".cpp", ".h", ".vue" };
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".zip", ".7z", ".rar",
          ".tar", ".gz", ".exe", ".dll", ".msi", ".lnk", ".bin", ".mp3", ".mp4", ".mkv", ".wav", ".flac", ".ogg" };

    // Bounded, read-only file access. No shell handler or file association is executed here.
    internal static FilePreviewData Read(string path, CancellationToken token = default)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(name)) name = path;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!System.IO.Path.IsPathFullyQualified(path)) return new(path, name, "missing", Error: "不是有效的绝对路径。");
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                var children = new List<string>();
                using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                while (children.Count <= MaxFolderEntries && entries.MoveNext())
                { token.ThrowIfCancellationRequested(); children.Add(entries.Current); }
                return new(path, name, "folder", Modified: Directory.GetLastWriteTime(path),
                    Truncated: children.Count > MaxFolderEntries, Children: children.Take(MaxFolderEntries).ToArray());
            }
            var info = new FileInfo(path);
            var result = new FilePreviewData(path, name, "other", info.Length, info.LastWriteTime);
            var extension = System.IO.Path.GetExtension(path);
            if (ImageExtensions.Contains(extension)) return result with { Kind = "image" };
            if (BinaryExtensions.Contains(extension)) return result;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[4096]; var count = stream.Read(header, 0, header.Length);
            var utf16 = count >= 2 && ((header[0] == 0xFF && header[1] == 0xFE) || (header[0] == 0xFE && header[1] == 0xFF));
            var utf32 = count >= 4 && ((header[0] == 0 && header[1] == 0 && header[2] == 0xFE && header[3] == 0xFF) ||
                (header[0] == 0xFF && header[1] == 0xFE && header[2] == 0 && header[3] == 0));
            if (!utf16 && !utf32 && header.AsSpan(0, count).Contains((byte)0)) return result;
            // Never classify a large arbitrary binary as text merely because its first bytes lack NULs.
            if (!TextExtensions.Contains(extension) && info.Length > 100_000) return result;
            stream.Position = 0;
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
            var chars = new char[MaxTextChars + 1];
            var read = reader.ReadBlock(chars, 0, chars.Length);
            token.ThrowIfCancellationRequested();
            var text = new string(chars, 0, Math.Min(read, MaxTextChars));
            var truncated = read > MaxTextChars;
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase) && !truncated)
            {
                try
                {
                    using var json = JsonDocument.Parse(text);
                    text = JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions
                    { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                }
                catch (JsonException) { }
            }
            return result with { Kind = "text", Text = text, Truncated = truncated };
        }
        catch (OperationCanceledException) { throw; }
        catch (DecoderFallbackException)
        { return new(path, name, "other", Error: "此文件无法按 UTF-8/Unicode 解码，可在原应用中查看。"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(path, name, "missing", Error: ex is FileNotFoundException or DirectoryNotFoundException
                ? "文件已移动或删除，无法预览。" : ex is DecoderFallbackException
                ? "此文件不是可识别的 Unicode 文本，无法直接读取内容。" : "无法读取文件：" + ex.Message);
        }
    }
}
