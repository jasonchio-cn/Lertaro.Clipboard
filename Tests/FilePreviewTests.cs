using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lertaro.Plugins.Clipboard.Tests;

[TestClass]
public class FilePreviewTests
{
    private string _root = null!;
    [TestInitialize] public void Init() { _root = Path.Combine(Path.GetTempPath(), "LertaroFilePreviewTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() => Directory.Delete(_root, true);
    private string Write(string name, string text, Encoding? encoding = null)
    { var path = Path.Combine(_root, name); File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false)); return path; }

    [TestMethod] public void Utf8AndUtf16TextPreserveWhitespaceAndUnicode()
    {
        var content = "  中文🙂\r\nsecond line\n ";
        foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode })
        {
            var result = FilePreviewReader.Read(Write("sample.txt", content, encoding));
            Assert.AreEqual("text", result.Kind); Assert.AreEqual(content, result.Text);
        }
    }
    [TestMethod] public void JsonPreviewFormatsChineseWithoutChangingFile()
    {
        var original = "{\"项目\":\"剪贴板\",\"ok\":true}";
        var path = Write("sample.json", original);
        var result = FilePreviewReader.Read(path);
        Assert.IsTrue(result.Text.Contains("剪贴板")); Assert.IsTrue(result.Text.Contains('\n'));
        Assert.AreEqual(original, File.ReadAllText(path));
    }
    [TestMethod] public void LargeTextIsBoundedAndMarkedTruncated()
    {
        var result = FilePreviewReader.Read(Write("large.txt", new string('x', FilePreviewReader.MaxTextChars + 200)));
        Assert.AreEqual(FilePreviewReader.MaxTextChars, result.Text.Length); Assert.IsTrue(result.Truncated);
    }
    [TestMethod] public void BinaryWithTextExtensionIsNotRenderedAsText()
    {
        var path = Path.Combine(_root, "binary.txt"); File.WriteAllBytes(path, [0x4D, 0x5A, 0, 1, 2, 3]);
        Assert.AreEqual("other", FilePreviewReader.Read(path).Kind);
    }
    [TestMethod] public void UnknownLargeFormatIsNotReadAsText() => Assert.AreEqual("other", FilePreviewReader.Read(Write("large.bin", new string('a', 200_000))).Kind);
    [TestMethod] public void AsciiPdfUsesSystemPreviewInsteadOfShowingPdfSource() => Assert.AreEqual("other", FilePreviewReader.Read(Write("document.pdf", "%PDF-1.4\n1 0 obj << /Type /Catalog >> endobj\n%%EOF")).Kind);
    [TestMethod] public void InvalidUtf8FallsBackToSystemPreview()
    {
        var path = Path.Combine(_root, "legacy.txt"); File.WriteAllBytes(path, [0xFF, 0xA4, 0xB5, 0x61]);
        Assert.AreEqual("other", FilePreviewReader.Read(path).Kind);
    }
    [TestMethod] public void FolderPreviewIsBoundedAndDoesNotRecurse()
    {
        for (var n = 0; n < FilePreviewReader.MaxFolderEntries + 2; n++) Write(n + ".txt", "test");
        var result = FilePreviewReader.Read(_root);
        Assert.AreEqual("folder", result.Kind); Assert.AreEqual(FilePreviewReader.MaxFolderEntries, result.Children!.Length); Assert.IsTrue(result.Truncated);
    }
    [TestMethod] public void MissingFileHasClearStateAndOriginalPath()
    {
        var path = Path.Combine(_root, "missing.pdf"); var result = FilePreviewReader.Read(path);
        Assert.AreEqual("missing", result.Kind); Assert.AreEqual(path, result.Path); Assert.IsTrue(result.Error.Contains("移动或删除"));
    }
    [TestMethod] public void RelativePathIsRejected() => Assert.AreEqual("missing", FilePreviewReader.Read("relative.txt").Kind);
    [TestMethod] public void CancellationIsObservedBeforeReading() => Assert.ThrowsException<OperationCanceledException>(() => FilePreviewReader.Read(Write("cancel.txt", "text"), new CancellationToken(true)));
    [TestMethod] public void ScriptIsOnlyReadNeverExecuted()
    {
        var marker = Path.Combine(_root, "must-not-exist.txt");
        var path = Write("sample.ps1", "Set-Content -LiteralPath '" + marker + "' -Value 'executed'");
        Assert.AreEqual("text", FilePreviewReader.Read(path).Kind); Assert.IsFalse(File.Exists(marker));
    }
}
