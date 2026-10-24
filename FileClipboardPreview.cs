using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lertaro.PluginSdk.Services;

namespace Lertaro.Plugins.Clipboard;

internal sealed class FileClipboardPreview : UserControl
{
    private readonly ComboBox _picker;
    private readonly TextBlock _details;
    private readonly ContentControl _content;
    private readonly TextBlock _caption;
    private readonly Button _open, _reveal;
    private CancellationTokenSource? _load;
    internal ComboBox Picker => _picker;
    internal bool IsPickerOpen => _picker.IsDropDownOpen;
    internal FilePreviewData? Current { get; private set; }
    internal string PreviewDescription => _caption.Text;

    internal FileClipboardPreview(string[] paths)
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Lertaro.Plugins.Clipboard;component/ClipboardControls.xaml") });
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _picker = new ComboBox { ItemsSource = paths.Select((path, index) => new FileChoice(path, $"{index + 1}. {System.IO.Path.GetFileName(path.TrimEnd('\\'))}")).ToArray(),
            DisplayMemberPath = nameof(FileChoice.Label), MinHeight = 34, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 10), IsTextSearchEnabled = true };
        System.Windows.Automation.AutomationProperties.SetName(_picker, "选择要预览的文件");
        _picker.SetResourceReference(StyleProperty, "ClipboardFilePicker");
        grid.Children.Add(_picker);
        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _details = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        _details.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); header.Children.Add(_details);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        _open = MakeButton("打开", OpenSelected); _reveal = MakeButton("所在位置", RevealSelected);
        actions.Children.Add(_open); actions.Children.Add(_reveal); Grid.SetColumn(actions, 1); header.Children.Add(actions);
        Grid.SetRow(header, 1); grid.Children.Add(header);
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _caption = new TextBlock { FontSize = 11, Margin = new Thickness(0, 0, 0, 8) };
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); body.Children.Add(_caption);
        _content = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        Grid.SetRow(_content, 1); body.Children.Add(_content); Grid.SetRow(body, 2); grid.Children.Add(body);
        Content = grid;
        _picker.SelectionChanged += (_, _) => { if (IsLoaded) _ = LoadSelected(); };
        _picker.SelectedIndex = paths.Length > 0 ? 0 : -1;
        Loaded += (_, _) =>
        {
            _ = LoadSelected();
        };
        Unloaded += (_, _) => { _load?.Cancel(); };
    }

    private Button MakeButton(string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(5, 0, 0, 0), MinWidth = 0, FontSize = 11 };
        button.SetResourceReference(StyleProperty, "ClipboardPreviewButton");
        button.Click += (_, _) => action(); return button;
    }
    private void OpenSelected()
    {
        if (_picker.SelectedItem is not FileChoice choice) return;
        try
        {
            if (Directory.Exists(choice.Path)) ExplorerService.OpenFolder(choice.Path);
            else if (File.Exists(choice.Path)) Process.Start(new ProcessStartInfo(choice.Path) { UseShellExecute = true });
        }
        catch (Exception ex) { ClipboardRuntime.Notify("无法打开文件", ex.Message); }
    }
    private void RevealSelected()
    {
        if (_picker.SelectedItem is not FileChoice choice) return;
        try
        {
            var parent = System.IO.Path.GetDirectoryName(choice.Path);
            if (!string.IsNullOrEmpty(parent)) ExplorerService.OpenDirectory(parent, choice.Path);
        }
        catch (Exception ex) { ClipboardRuntime.Notify("无法定位文件", ex.Message); }
    }
    private async Task LoadSelected()
    {
        _load?.Cancel(); _load?.Dispose(); _load = new(); var token = _load.Token;
        Current = null; _open.IsEnabled = _reveal.IsEnabled = false;
        if (_picker.SelectedItem is not FileChoice choice) { ShowMessage("没有可预览的文件。"); return; }
        _picker.ToolTip = choice.Path; _caption.Text = "正在读取文件…"; _content.Content = null;
        try
        {
            var data = await Task.Run(() => FilePreviewReader.Read(choice.Path, token), token);
            if (token.IsCancellationRequested || !IsLoaded) return;
            Current = data;
            _details.Text = (data.Kind == "folder" ? "文件夹" : FormatSize(data.Size)) +
                (data.Modified is { } modified ? $" · {modified:yyyy-MM-dd HH:mm}" : "");
            _details.ToolTip = data.Path;
            _open.IsEnabled = _reveal.IsEnabled = data.Kind != "missing";
            switch (data.Kind)
            {
                case "missing": ShowMessage(data.Error); return;
                case "text":
                    _caption.Text = data.Truncated ? "内容预览 · 已截取前 100,000 个字符" : "内容预览";
                    _content.Content = TextContent(data.Text); return;
                case "folder":
                    _caption.Text = data.Truncated ? "文件夹内容 · 显示前 200 项" : $"文件夹内容 · {data.Children?.Length ?? 0} 项";
                    _content.Content = FolderContent(data.Children ?? []); return;
                case "image":
                    var image = await Task.Run(() => LoadImage(data.Path), token);
                    if (token.IsCancellationRequested || !IsLoaded) return;
                    if (image != null)
                    {
                        _caption.Text = $"图片预览 · {image.Width} × {image.Height}";
                        _content.Content = new Image { Source = image.Bitmap, Stretch = Stretch.Uniform }; return;
                    }
                    break;
            }
            var thumbnail = await Task.Run(() =>
            {
                var result = IconService.GetThumbnail(data.Path, 768);
                if (result is { IsFrozen: false, CanFreeze: true }) result.Freeze();
                return result;
            }, token);
            if (token.IsCancellationRequested || !IsLoaded) return;
            _caption.Text = "系统缩略图 · 可用性取决于 Windows 已安装的处理器";
            if (thumbnail != null) _content.Content = new Image { Source = thumbnail, Stretch = Stretch.Uniform };
            else ShowMessage("此格式暂不支持内嵌内容预览。\n可点击“打开”或“所在位置”查看。", keepCaption: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { if (!token.IsCancellationRequested && IsLoaded) ShowMessage("预览失败：" + ex.Message); }
    }

    private sealed record FileImage(BitmapSource Bitmap, int Width, int Height);
    private static FileImage? LoadImage(string path)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var width = decoder.Frames[0].PixelWidth; var height = decoder.Frames[0].PixelHeight;
            if (width < 1 || height < 1 || (long)width * height > 64L * 1024 * 1024) return null;
            var scale = Math.Min(1d, 1600d / Math.Max(width, height));
            input.Position = 0;
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = Math.Max(1, (int)(width * scale)); image.StreamSource = input; image.EndInit(); image.Freeze();
            return new(image, width, height);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or FileFormatException) { return null; }
    }
    private static TextBox TextContent(string text)
    {
        var box = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13, TextWrapping = TextWrapping.NoWrap, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Top, HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        box.SetResourceReference(TextBox.ForegroundProperty, "TextPrimary"); return box;
    }
    private static UIElement FolderContent(string[] paths)
    {
        if (paths.Length == 0) return new TextBlock { Text = "这是一个空文件夹。", Opacity = 0.6, Margin = new Thickness(0, 12, 0, 0) };
        var list = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, IsTabStop = false };
        foreach (var path in paths) list.Items.Add(System.IO.Path.GetFileName(path));
        return list;
    }
    private void ShowMessage(string message, bool keepCaption = false)
    {
        if (!keepCaption) _caption.Text = "文件预览";
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 16, 0, 0), Opacity = 0.75 };
        _content.Content = text;
    }
    private static string FormatSize(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024:N1} MB" : bytes >= 1024 ? $"{bytes / 1024d:N1} KB" : $"{bytes:N0} 字节";
    private sealed record FileChoice(string Path, string Label);
}
