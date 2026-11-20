using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Lertaro.Plugins.Clipboard;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clipboard.Tests;

[TestClass]
public class PanelSelectionTests
{
    [TestMethod]
    public void SingleCellsKeepBiff12WhileMultipleSelectionsUseDisplayText()
    {
        var cells = SpreadsheetClipboard.WithBiff12(ClipRecord.FromText("3"), [0, 255]);
        var plain = ClipRecord.FromText("end");
        Assert.AreSame(cells, PanelPastePlan.Build([cells], _ => null, _ => false).Single());
        var combined = PanelPastePlan.Build([cells, plain], _ => null, _ => false).Single();
        Assert.IsNull(combined.Biff12);
        Assert.AreEqual("3\r\nend", combined.Text);
    }

    [TestMethod]
    public void PlanGroupsAdjacentFormatsAfterDroppingInvalidPaths()
    {
        var first = ClipRecord.FromText("alpha\none\r\n");
        var second = ClipRecord.FromText("bravo\r\ntwo");
        var image = new ClipRecord { Kind = ClipKind.Image };
        var files = new ClipRecord { Kind = ClipKind.Files, Files = ["missing", "document", "folder"] };
        var invalid = files with { Files = ["missing"] };
        string? Image(ClipRecord _) => "screenshot.png";
        bool Exists(string path) => path != "missing";

        var segments = PanelPastePlan.Build([first, invalid, second, image, files, first], Image, Exists);
        CollectionAssert.AreEqual(new[] { ClipKind.Text, ClipKind.Files, ClipKind.Text }, segments.Select(s => s.Kind).ToArray());
        Assert.AreEqual("alpha\none\r\n\r\nbravo\r\ntwo", segments[0].Text);
        CollectionAssert.AreEqual(new[] { "screenshot.png", "document", "folder" }, segments[1].Files);
        Assert.AreEqual(first.Text, segments[2].Text);
        Assert.AreSame(image, PanelPastePlan.Build([image], Image, Exists).Single());
        CollectionAssert.AreEqual(new[] { "document", "folder" }, PanelPastePlan.Build([files], Image, Exists).Single().Files);
        CollectionAssert.AreEqual(new[] { "screenshot.png" }, PanelPastePlan.Build([image, invalid], Image, Exists).Single().Files);
        Assert.AreEqual(0, PanelPastePlan.Build([image, invalid], _ => null, Exists).Length);
        Assert.AreEqual(0, PanelPastePlan.Build([], Image, Exists).Length);
    }

    [TestMethod]
    public Task StagedScreenshotsAreReusedAndClearedWithoutDeletingOtherFiles() => RunSta(() =>
    {
        var directory = Directory.CreateTempSubdirectory("lertaro-panel-images-");
        try
        {
            var store = new HistoryStore(directory.FullName);
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 1, 2, 3, 255 }, 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            var png = stream.ToArray();
            var hash = ClipRecord.Fingerprint(ClipKind.Image, png);
            var image = new ClipRecord { Kind = ClipKind.Image, Hash = hash, ImageFile = hash.Split(':')[1] + ".png" };
            store.SaveImage(image, png);

            var path = PanelPasteImages.Resolve(store, image, stage: true)!;
            Assert.AreNotEqual(store.ImagePath(image), path);
            Assert.AreEqual(path, PanelPasteImages.Resolve(store, image, stage: true));
            CollectionAssert.AreEqual(png, File.ReadAllBytes(path));
            var staging = Path.GetDirectoryName(path)!;
            var unrelated = Path.Combine(staging, "other.png");
            File.WriteAllText(unrelated, "keep");
            var expired = Path.Combine(staging, new string('a', 64) + ".png");
            File.WriteAllBytes(expired, png);
            File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-2));
            Assert.AreEqual(path, PanelPasteImages.Resolve(store, image, stage: true));
            Assert.IsFalse(File.Exists(expired));

            PanelPasteImages.Clear(store);
            Assert.IsFalse(File.Exists(path));
            Assert.IsTrue(File.Exists(unrelated));
            Assert.IsTrue(File.Exists(store.ImagePath(image)));
        }
        finally { directory.Delete(recursive: true); }
    });

    [TestMethod]
    public Task PanelRetainsOnlyVisibleSelectionAndDoubleClickPastesOnce() => RunSta(() =>
    {
        var directory = Directory.CreateTempSubdirectory("lertaro-panel-test-");
        try
        {
            var store = new HistoryStore(directory.FullName);
            var latest = DateTimeOffset.UtcNow;
            var above = ClipRecord.FromText("上\n第一段") with { CopiedAt = latest };
            var middle = ClipRecord.FromText("中") with { CopiedAt = latest.AddMinutes(-1) };
            var below = ClipRecord.FromText("下\r\n第三段") with { CopiedAt = latest.AddMinutes(-2) };
            var files = new ClipRecord { Kind = ClipKind.Files, Category = "files", Hash = "files", CopiedAt = latest.AddMinutes(-3) };
            foreach (var record in new[] { above, middle, below, files }) store.Add(record, new());
            var pastes = new List<IReadOnlyList<ClipRecord>>();
            using var panel = new ClipboardPanel(store, pastes.Add, _ => { }, _ => { }, () => { }, () => { }, () => { });
            var list = (ListBox)panel.FindName("HistoryList");
            void Click(int index, ModifierKeys modifiers = ModifierKeys.None, int count = 1) =>
                panel.HandleRowClick((ClipboardRow)list.Items[index], count, modifiers);
            string[] SelectedIds() => list.SelectedItems.Cast<ClipboardRow>().Select(row => row.Record.Id).ToArray();

            panel.Open();
            Assert.AreEqual(0, list.SelectedItems.Count);
            panel.Reload();
            Assert.AreEqual(0, list.SelectedItems.Count);

            Click(2);
            Click(0, ModifierKeys.Shift);
            Click(1, ModifierKeys.Control);
            CollectionAssert.AreEquivalent(new[] { above.Id, below.Id }, SelectedIds());
            Click(0, ModifierKeys.Shift);
            CollectionAssert.AreEqual(new[] { below.Id }, SelectedIds());
            Click(0, ModifierKeys.Shift);
            panel.Reload();
            CollectionAssert.AreEquivalent(new[] { above.Id, below.Id }, SelectedIds());
            Click(2);
            CollectionAssert.AreEquivalent(new[] { above.Id, below.Id }, SelectedIds());
            Click(2, count: 2);
            Click(2, count: 3);
            Assert.AreEqual(1, pastes.Count);
            CollectionAssert.AreEqual(new[] { above, below }, pastes[0].ToArray());

            // WPF counts a preceding Shift-click as the first click in its mouse sequence.
            Click(2, ModifierKeys.Shift);
            Click(2, ModifierKeys.Shift);
            Click(2, count: 2);
            CollectionAssert.AreEquivalent(new[] { above.Id, below.Id }, SelectedIds());
            Click(2, count: 3);
            Click(2, count: 4);
            Assert.AreEqual(2, pastes.Count);
            CollectionAssert.AreEqual(new[] { above, below }, pastes[1].ToArray());

            Click(1);
            CollectionAssert.AreEqual(new[] { middle.Id }, SelectedIds());
            Click(0, ModifierKeys.Shift);
            Click(3, ModifierKeys.Shift);
            CollectionAssert.AreEquivalent(new[] { above.Id, middle.Id, files.Id }, SelectedIds());
            Click(3);
            Click(3, count: 2);
            CollectionAssert.AreEqual(new[] { above, middle, files }, pastes[2].ToArray());
            Click(0, ModifierKeys.Shift);
            CollectionAssert.AreEquivalent(new[] { middle.Id, files.Id }, SelectedIds());
            Click(0);
            store.Delete(above.Id);
            panel.Reload();
            Assert.AreEqual(0, list.SelectedItems.Count);
            Click(0);
            panel.Open();
            Assert.AreEqual(0, list.SelectedItems.Count);
            store.Clear();
            panel.Reload();
            Assert.AreEqual(0, list.Items.Count);
            Assert.IsFalse(((Button)panel.FindName("PasteButton")).IsEnabled);
        }
        finally { directory.Delete(recursive: true); }
    });

    private static Task RunSta(Action action)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); result.SetResult(); }
            catch (Exception ex) { result.SetException(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }
}
