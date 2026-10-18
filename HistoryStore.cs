using System.IO;
using System.Text.Json;

namespace Lertaro.Plugins.Clipboard;

// Atomic JSON snapshots avoid a second SQLite/native dependency in the host process.
// Records and aggregate bytes are bounded; all persistence is called on a worker thread.
public sealed class HistoryStore
{
    private readonly object _gate = new();
    private readonly string _root;
    private List<ClipRecord> _records = [];
    public string Root => _root;
    public HistoryStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(Path.Combine(_root, "images"));
        var index = Path.Combine(_root, "history.json");
        if (!File.Exists(index)) return;
        // Fail closed on corruption; never overwrite unreadable user history with an empty list.
        if (new FileInfo(index).Length > 1024L * 1024 * 1024) throw new InvalidDataException("历史索引超过读取上限。");
        _records = JsonSerializer.Deserialize<List<ClipRecord>>(File.ReadAllText(index))
            ?? throw new InvalidDataException("历史索引无效。");
        if (_records.Any(x => !Valid(x))) throw new InvalidDataException("历史索引包含无效记录。");
    }

    private static bool Valid(ClipRecord x) => x != null && Guid.TryParseExact(x.Id, "N", out _) &&
        Enum.IsDefined(x.Kind) && x.Text != null && x.Files != null && x.Files.All(f => !string.IsNullOrWhiteSpace(f)) &&
        x.Bytes >= 0 && !string.IsNullOrEmpty(x.Hash) &&
        (x.Kind != ClipKind.Image || (x.ImageFile == x.Hash.Split(':').Last() + ".png" &&
            x.ImageFile.Length == 68 && x.ImageFile[..64].All(Uri.IsHexDigit)));

    public ClipRecord[] Snapshot() { lock (_gate) return _records.ToArray(); }
    public string ImagePath(ClipRecord item)
    {
        if (item.Kind != ClipKind.Image || !Valid(item)) throw new InvalidDataException("图片路径无效。");
        return Path.Combine(_root, "images", item.ImageFile);
    }

    public void SaveImage(ClipRecord item, byte[] png)
    {
        var file = ImagePath(item);
        if (File.Exists(file)) return;
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, png); File.Move(temp, file, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public ClipRecord Add(ClipRecord item, ClipLimits limits)
    {
        lock (_gate)
        {
            var previous = _records.FirstOrDefault(x => x.Hash == item.Hash);
            item = previous == null ? item : item with { Id = previous.Id };
            _records = _records.Where(x => x.Hash != item.Hash).Prepend(item).ToList();
            PruneUnsafe(limits);
            return item;
        }
    }
    public void Prune(ClipLimits limits) { lock (_gate) PruneUnsafe(limits); }
    private void PruneUnsafe(ClipLimits limits)
    {
        long total = 0, images = 0;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(limits.Days, 1, 3650));
        _records = _records.OrderByDescending(x => x.CopiedAt).Where(x => x.CopiedAt >= cutoff).Where(x =>
        {
            if (total + x.Bytes > limits.TotalBytes || (x.Kind == ClipKind.Image && images + x.Bytes > limits.ImageBytes)) return false;
            total += x.Bytes; if (x.Kind == ClipKind.Image) images += x.Bytes; return true;
        }).Take(Math.Clamp(limits.MaxItems, 1, 10000)).ToList();
    }
    public void Delete(string id) { lock (_gate) _records.RemoveAll(x => x.Id == id); }
    public void Clear() { lock (_gate) _records.Clear(); }

    public void Save(IEnumerable<ClipRecord> additionallyRetained)
    {
        var rows = Snapshot();
        var temp = Path.Combine(_root, "history." + Guid.NewGuid().ToString("N") + ".tmp");
        var index = Path.Combine(_root, "history.json");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, rows); stream.Flush(true); }
            File.Move(temp, index, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        // Only prune plugin-owned assets after successfully committing their new reference list.
        var live = rows.Concat(additionallyRetained).Where(x => x.Kind == ClipKind.Image).Select(x => x.ImageFile).ToHashSet();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_root, "images"), "*.png"))
        {
            var name = Path.GetFileName(file);
            if (name.Length == 68 && name[..64].All(Uri.IsHexDigit) && !live.Contains(name)) File.Delete(file);
        }
    }
}
