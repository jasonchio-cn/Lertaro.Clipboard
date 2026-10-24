using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lertaro.Plugins.Clipboard.Tests;

[TestClass]
public class MaintenanceTests
{
    private string _root = null!;
    [TestInitialize]
    public void Init() => _root = Path.Combine(Path.GetTempPath(), "LertaroMaintenanceTests", Guid.NewGuid().ToString("N"));
    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private HistoryStore Fixture()
    {
        var store = new HistoryStore(_root);
        store.Add(ClipRecord.FromText("retained history fixture"), new());
        byte[] png = [1, 2, 3];
        var hash = ClipRecord.Fingerprint(ClipKind.Image, png);
        var image = new ClipRecord { Kind = ClipKind.Image, Hash = hash, ImageFile = hash.Split(':')[1] + ".png", Bytes = png.Length };
        store.SaveImage(image, png); store.Add(image, new()); store.Save([]);
        File.WriteAllText(Path.Combine(_root, "images", "unrelated.png"), "not a plugin asset");
        return store;
    }
    private static void AssertStopped(ClipboardRuntime runtime)
    {
        Assert.IsFalse(runtime.Ready);
        Assert.IsFalse(runtime.HasMonitorResources);
        Assert.AreEqual(IntPtr.Zero, runtime.ListenerHandle);
    }

    [TestMethod]
    public async Task ClearWhileInitiallyDisabledLoadsHistoryAndDeletesOwnedAssetsWithoutMonitoring()
    {
        Fixture(); var loads = 0;
        var sequence = new PasteSequence(); sequence.Add(ClipRecord.FromText("queued fixture"), new());
        var runtime = new ClipboardRuntime(() => { loads++; return new HistoryStore(_root); }, sequence);
        Assert.IsNull(runtime.Store); AssertStopped(runtime);
        await runtime.ClearAsync();
        Assert.AreEqual(1, loads);
        Assert.AreEqual(0, new HistoryStore(_root).Snapshot().Length);
        Assert.AreEqual(0, sequence.PendingCount);
        CollectionAssert.AreEqual(new[] { "unrelated.png" }, Directory.GetFiles(Path.Combine(_root, "images")).Select(Path.GetFileName).ToArray());
        AssertStopped(runtime);
        await runtime.StopAsync();
    }

    [TestMethod]
    public async Task ResetWhileInitiallyDisabledDoesNotLoadOrModifyHistory()
    {
        Fixture(); var original = File.ReadAllBytes(Path.Combine(_root, "history.json")); var loads = 0;
        var sequence = new PasteSequence(); sequence.Add(ClipRecord.FromText("queued fixture"), new());
        var runtime = new ClipboardRuntime(() => { loads++; return new HistoryStore(_root); }, sequence);
        await runtime.ResetSequenceAsync();
        Assert.AreEqual(0, loads); Assert.IsNull(runtime.Store); Assert.AreEqual(0, sequence.PendingCount);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(Path.Combine(_root, "history.json")));
        AssertStopped(runtime); await runtime.StopAsync();
    }

    [TestMethod]
    public async Task ResetClearsFrozenAndPendingCopiesWhilePreservingLoadedHistory()
    {
        Fixture(); var store = new HistoryStore(_root);
        var sequence = new PasteSequence(); var runtime = new ClipboardRuntime(() => store, sequence);
        // Load through the same maintenance path, then create a new history fixture.
        await runtime.ClearAsync();
        store.Add(ClipRecord.FromText("keep"), new()); store.Save([]);
        foreach (var text in new[] { "A", "B", "A" }) sequence.Add(ClipRecord.FromText(text), new());
        sequence.Peek(); sequence.Advance(); sequence.Add(ClipRecord.FromText("next batch"), new());
        await runtime.ResetSequenceAsync();
        Assert.AreEqual(0, sequence.PendingCount); Assert.IsNull(sequence.Peek());
        Assert.AreEqual("keep", new HistoryStore(_root).Snapshot().Single().Text);
        AssertStopped(runtime); await runtime.StopAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MaintenanceInvalidatesOldPasteCompletionWithoutAdvancingNewBatch(bool clear)
    {
        var sequence = new PasteSequence(); sequence.Add(ClipRecord.FromText("old"), new());
        var runtime = new ClipboardRuntime(Fixture, sequence);
        var old = runtime.PeekSequence();
        Assert.AreEqual("old", old.Item!.Text);
        if (clear) await runtime.ClearAsync(); else await runtime.ResetSequenceAsync();
        sequence.Add(ClipRecord.FromText("new"), new());
        var current = runtime.PeekSequence();
        Assert.IsFalse(runtime.TryAdvanceSequence(old.Generation, out _));
        Assert.AreEqual("new", sequence.Peek()!.Text);
        Assert.IsTrue(runtime.TryAdvanceSequence(current.Generation, out _));
        Assert.IsNull(sequence.Peek());
        AssertStopped(runtime); await runtime.StopAsync();
    }

    [TestMethod]
    public async Task MaintenanceRequestsShareOneLoadAndSerializeTheirWrites()
    {
        Fixture(); var loads = 0;
        var runtime = new ClipboardRuntime(() => { Interlocked.Increment(ref loads); return new HistoryStore(_root); });
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => i % 2 == 0 ? runtime.ClearAsync() : runtime.ResetSequenceAsync()));
        Assert.AreEqual(1, loads); Assert.AreEqual(0, new HistoryStore(_root).Snapshot().Length);
        AssertStopped(runtime); await runtime.StopAsync();
    }

    [TestMethod]
    public async Task SaveFailureIsReturnedAndRetryCanPersistSuccessfully()
    {
        Fixture(); var store = new HistoryStore(_root); var runtime = new ClipboardRuntime(() => store);
        var index = Path.Combine(_root, "history.json");
        var failed = false;
        using (var locked = new FileStream(index, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try { await runtime.ClearAsync(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
        }
        Assert.IsTrue(failed, "A locked history file must report failure to the caller.");
        Assert.AreEqual(2, new HistoryStore(_root).Snapshot().Length);
        Assert.IsTrue(Directory.GetFiles(Path.Combine(_root, "images"), "*.png").Length > 1);
        await runtime.ClearAsync();
        Assert.AreEqual(0, new HistoryStore(_root).Snapshot().Length);
        AssertStopped(runtime); await runtime.StopAsync();
    }

    [TestMethod]
    public async Task FailedColdLoadIsReportedAndCanBeRetriedWithoutMonitoring()
    {
        Fixture(); var fail = true; var loads = 0;
        var runtime = new ClipboardRuntime(() =>
        {
            loads++;
            if (fail) throw new IOException("fixture load failed");
            return new HistoryStore(_root);
        });
        await Assert.ThrowsExceptionAsync<IOException>(() => runtime.ClearAsync());
        Assert.IsNull(runtime.Store); AssertStopped(runtime);
        Assert.AreEqual(2, new HistoryStore(_root).Snapshot().Length);
        fail = false; await runtime.ClearAsync();
        Assert.AreEqual(2, loads); Assert.AreEqual(0, new HistoryStore(_root).Snapshot().Length);
        await runtime.StopAsync();
    }

    [TestMethod]
    public async Task UnresolvedMaintenanceFailureIsPropagatedBeforeSettingsTransfer()
    {
        var runtime = new ClipboardRuntime(() => throw new IOException("fixture load failed"));
        await Assert.ThrowsExceptionAsync<IOException>(() => runtime.ClearAsync());
        await Assert.ThrowsExceptionAsync<IOException>(() => runtime.StopAsync());
        AssertStopped(runtime);
    }

    [TestMethod]
    public async Task StopWaitsForAcceptedMaintenanceAndRejectsLaterRequests()
    {
        Fixture(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var runtime = new ClipboardRuntime(() =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("maintenance fixture release");
            return new HistoryStore(_root);
        });
        var clearing = runtime.ClearAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = runtime.StopAsync();
        try
        {
            Assert.IsFalse(stopping.IsCompleted);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => runtime.ClearAsync());
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => runtime.ResetSequenceAsync());
        }
        finally { release.Set(); }
        await Task.WhenAll(clearing, stopping).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, new HistoryStore(_root).Snapshot().Length); AssertStopped(runtime);
    }
}
