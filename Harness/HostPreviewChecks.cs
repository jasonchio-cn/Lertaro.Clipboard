using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lertaro.PluginSdk.Abstractions.Plugins.Preview;
using Lertaro.PluginSdk.Services;
using Lertaro.Plugins.Clipboard;

internal static class HostPreviewChecks
{
    internal static async Task Run(string host, string output, string theme, List<string> checks)
    {
        var bridgeFile = Directory.GetFiles(Path.Combine(host, "Plugins"), "Lertaro.Plugins.FlowLauncherBridge.dll", SearchOption.AllDirectories).Single();
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var path = Path.Combine(Path.GetDirectoryName(bridgeFile)!, name.Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        var bridge = Assembly.LoadFrom(bridgeFile);
        var adapter = (IFilePreviewProvider)Activator.CreateInstance(bridge.GetType("Lertaro.Plugins.FlowLauncherBridge.Providers.FlowPreviewProvider", true)!)!;
        var appAssembly = Assembly.LoadFrom(Path.Combine(host, "Lertaro.App.dll"));
        Application.Current.Resources.MergedDictionaries.Insert(0, new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Lertaro.App;component/Resources/Styles.xaml") });
        var store = new HistoryStore(Path.Combine(output, "host-preview-" + theme));
        var runtime = new ClipboardRuntime(() => store);
        await runtime.ClearAsync();
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.CornflowerBlue, null, new Rect(0, 0, 320, 160));
            drawing.DrawEllipse(Brushes.Gold, null, new Point(240, 60), 45, 45);
        }
        var image = new RenderTargetBitmap(320, 160, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze();
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var bytes = new MemoryStream(); png.Save(bytes);
        var hash = ClipRecord.Fingerprint(ClipKind.Image, bytes.ToArray());
        var record = new ClipRecord { Kind = ClipKind.Image, Category = "image", Hash = hash, ImageFile = hash.Split(':')[1] + ".png", Width = 320, Height = 160, Bytes = bytes.Length };
        store.Add(record, new());
        store.Add(ClipRecord.FromText("host text preview fixture"), new());
        var fixtureFile = Path.Combine(store.Root, "preview.txt"); File.WriteAllText(fixtureFile, "host file preview fixture");
        store.Add(new ClipRecord { Kind = ClipKind.Files, Category = "files", Files = [fixtureFile], Hash = "host-file-fixture", Bytes = 40 }, new());
        var provider = new ClipboardProvider(runtime);

        // Materialize rows through the installed host mapper, not a replacement model.
        var resultType = appAssembly.GetType("Lertaro.App.AppSearchResult", true)!;
        var mapper = appAssembly.GetType("Lertaro.App.ViewModels.Search.Mapping.PluginSearchResultMapper", true)!;
        IList Map()
        {
            var results = provider.GetInstantResults("clip /image").ToArray();
            var rows = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(resultType))!;
            mapper.GetMethod("AddInstantResultItems")!.Invoke(null, [rows, results, "", provider]);
            return rows;
        }
        // Start with a missing image to deterministically render the fallback icon,
        // then make the thumbnail available and verify the host replaces that row.
        var coldRows = Map();
        if (resultType.GetProperty("IconOverride")!.GetValue(coldRows[0]) is BitmapSource)
            throw new InvalidOperationException("Cold thumbnail fixture unexpectedly had a bitmap.");
        store.SaveImage(record, bytes.ToArray());
        var rows = Map();
        for (var i = 0; i < 100 && resultType.GetProperty("IconOverride")!.GetValue(rows[0]) is not BitmapSource; i++)
        { await Task.Delay(30); rows = Map(); }
        if (resultType.GetProperty("IconOverride")!.GetValue(rows[0]) is not BitmapSource icon || icon.PixelWidth > 80 || !icon.IsFrozen)
            throw new InvalidOperationException("Installed host mapper did not materialize the image thumbnail.");
        checks.Add(theme + ": installed host mapper displays virtual clipboard result thumbnail");

        var collectionType = appAssembly.GetType("Lertaro.App.Helpers.ObservableRangeCollection`1", true)!.MakeGenericType(resultType);
        var collection = (IList)Activator.CreateInstance(collectionType)!;
        foreach (var row in coldRows) collection.Add(row);
        var reconciler = appAssembly.GetType("Lertaro.App.ViewModels.Search.SearchResultsReconciler", true)!;
        var callback = typeof(Action<>).MakeGenericType(resultType);
        var noOp = System.Linq.Expressions.Expression.Lambda(callback, System.Linq.Expressions.Expression.Empty(),
            System.Linq.Expressions.Expression.Parameter(resultType)).Compile();
        reconciler.GetMethod("Replace")!.Invoke(null, [collection, rows, coldRows[0], noOp]);
        if (resultType.GetProperty("IconOverride")!.GetValue(collection[0]) is not BitmapSource)
            throw new InvalidOperationException("Installed host retained a stale fallback icon after the asynchronous refresh.");
        var stableRows = Map();
        if (!Equals(resultType.GetProperty("FullPath")!.GetValue(rows[0]), resultType.GetProperty("FullPath")!.GetValue(stableRows[0])))
            throw new InvalidOperationException("Ready thumbnail regenerated its preview token on every query.");
        checks.Add(theme + ": installed host reconciliation replaces cold thumbnail once and retains ready result identity");

        var quickLookType = appAssembly.GetType("Lertaro.App.Views.QuickLook.QuickLookWindow", true)!;
        foreach (var query in new[] { "clip /image", "clip /text", "clip /files" })
        {
            var result = provider.GetInstantResults(query).First();
            // This row is only used for preview routing, so explicitly release any
            // native icon that would otherwise be owned by the search mapper.
            if (result.HBitmapIcon != IntPtr.Zero) ClipboardThumbnails.DeleteObject(result.HBitmapIcon);
            if (!adapter.CanPreview(result.ActionArgument, false)) throw new InvalidOperationException("Host adapter did not accept SDK preview token.");
            var content = (ClipboardPreview)adapter.CreatePreview(result.ActionArgument, false);
            var window = (Window)Activator.CreateInstance(quickLookType)!;
            var area = (ContentControl)window.FindName("ContentArea"); area.Content = content;
            window.Show();
            try
            {
                if (query.EndsWith("/image"))
                {
                    for (var i = 0; i < 100 && content.ImagePreview?.Source == null; i++) await Task.Delay(30);
                    window.UpdateLayout();
                    if (content.ImagePreview is not { Source: BitmapSource, IsVisible: true } photo || photo.ActualHeight <= 0 || photo.ActualWidth <= 0)
                        throw new InvalidOperationException("Host adapter collapsed or failed to display the clipboard image.");
                    checks.Add(theme + ": installed Flow adapter and QuickLook show clipboard image content");
                }
                else if (query.EndsWith("/text"))
                {
                    window.UpdateLayout();
                    if (!Descendants(content).OfType<TextBox>().Any(box => box.Text == "host text preview fixture" && box.IsVisible && box.ActualHeight > 0))
                        throw new InvalidOperationException("Host adapter collapsed clipboard text content.");
                    checks.Add(theme + ": installed Flow adapter and QuickLook show clipboard text content");
                }
                else
                {
                    for (var i = 0; i < 100 && content.FilePreview?.Current == null; i++) await Task.Delay(30);
                    window.UpdateLayout();
                    if (!Descendants(content).OfType<TextBox>().Any(box => box.Text == "host file preview fixture" && box.IsVisible && box.ActualHeight > 0))
                        throw new InvalidOperationException("Host adapter collapsed clipboard file content.");
                    checks.Add(theme + ": installed Flow adapter and QuickLook show clipboard file content");
                }
            }
            finally { area.Content = null; window.Close(); }
        }
        await runtime.StopAsync();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
}
