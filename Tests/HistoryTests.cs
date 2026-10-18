using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lertaro.Plugins.Clipboard.Tests;

[TestClass]
public class HistoryTests
{
    private string _root = null!;
    [TestInitialize] public void Init() => _root = Path.Combine(Path.GetTempPath(), "LertaroClipboardTests", Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    [TestMethod] public void TextRoundTripPreservesWhitespaceAndUnicode()
    {
        var store = new HistoryStore(_root);
        var text = "  \t中文😀\r\nline2\n ";
        store.Add(ClipRecord.FromText(text), new()); store.Save([]);
        Assert.AreEqual(text, new HistoryStore(_root).Snapshot().Single().Text);
    }
    [TestMethod] public void DuplicateMovesToFrontWithStableIdentity()
    {
        var store = new HistoryStore(_root);
        var a = store.Add(ClipRecord.FromText("A"), new());
        store.Add(ClipRecord.FromText("B"), new());
        var again = store.Add(ClipRecord.FromText("A"), new());
        Assert.AreEqual(2, store.Snapshot().Length); Assert.AreEqual(a.Id, again.Id);
        Assert.AreEqual("A", store.Snapshot()[0].Text);
    }
    [TestMethod] public void RetentionDropsOldestByCountAndAge()
    {
        var store = new HistoryStore(_root);
        store.Add(ClipRecord.FromText("old") with { CopiedAt = DateTimeOffset.UtcNow.AddDays(-100) }, new());
        store.Add(ClipRecord.FromText("A"), new(MaxItems: 1));
        store.Add(ClipRecord.FromText("B"), new(MaxItems: 1));
        Assert.AreEqual("B", store.Snapshot().Single().Text);
    }
    [TestMethod] public void RetentionEnforcesAggregateBytes()
    {
        var store = new HistoryStore(_root);
        store.Add(ClipRecord.FromText("12345"), new(TotalBytes: 8));
        store.Add(ClipRecord.FromText("67890"), new(TotalBytes: 8));
        Assert.AreEqual("67890", store.Snapshot().Single().Text);
    }
    [TestMethod] public void CorruptHistoryIsPreservedAndRejected()
    {
        Directory.CreateDirectory(_root); var path = Path.Combine(_root, "history.json");
        File.WriteAllText(path, "{invalid");
        Assert.ThrowsException<JsonException>(() => new HistoryStore(_root));
        Assert.AreEqual("{invalid", File.ReadAllText(path));
    }
    [TestMethod] public void InvalidImagePathCannotEscapeStore()
    {
        var store = new HistoryStore(_root);
        var item = new ClipRecord { Kind = ClipKind.Image, Hash = "x", ImageFile = "..\\outside.png" };
        Assert.ThrowsException<InvalidDataException>(() => store.ImagePath(item));
    }
    [TestMethod] public void ImageCleanupHonorsActiveSequenceAndUnrelatedFiles()
    {
        var store = new HistoryStore(_root);
        byte[] png = [1, 2, 3]; var hash = ClipRecord.Fingerprint(ClipKind.Image, png);
        var image = new ClipRecord { Kind = ClipKind.Image, Hash = hash, ImageFile = hash.Split(':')[1] + ".png", Bytes = 3 };
        store.SaveImage(image, png); var path = store.ImagePath(image);
        File.WriteAllText(Path.Combine(_root, "images", "unrelated.png"), "keep");
        store.Save([image]); Assert.IsTrue(File.Exists(path));
        store.Save([]); Assert.IsFalse(File.Exists(path));
        Assert.IsTrue(File.Exists(Path.Combine(_root, "images", "unrelated.png")));
    }
    [TestMethod] public void FailedSnapshotDoesNotDeleteAssets()
    {
        var store = new HistoryStore(_root);
        byte[] png = [3, 4]; var hash = ClipRecord.Fingerprint(ClipKind.Image, png);
        var item = new ClipRecord { Kind = ClipKind.Image, Hash = hash, ImageFile = hash.Split(':')[1] + ".png", Bytes = 2 };
        store.SaveImage(item, png); Directory.CreateDirectory(Path.Combine(_root, "history.json"));
        Assert.ThrowsException<UnauthorizedAccessException>(() => store.Save([]));
        Assert.IsTrue(File.Exists(store.ImagePath(item)));
    }
    [DataTestMethod]
    [DataRow("{\"a\":1}", "json")]
    [DataRow("https://example.com", "url")]
    [DataRow("const a = 1;", "code")]
    [DataRow("#ff0000", "text")]
    public void ClassificationDoesNotIncludeColors(string text, string expected) => Assert.AreEqual(expected, ClipRecord.Classify(text));
    [TestMethod] public void TypeParticipatesInHash() => Assert.AreNotEqual(
        ClipRecord.Fingerprint(ClipKind.Text, Encoding.UTF8.GetBytes("same")), ClipRecord.Fingerprint(ClipKind.Files, Encoding.UTF8.GetBytes("same")));
}

[TestClass]
public class SequenceTests
{
    [TestMethod] public void NativeHotkeysRequireModifiersAndAcceptRecorderSyntax()
    {
        Assert.AreEqual((0x4003u, 0x56u), Native.ParseHotkey("Ctrl+Alt+V"));
        Assert.AreEqual((0u, 0u), Native.ParseHotkey(""));
        Assert.ThrowsException<ArgumentException>(() => Native.ParseHotkey("V"));
        Assert.ThrowsException<ArgumentException>(() => Native.ParseHotkey("+++"));
    }
    [TestMethod] public void FilterOnlyConsumesKnownCategoryPrefix()
    {
        Assert.AreEqual(("image", "sample"), ClipboardProvider.ParseFilter(" /image sample "));
        Assert.AreEqual(("all", "/unknown sample"), ClipboardProvider.ParseFilter("/unknown sample"));
    }
    [TestMethod] public void CopyOrderKeepsDuplicatesAndDoesNotCycle()
    {
        var queue = new PasteSequence(); foreach (var s in new[] { "A", "B", "A" }) queue.Add(ClipRecord.FromText(s), new());
        foreach (var s in new[] { "A", "B", "A" }) { Assert.AreEqual(s, queue.Peek()!.Text); queue.Advance(); }
        Assert.IsNull(queue.Peek());
    }
    [TestMethod] public void FailureDoesNotAdvanceAndNewCopiesDoNotAlterFrozenBatch()
    {
        var queue = new PasteSequence(); queue.Add(ClipRecord.FromText("A"), new());
        Assert.AreEqual("A", queue.Peek()!.Text);
        queue.Add(ClipRecord.FromText("B"), new());
        Assert.AreEqual("A", queue.Peek()!.Text); queue.Advance(); Assert.IsNull(queue.Peek());
    }
    [TestMethod] public void ResetDropsPreviousBatchAndCollectsNewOne()
    {
        var queue = new PasteSequence(); queue.Add(ClipRecord.FromText("old"), new()); queue.Peek(); queue.Reset();
        queue.Add(ClipRecord.FromText("new"), new()); Assert.AreEqual("new", queue.Peek()!.Text);
    }
    [TestMethod] public void RemovingConsumedItemsKeepsCorrectNextItem()
    {
        var queue = new PasteSequence(); var a = ClipRecord.FromText("A"); var b = ClipRecord.FromText("B");
        queue.Add(a, new()); queue.Add(b, new()); queue.Peek(); queue.Advance(); queue.Remove(a.Id);
        Assert.AreEqual("B", queue.Peek()!.Text);
    }
    [TestMethod] public void QueueIsBounded()
    {
        var queue = new PasteSequence(); queue.Add(ClipRecord.FromText("A"), new(MaxItems: 1)); queue.Add(ClipRecord.FromText("B"), new(MaxItems: 1));
        Assert.AreEqual("B", queue.Peek()!.Text);
    }
}
