using System.IO;
using System.Windows.Media.Imaging;

namespace Lertaro.Plugins.Clipboard;

internal static class PanelPasteImages
{
    private static readonly object Gate = new();

    internal static string? Resolve(HistoryStore store, ClipRecord record, bool stage)
    {
        var original = store.ImagePath(record);
        try
        {
            using var stream = File.OpenRead(original);
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;
            if (!stage) return original;
            lock (Gate)
            {
                var root = Path.Combine(store.Root, "paste-files");
                Directory.CreateDirectory(root);
                if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("无法安全保存截图临时副本。");
                Prune(root, DateTime.UtcNow.AddDays(-1));
                var path = Path.Combine(root, record.ImageFile);
                if (PanelPastePlan.PathExists(path))
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("无法安全使用截图临时副本。");
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                    return path;
                }
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    stream.Position = 0;
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        stream.CopyTo(output);
                    File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return path;
            }
        }
        catch (Exception ex) when (IsMissingOrInvalid(ex)) { return null; }
    }

    internal static bool IsMissingOrInvalid(Exception ex) =>
        ex is FileNotFoundException or DirectoryNotFoundException or FileFormatException or NotSupportedException;

    internal static void Clear(HistoryStore store)
    {
        lock (Gate) Prune(Path.Combine(store.Root, "paste-files"), DateTime.MaxValue);
    }

    private static void Prune(string root, DateTime cutoff)
    {
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return;
        foreach (var path in Directory.EnumerateFiles(root, "*.png"))
        {
            var name = Path.GetFileName(path);
            if (name.Length != 68 || !name[..64].All(Uri.IsHexDigit) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || File.GetLastWriteTimeUtc(path) >= cutoff) continue;
            try { File.Delete(path); }
            catch (Exception ex) when (cutoff != DateTime.MaxValue && ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
