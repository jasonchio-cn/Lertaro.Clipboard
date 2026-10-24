using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lertaro.Plugins.Clipboard.Tests;

[TestClass]
public class ThumbnailTests
{
    private string _root = null!;
    [TestInitialize] public void Init() { _root = Path.Combine(Path.GetTempPath(), "LertaroThumbnailTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() => Directory.Delete(_root, true);
    private string Image(string name, int width, int height)
    {
        var pixels = Enumerable.Repeat((byte)180, width * height * 4).ToArray();
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(_root, name); using var output = File.Create(path); png.Save(output); return path;
    }

    [TestMethod]
    [DataRow(900, 90)]
    [DataRow(90, 900)]
    public async Task ThumbnailIsFrozenAndFitsBothDimensions(int width, int height)
    {
        var cache = new ClipboardThumbnails(); var path = Image("fixture.png", width, height);
        var task = cache.GetAsync(path); var image = await task;
        Assert.IsNotNull(image); Assert.IsTrue(image.IsFrozen);
        Assert.IsTrue(image.PixelWidth <= ClipboardThumbnails.Edge && image.PixelHeight <= ClipboardThumbnails.Edge);
        Assert.AreSame(task, cache.GetAsync(path));
    }

    [TestMethod]
    public async Task CachedThumbnailProvidesFreshSdkOwnedHandles()
    {
        var cache = new ClipboardThumbnails(); var path = Image("fixture.png", 160, 100);
        await cache.GetAsync(path);
        var first = cache.GetHBitmap(path, () => { }, false);
        var second = cache.GetHBitmap(path, () => { }, false);
        try
        {
            Assert.AreNotEqual(IntPtr.Zero, first); Assert.AreNotEqual(IntPtr.Zero, second); Assert.AreNotEqual(first, second);
            var materialized = Imaging.CreateBitmapSourceFromHBitmap(first, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            Assert.AreEqual(80, materialized.PixelWidth); Assert.AreEqual(50, materialized.PixelHeight);
        }
        finally { if (first != IntPtr.Zero) ClipboardThumbnails.DeleteObject(first); if (second != IntPtr.Zero) ClipboardThumbnails.DeleteObject(second); }
    }

    [TestMethod]
    public async Task CompletedThumbnailRequestsARefreshAndFailedEntryCanRecover()
    {
        var cache = new ClipboardThumbnails(); var path = Path.Combine(_root, "later.png");
        Assert.IsNull(await cache.GetAsync(path));
        Image("later.png", 80, 40);
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.IsNotNull(await cache.GetAsync(path, () => refreshed.TrySetResult()));
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task MissingAndMalformedImagesFallBackWithoutFaulting()
    {
        var cache = new ClipboardThumbnails();
        Assert.IsNull(await cache.GetAsync(Path.Combine(_root, "missing.png")));
        var path = Path.Combine(_root, "broken.png"); File.WriteAllText(path, "not a PNG");
        Assert.IsNull(await cache.GetAsync(path));
    }
}
