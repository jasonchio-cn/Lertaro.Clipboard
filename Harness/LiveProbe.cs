using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinClipboard = System.Windows.Clipboard;

internal static class LiveProbe
{
    internal static void Run(Application app, string root)
    {
        // Only the disposable receiver lives here; the running Lertaro handles the real global keys.
        var original = new DataObject();
        var previous = WinClipboard.GetDataObject();
        if (previous != null) foreach (var format in previous.GetFormats(false))
        {
            try
            {
                var value = previous.GetData(format, false);
                if (value is MemoryStream stream) value = new MemoryStream(stream.ToArray());
                if (value != null) original.SetData(format, value);
            }
            catch { }
        }
        var events = new List<object>();
        var editor = new RichTextBox { AcceptsTab = true, FontSize = 18, MinHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var status = new TextBlock { Text = "测试窗口已准备好", Margin = new Thickness(0, 10, 0, 0) };
        var a = "验证A｜中文🙂";
        var b = "验证B｜第二条";
        var files = new[] { Path.Combine(root, "验证文件一.txt"), Path.Combine(root, "验证文件二.txt") };
        File.WriteAllText(files[0], "first test file"); File.WriteAllText(files[1], "second test file");
        void Save()
        {
            var report = new { Text = new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text,
                Events = events.ToArray(), ExpectedA = a, ExpectedB = b, ExpectedFiles = files };
            var path = Path.Combine(root, "live-state.json"); var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, true);
        }
        editor.TextChanged += (_, _) => Save();
        DataObject.AddPastingHandler(editor, (_, e) =>
        {
            var source = e.SourceDataObject;
            if (source.GetDataPresent(DataFormats.FileDrop))
            {
                var received = source.GetData(DataFormats.FileDrop) as string[] ?? [];
                events.Add(new { Kind = "files", Files = received });
                status.Text = $"已接收 {received.Length} 个文件路径";
                e.CancelCommand();
            }
            else if (source.GetDataPresent("PNG") || source.GetDataPresent(DataFormats.Bitmap))
            {
                BitmapSource? image = null;
                if (source.GetData("PNG") is MemoryStream png)
                {
                    using var input = new MemoryStream(png.ToArray());
                    image = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                }
                image ??= source.GetData(DataFormats.Bitmap) as BitmapSource;
                if (image == null) throw new InvalidDataException("Image paste did not expose pixels.");
                var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
                var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
                converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
                events.Add(new { Kind = "image", Width = image.PixelWidth, Height = image.PixelHeight,
                    Alpha = Enumerable.Range(0, pixels.Length / 4).Select(i => pixels[i * 4 + 3]).ToArray() });
                status.Text = $"已接收图片 {image.PixelWidth}×{image.PixelHeight}";
                e.CancelCommand();
            }
            else
            {
                var text = source.GetData(DataFormats.UnicodeText) as string ?? source.GetData(DataFormats.Text) as string ?? "";
                events.Add(new { Kind = "text", Text = text }); status.Text = $"已接收文本；共 {events.Count} 次粘贴";
            }
            Save();
        });
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Lertaro 快捷键验证", FontSize = 23, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "只操作本窗口的临时内容；Ctrl+Shift+D 打开历史，Ctrl+Shift+V 按复制顺序粘贴。", Margin = new Thickness(0, 8, 0, 16), TextWrapping = TextWrapping.Wrap });
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        void Button(string label, Action action)
        {
            var button = new Button { Content = label, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 8) };
            button.Click += (_, _) => { action(); editor.Focus(); Save(); }; buttons.Children.Add(button);
        }
        Button("复制 A", () => { WinClipboard.SetText(a); status.Text = "已复制测试 A"; });
        Button("复制 B", () => { WinClipboard.SetText(b); status.Text = "已复制测试 B"; });
        Button("复制透明图片", () =>
        {
            var pixels = new byte[] { 0, 0, 255, 128, 0, 255, 0, 255, 255, 0, 0, 255, 0, 0, 0, 0 };
            var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = new MemoryStream(); encoder.Save(output);
            var data = new DataObject(); data.SetImage(image); data.SetData("PNG", new MemoryStream(output.ToArray()));
            WinClipboard.SetDataObject(data, true); status.Text = "已复制 2×2 透明图片";
        });
        Button("复制两个文件", () =>
        {
            var list = new System.Collections.Specialized.StringCollection(); list.AddRange(files);
            WinClipboard.SetFileDropList(list); status.Text = "已复制两个临时文件";
        });
        Button("清空测试输入", () => { editor.Document.Blocks.Clear(); events.Clear(); status.Text = "测试输入已清空"; });
        panel.Children.Add(buttons); panel.Children.Add(editor); panel.Children.Add(status);
        var window = new Window { Title = "Lertaro 快捷键验证（临时窗口）", Width = 820, Height = 420, Content = panel };
        window.Loaded += (_, _) => { editor.Focus(); Save(); };
        window.Closed += (_, _) =>
        {
            try { WinClipboard.SetDataObject(original, true); File.WriteAllText(Path.Combine(root, "clipboard-restored.txt"), "restored"); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(root, "restore-error.txt"), ex.Message); }
            app.Shutdown();
        };
        window.Show(); app.Run();
    }
}
