using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lertaro.Plugins.Clipboard;

public enum ClipKind { Text, Image, Files }

public sealed record ClipRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; init; }
    public string Category { get; init; } = "text";
    public string Text { get; init; } = "";
    public string[] Files { get; init; } = [];
    public string ImageFile { get; init; } = "";
    public string Hash { get; init; } = "";
    public DateTimeOffset CopiedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Source { get; init; } = "未知应用";
    public int Width { get; init; }
    public int Height { get; init; }
    public long Bytes { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary => Kind switch
    {
        ClipKind.Image => $"图片 · {Width} × {Height}",
        ClipKind.Files => $"{Files.Length} 个文件/文件夹 · {string.Join("、", Files.Select(System.IO.Path.GetFileName))}",
        _ => Text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ')
    };

    public static ClipRecord FromText(string text, string source = "未知应用") => new()
    {
        Kind = ClipKind.Text, Text = text, Source = source, Category = Classify(text),
        Hash = Fingerprint(ClipKind.Text, Encoding.UTF8.GetBytes(text)), Bytes = Encoding.UTF8.GetByteCount(text)
    };

    public static string Fingerprint(ClipKind kind, byte[] content) =>
        ((int)kind).ToString() + ":" + Convert.ToHexString(SHA256.HashData(content));

    public static string Classify(string text)
    {
        var trimmed = text.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return "url";
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try { using var json = JsonDocument.Parse(trimmed); return "json"; } catch (JsonException) { }
        }
        string[] markers = ["function ", "const ", "import ", "class ", "def ", "return ", "using System", "public static "];
        return markers.Any(text.Contains) ? "code" : "text";
    }
}

public sealed record ClipLimits(int MaxItems = 1000, int Days = 90, long TotalBytes = 256L * 1024 * 1024,
    long ImageBytes = 128L * 1024 * 1024, long MaxItemBytes = 10L * 1024 * 1024);

// Kept independent of the deduplicated history: A, B, A must paste A, B, A.
public sealed class PasteSequence
{
    private readonly List<ClipRecord> _pending = [];
    private List<ClipRecord>? _active;
    private int _position;
    public int PendingCount => _pending.Count;
    public (int Position, int Total) Progress => (_position, _active?.Count ?? _pending.Count);
    public IEnumerable<ClipRecord> Retained => _pending.Concat(_active ?? []);
    public void Add(ClipRecord item, ClipLimits limits)
    {
        _pending.Add(item);
        while (_pending.Count > limits.MaxItems || _pending.Sum(x => x.Bytes) > limits.TotalBytes)
            _pending.RemoveAt(0);
    }
    public ClipRecord? Peek()
    {
        if (_active == null) { _active = [.. _pending]; _pending.Clear(); _position = 0; }
        return _position < _active.Count ? _active[_position] : null;
    }
    public void Advance() { if (_active != null && _position < _active.Count) _position++; }
    public void Reset() { _active = null; _pending.Clear(); _position = 0; }
    public void Remove(string id)
    {
        _pending.RemoveAll(x => x.Id == id);
        if (_active == null) return;
        var consumed = _active.Take(_position).Count(x => x.Id != id);
        _active.RemoveAll(x => x.Id == id);
        _position = consumed;
    }
}
