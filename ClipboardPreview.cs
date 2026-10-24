using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lertaro.Plugins.Clipboard;

internal sealed class ClipboardPreview : UserControl
{
    internal FileClipboardPreview? FilePreview { get; }
    internal bool IsFilePickerOpen => FilePreview?.IsPickerOpen == true;
    internal Image? ImagePreview { get; }
    internal ClipboardPreview(ClipRecord item, HistoryStore store)
    {
        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var info = new TextBlock { Text = $"{ClipboardProvider.Label(item.Category)} · {item.CopiedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n来源：{item.Source} · " +
            (item.Kind == ClipKind.Files ? $"共 {item.Files.Length} 项" : $"{item.Bytes:N0} 字节") +
            (item.Kind == ClipKind.Text ? $" · {item.Text.Length:N0} 字符" : ""), Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
        info.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); grid.Children.Add(info);
        if (item.Kind == ClipKind.Files)
        {
            FilePreview = new FileClipboardPreview(item.Files);
            Grid.SetRow(FilePreview, 1); grid.Children.Add(FilePreview);
        }
        else if (item.Kind == ClipKind.Image)
        {
            var image = ImagePreview = new Image { Stretch = Stretch.Uniform };
            Grid.SetRow(image, 1); grid.Children.Add(image);
            Loaded += async (_, _) =>
            {
                try
                {
                    var bitmap = await Task.Run(() =>
                    {
                        using var input = File.OpenRead(store.ImagePath(item));
                        var bmp = new BitmapImage(); bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = input; bmp.EndInit(); bmp.Freeze(); return bmp;
                    });
                    image.Source = bitmap;
                }
                catch (Exception ex) { info.Text += "\n图片加载失败：" + ex.Message; }
            };
        }
        else
        {
            var text = item.Kind == ClipKind.Files ? string.Join(Environment.NewLine, item.Files) : item.Text;
            if (item.Category == "json")
            {
                try { using var doc = JsonDocument.Parse(text); text = JsonSerializer.Serialize(doc.RootElement,
                    new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }); }
                catch (JsonException) { }
            }
            var box = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true,
                TextWrapping = item.Category is "code" or "json" ? TextWrapping.NoWrap : TextWrapping.Wrap,
                FontFamily = new FontFamily(item.Category is "code" or "json" ? "Cascadia Mono, Consolas" : "Microsoft YaHei UI"),
                FontSize = 14, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(0, 8, 0, 0),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            box.SetResourceReference(TextBox.ForegroundProperty, "TextPrimary");
            Grid.SetRow(box, 1); grid.Children.Add(box);
        }
        // Keep our layout inside a single surface. Generic host preview adapters
        // normalize top-level grids and may otherwise collapse the content row.
        Content = new Border { Child = grid };
    }
}
