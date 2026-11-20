using System.IO;

namespace Lertaro.Plugins.Clipboard;

internal static class PanelPastePlan
{
    internal static ClipRecord[] Build(IReadOnlyList<ClipRecord> ordered, Func<ClipRecord, string?> resolveImage, Func<string, bool> exists)
    {
        var segments = new List<ClipRecord>();
        var text = new List<string>();
        var files = new List<string>();
        foreach (var record in ordered)
        {
            if (record.Kind == ClipKind.Text)
            {
                if (record.Text.Length == 0) continue;
                FlushFiles();
                text.Add(record.Text);
                continue;
            }

            if (record.Kind == ClipKind.Image)
            {
                if (resolveImage(record) is not { } image) continue;
                if (ordered.Count == 1) return [record];
                FlushText();
                files.Add(image);
            }
            else
            {
                var live = record.Files.Where(exists).ToArray();
                if (live.Length == 0) continue;
                FlushText();
                files.AddRange(live);
            }
        }
        FlushText();
        FlushFiles();
        return segments.ToArray();

        void FlushText()
        {
            if (text.Count == 0) return;
            segments.Add(new ClipRecord { Kind = ClipKind.Text, Text = string.Join("\r\n", text) });
            text.Clear();
        }

        void FlushFiles()
        {
            if (files.Count == 0) return;
            segments.Add(new ClipRecord { Kind = ClipKind.Files, Files = files.ToArray() });
            files.Clear();
        }
    }

    internal static bool PathExists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }
}
