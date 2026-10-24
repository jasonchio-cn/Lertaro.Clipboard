using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lertaro.PluginSdk.Windows;
using Lertaro.PluginSdk.Services;
using Lertaro.Plugins.Clipboard;

internal static class RenderUi
{
    internal static void Run(Application app, string output, string host, bool interactive = false)
    {
        host = Path.GetFullPath(host);
        if (!File.Exists(Path.Combine(host, "Lertaro.App.dll"))) throw new ArgumentException("A full Lertaro installation is required for UI resources.");
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            foreach (var file in new[] { Path.Combine(host, name.Name + ".dll"), Path.Combine(host, "Plugins", name.Name + ".dll") })
                if (File.Exists(file)) return Assembly.LoadFrom(file);
            return null;
        };
        var store = new HistoryStore(Path.Combine(output, "sample-data"));
        var samples = new[]
        {
            ClipRecord.FromText("明天的安排\n\n10:00 产品讨论\n14:30 确认界面细节\n\n把重要的内容留在手边。", "微信"),
            ClipRecord.FromText("https://github.com/Lertaro/Lertaro", "Vivaldi"),
            ClipRecord.FromText("public static string FormatTitle(string text)\n{\n    return text.Trim();\n}", "Code"),
            ClipRecord.FromText("发布前检查：快捷键、图片预览、搜索与类型筛选。", "Notepad"),
            ClipRecord.FromText("{\"project\":\"Lertaro Clipboard\",\"version\":\"0.2.0\",\"features\":[\"搜索历史\",\"类型标签\",\"内容预览\"],\"ready\":true}", "Code")
        };
        var when = DateTimeOffset.Now;
        for (var i = 0; i < samples.Length; i++) store.Add(samples[i] with { CopiedAt = when.AddMinutes(-i * 3) }, new());
        var fixture = Path.Combine(output, "sample-files"); Directory.CreateDirectory(fixture);
        var textFile = Path.Combine(fixture, "项目说明.txt"); File.WriteAllText(textFile, "Lertaro 剪贴板扩展\n\n文件预览\n\n• 在列表中选择需要查看的文件\n• 图片、文本和文件夹直接预览\n• 其他格式使用系统缩略图\n\n原始文件保持不变。");
        var jsonFile = Path.Combine(fixture, "配置.json"); File.WriteAllText(jsonFile, "{\"项目\":\"剪贴板\",\"预览\":true,\"版本\":\"0.3.0\"}");
        var pictureFile = Path.Combine(fixture, "界面设计.png");
        var childFolder = Path.Combine(fixture, "参考资料"); Directory.CreateDirectory(childFolder);
        File.WriteAllText(Path.Combine(childFolder, "阅读说明.txt"), "sample");
        var unsupported = Path.Combine(fixture, "样例.bin"); File.WriteAllBytes(unsupported, [0x4D, 0x5A, 0, 1, 2, 3]);
        var missing = Path.Combine(fixture, "已移动的文件.pdf");
        store.Add(new ClipRecord { Kind = ClipKind.Files, Category = "files", Files = [textFile, jsonFile, pictureFile, childFolder, unsupported, missing],
            Source = "explorer", Hash = "files-sample", Bytes = 120, CopiedAt = when.AddMinutes(-18) }, new());
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(42, 91, 141), Color.FromRgb(43, 149, 151), 35), null, new Rect(0, 0, 600, 360));
            drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(95, 240, 248, 255)), null, new Point(480, 60), 160, 160);
            drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(85, 255, 255, 255)), null, new Rect(65, 100, 380, 170), 22, 22);
        }
        var bitmap = new RenderTargetBitmap(600, 360, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var memory = new MemoryStream(); png.Save(memory);
        var bytes = memory.ToArray(); var hash = ClipRecord.Fingerprint(ClipKind.Image, bytes);
        File.WriteAllBytes(pictureFile, bytes);
        var thumbnailCalls = 0;
        IconService.GetThumbnailFunc = (_, _) => { Interlocked.Increment(ref thumbnailCalls); return bitmap; };
        var image = new ClipRecord { Kind = ClipKind.Image, Category = "image", Hash = hash, ImageFile = hash.Split(':')[1] + ".png",
            Width = 600, Height = 360, Bytes = bytes.Length, Source = "截图工具", CopiedAt = when.AddMinutes(-8) };
        store.SaveImage(image, bytes); store.Add(image, new()); store.Save([]);
        void SetTheme(string theme)
        {
            app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Lertaro.Plugins.CoreExtensions;component/Resources/Themes/" + theme + ".xaml") });
        }
        if (interactive)
        {
            app.Startup += (_, _) =>
            {
                SetTheme("Light");
                var backdrop = new Window { WindowStyle = WindowStyle.None, WindowState = WindowState.Maximized, ShowInTaskbar = false, Background = Brushes.White };
                backdrop.Show();
                var demo = ClipboardPanel.CreateWindow(); demo.Title = "剪贴板历史 · 示例"; demo.Owner = backdrop;
                var panel = new ClipboardPanel(store, _ => { }, _ => { }, _ => { }, () => { }, () => { }, demo.Close);
                demo.ContentHostControl.Content = panel;
                demo.ContentHostControl.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                demo.ContentHostControl.VerticalContentAlignment = VerticalAlignment.Stretch;
                demo.Closed += (_, _) => { backdrop.Close(); app.Shutdown(); };
                demo.Show();
            };
            app.Run(); return;
        }
        var checks = new List<string>();
        app.Startup += async (_, _) =>
        {
            try
            {
                foreach (var theme in new[] { "Light", "Dark" })
                {
                    SetTheme(theme);
                    var window = ClipboardPanel.CreateWindow();
                    var copies = 0; var pastes = 0;
                    var panel = new ClipboardPanel(store, _ => pastes++, _ => copies++, _ => { }, () => { }, () => { }, window.Hide);
                    window.ContentHostControl.Content = panel;
                    window.ContentHostControl.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    window.ContentHostControl.VerticalContentAlignment = VerticalAlignment.Stretch;
                    window.Show(); await Task.Delay(300);
                    var tabs = (StackPanel)panel.FindName("TypeTabs");
                    var list = (ListBox)panel.FindName("HistoryList");
                    var search = (TextBox)panel.FindName("SearchBox");
                    if (tabs.Children.Count != 7 || list.Items.Count != 7) throw new InvalidOperationException("Type tab or history count mismatch.");
                    var jsonTab = tabs.Children.OfType<RadioButton>().Single(t => (string)t.Tag == "json");
                    jsonTab.IsChecked = true;
                    if (list.Items.Count != 1 || ((ClipboardRow)list.Items[0]).Record.Category != "json") throw new InvalidOperationException("JSON filter failed.");
                    checks.Add(theme + ": JSON filter and selection");
                    Save(window, Path.Combine(output, "panel-" + theme.ToLowerInvariant() + "-json.png"));
                    var allTab = tabs.Children.OfType<RadioButton>().Single(t => (string)t.Tag == "all"); allTab.IsChecked = true;
                    Save(window, Path.Combine(output, "panel-" + theme.ToLowerInvariant() + ".png"));
                    var imageTab = tabs.Children.OfType<RadioButton>().Single(t => (string)t.Tag == "image"); imageTab.IsChecked = true;
                    await Task.Delay(250);
                    if (list.Items.Count != 1 || ((ClipboardRow)list.Items[0]).Thumbnail == null) throw new InvalidOperationException("Image thumbnail failed.");
                    Save(window, Path.Combine(output, "panel-" + theme.ToLowerInvariant() + "-image.png"));
                    var clipboardImage = ((ContentControl)panel.FindName("PreviewHost")).Content as ClipboardPreview;
                    if (clipboardImage?.ImagePreview is not { Source: BitmapSource, IsVisible: true } displayed || displayed.ActualHeight <= 0)
                        throw new InvalidOperationException("Clipboard image source was not visibly rendered.");
                    checks.Add(theme + ": image filter, thumbnail and preview");
                    var filesTab = tabs.Children.OfType<RadioButton>().Single(t => (string)t.Tag == "files"); filesTab.IsChecked = true;
                    var preview = (ClipboardPreview)((ContentControl)panel.FindName("PreviewHost")).Content;
                    var fileView = preview.FilePreview ?? throw new InvalidOperationException("File preview missing.");
                    await WaitFor(() => fileView.Current?.Path == textFile && fileView.Current.Kind == "text");
                    fileView.Picker.UpdateLayout();
                    if (!TextLabels(fileView.Picker).Contains("1. 项目说明.txt")) throw new InvalidOperationException("File picker shows an object or path instead of the display label.");
                    checks.Add(theme + ": collapsed file picker shows its friendly filename label");
                    Save(window, Path.Combine(output, "panel-" + theme.ToLowerInvariant() + "-files-text.png"));
                    checks.Add(theme + ": file text content preview");
                    fileView.Picker.SelectedIndex = 1;
                    await WaitFor(() => fileView.Current?.Path == jsonFile && fileView.Current.Text.Contains("剪贴板"));
                    fileView.Picker.UpdateLayout();
                    if (!TextLabels(fileView.Picker).Contains("2. 配置.json")) throw new InvalidOperationException("File picker label did not follow the selected file.");
                    checks.Add(theme + ": file picker display label follows selection");
                    checks.Add(theme + ": multi-file picker switches to formatted JSON");
                    fileView.Picker.IsDropDownOpen = true;
                    panel.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(window), Environment.TickCount, System.Windows.Input.Key.Enter)
                        { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                    if (pastes != 0) throw new InvalidOperationException("File selection Enter unexpectedly pasted the clipboard.");
                    fileView.Picker.IsDropDownOpen = false;
                    checks.Add(theme + ": file picker Enter does not trigger clipboard paste");
                    fileView.Picker.SelectedIndex = 2;
                    await WaitFor(() => fileView.Current?.Path == pictureFile && fileView.PreviewDescription == "图片预览 · 600 × 360");
                    Save(window, Path.Combine(output, "panel-" + theme.ToLowerInvariant() + "-files-image.png"));
                    checks.Add(theme + ": image file preview preserves original dimensions");
                    fileView.Picker.SelectedIndex = 3;
                    await WaitFor(() => fileView.Current?.Kind == "folder" && fileView.Current.Children?.Length == 1);
                    checks.Add(theme + ": folder child listing");
                    fileView.Picker.SelectedIndex = 4;
                    await WaitFor(() => fileView.Current?.Path == unsupported && fileView.PreviewDescription.StartsWith("系统缩略图"));
                    if (thumbnailCalls == 0) throw new InvalidOperationException("Host thumbnail SDK was not called.");
                    checks.Add(theme + ": unsupported files use SDK thumbnail service");
                    fileView.Picker.SelectedIndex = 5;
                    await WaitFor(() => fileView.Current?.Kind == "missing");
                    checks.Add(theme + ": deleted file handled without opening it");
                    fileView.Picker.SelectedIndex = 2; fileView.Picker.SelectedIndex = 0;
                    await WaitFor(() => fileView.Current?.Path == textFile && fileView.Current.Kind == "text");
                    checks.Add(theme + ": rapid file switching ignores stale loads");
                    allTab.IsChecked = true; search.Text = "发布前"; await Task.Delay(250);
                    if (list.Items.Count != 1) throw new InvalidOperationException("Search failed.");
                    ((Button)panel.FindName("CopyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (copies != 1) throw new InvalidOperationException("Copy callback failed.");
                    checks.Add(theme + ": search and copy callback");
                    search.Text = "no-match-value"; await Task.Delay(250);
                    if (((FrameworkElement)panel.FindName("EmptyList")).Visibility != Visibility.Visible || ((Button)panel.FindName("PasteButton")).IsEnabled)
                        throw new InvalidOperationException("Empty state failed.");
                    checks.Add(theme + ": empty state disables actions");
                    search.Text = ""; await Task.Delay(200); window.Width = 950; window.Height = 620; await Task.Delay(100);
                    Save(window, Path.Combine(output, "panel-" + theme.ToLowerInvariant() + "-small.png"));
                    panel.Dispose(); window.Close();
                    await HostPreviewChecks.Run(host, output, theme, checks);
                }
                File.WriteAllText(Path.Combine(output, "ui-checks.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "ui-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
            finally { app.Shutdown(); }
        };
        app.Run();
    }
    private static async Task WaitFor(Func<bool> ready)
    {
        for (var attempt = 0; attempt < 100; attempt++) { if (ready()) return; await Task.Delay(30); }
        throw new TimeoutException("File preview did not reach expected state.");
    }
    private static IEnumerable<string> TextLabels(DependencyObject parent)
    {
        if (parent is TextBlock text) yield return text.Text;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var label in TextLabels(VisualTreeHelper.GetChild(parent, i))) yield return label;
    }
    private static void Save(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); png.Save(output);
    }
}
