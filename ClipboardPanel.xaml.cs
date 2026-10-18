using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lertaro.PluginSdk.Windows;

namespace Lertaro.Plugins.Clipboard;

public partial class ClipboardPanel : UserControl, IDisposable
{
    private readonly HistoryStore _store;
    private readonly Action<ClipRecord> _paste, _copy;
    private readonly Action<string> _delete;
    private readonly Action _reset, _clear, _close;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource _images = new();
    private readonly Dictionary<string, RadioButton> _tabs = [];
    private string _category = "all";
    private bool _disposed;
    private static readonly SemaphoreSlim ThumbnailSlots = new(3);

    internal static PluginWindow CreateWindow()
    {
        var window = new PluginWindow("剪贴板历史", 1120, 760, PluginWindowMode.Window)
        { ShowFooter = false, MinWidth = 950, MinHeight = 620, ResizeMode = ResizeMode.CanResize };
        window.ContentHostControl.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        window.ContentHostControl.VerticalContentAlignment = VerticalAlignment.Stretch;
        // A text close mark remains legible when a theme's optional icon font is unavailable.
        window.Loaded += (_, _) =>
        {
            void Visit(DependencyObject node)
            {
                if (node is Button button && button.Content is string glyph && glyph == "\uE8BB")
                { button.Content = "×"; button.FontFamily = new FontFamily("Segoe UI"); button.FontSize = 19; }
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i));
            }
            Visit(window);
        };
        return window;
    }

    internal ClipboardPanel(HistoryStore store, Action<ClipRecord> paste, Action<ClipRecord> copy, Action<string> delete,
        Action reset, Action clear, Action close)
    {
        _store = store; _paste = paste; _copy = copy; _delete = delete; _reset = reset; _clear = clear; _close = close;
        InitializeComponent();
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(140), DispatcherPriority.Background,
            (sender, _) => { ((DispatcherTimer)sender!).Stop(); Reload(); }, Dispatcher); _debounce.Stop();
        foreach (var (key, name) in ClipboardProvider.Categories)
        {
            var tab = new RadioButton { Tag = key, Content = name, GroupName = "ClipboardTypes", Style = (Style)Resources["TypeTab"] };
            System.Windows.Automation.AutomationProperties.SetName(tab, name);
            tab.Checked += (_, _) => { _category = key; if (IsLoaded) Reload(); };
            _tabs.Add(key, tab); TypeTabs.Children.Add(tab);
        }
        _tabs["all"].IsChecked = true;
        HistoryList.ContextMenu = new ContextMenu();
        PreviewKeyDown += HandleKeys;
        Loaded += (_, _) => { Reload(); FocusSearch(); };
    }

    internal void FocusSearch() => Dispatcher.BeginInvoke(() => { SearchBox.Focus(); SearchBox.SelectAll(); }, DispatcherPriority.Input);
    internal void Reload()
    {
        if (_disposed) return;
        _images.Cancel(); _images.Dispose(); _images = new();
        var token = _images.Token;
        var selected = (HistoryList.SelectedItem as ClipboardRow)?.Record.Id;
        var search = SearchBox.Text;
        var all = _store.Snapshot();
        var searched = all.Where(x => ClipboardProvider.Matches(x, search)).ToArray();
        foreach (var (key, name) in ClipboardProvider.Categories)
        {
            var count = key == "all" ? searched.Length : searched.Count(x => x.Category == key);
            _tabs[key].Content = $"{name}  {count}";
        }
        var rows = searched.Where(x => _category == "all" || x.Category == _category).Select(x => new ClipboardRow(x)).ToArray();
        HistoryList.ItemsSource = rows;
        HistoryList.SelectedItem = rows.FirstOrDefault(x => x.Record.Id == selected) ?? rows.FirstOrDefault();
        if (HistoryList.SelectedItem != null) HistoryList.ScrollIntoView(HistoryList.SelectedItem);
        EmptyList.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListHeading.Text = $"最近复制 · {ClipboardProvider.Label(_category)} · {rows.Length} 条";
        RecordStatus.Text = rows.Length == 0 ? "复制内容后，会自动记录在这里" : $"共 {all.Length} 条历史 · 已显示 {rows.Length} 条";
        UpdatePreview();
        foreach (var row in rows.Where(x => x.Record.Kind == ClipKind.Image)) _ = LoadThumbnail(row, token);
    }

    private async Task LoadThumbnail(ClipboardRow row, CancellationToken token)
    {
        try
        {
            await ThumbnailSlots.WaitAsync(token);
            try
            {
                var image = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    using var input = File.OpenRead(_store.ImagePath(row.Record));
                    var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 80; bitmap.StreamSource = input; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
                }, token);
                if (!token.IsCancellationRequested && !_disposed) row.Thumbnail = image;
            }
            finally { ThumbnailSlots.Release(); }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or NotSupportedException or System.Runtime.InteropServices.COMException) { }
    }

    private void UpdatePreview()
    {
        var item = (HistoryList.SelectedItem as ClipboardRow)?.Record;
        PasteButton.IsEnabled = CopyButton.IsEnabled = DeleteButton.IsEnabled = item != null;
        PreviewHost.Content = item != null ? new ClipboardPreview(item, _store) : new TextBlock
        { Text = "选择一条记录，查看完整内容", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6, FontSize = 12 };
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) { _debounce?.Stop(); _debounce?.Start(); }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e) { if (PreviewHost != null) UpdatePreview(); }
    private void PasteClick(object sender, RoutedEventArgs e) { if (HistoryList.SelectedItem is ClipboardRow row) _paste(row.Record); }
    private void CopyClick(object sender, RoutedEventArgs e) { if (HistoryList.SelectedItem is ClipboardRow row) _copy(row.Record); }
    private void DeleteClick(object sender, RoutedEventArgs e) { if (HistoryList.SelectedItem is ClipboardRow row) _delete(row.Record.Id); }
    private void ResetClick(object sender, RoutedEventArgs e) => _reset();
    private void ClearClick(object sender, RoutedEventArgs e) => _clear();
    private void PasteDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(HistoryList, source) is ListBoxItem) PasteClick(sender, e);
    }
    private void SelectRightClickedRow(object sender, MouseButtonEventArgs e) { if (sender is ListBoxItem item) item.IsSelected = true; }
    private void OpenRowMenu(object sender, ContextMenuEventArgs e)
    {
        if (!PopulateRowMenu()) e.Handled = true;
    }
    private bool PopulateRowMenu()
    {
        if (HistoryList.SelectedItem is not ClipboardRow row) return false;
        var menu = HistoryList.ContextMenu; menu.Items.Clear();
        void Add(string text, string key, Action action) { var item = new MenuItem { Header = text, InputGestureText = key }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("粘贴到原窗口", "Enter", () => _paste(row.Record));
        Add("仅复制", "Ctrl+C", () => _copy(row.Record));
        menu.Items.Add(new Separator()); Add("删除记录", "Ctrl+D", () => _delete(row.Record.Id));
        return true;
    }
    private void HandleKeys(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape or Key.Left or Key.Right or Key.Up or Key.Down &&
            (Keyboard.FocusedElement is ComboBox || PreviewHost.Content is ClipboardPreview { IsFilePickerOpen: true })) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Escape) { _close(); e.Handled = true; }
        else if (ctrl && e.Key == Key.F) { FocusSearch(); e.Handled = true; }
        else if (ctrl && e.Key == Key.O && PopulateRowMenu())
        { HistoryList.ContextMenu.PlacementTarget = HistoryList; HistoryList.ContextMenu.IsOpen = true; e.Handled = true; }
        else if (ctrl && e.Key == Key.D) { DeleteClick(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.C && !(Keyboard.FocusedElement is TextBox box && box.SelectionLength > 0)) { CopyClick(this, e); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { PasteClick(this, e); e.Handled = true; }
        else if ((e.Key is Key.Up or Key.Down) && Keyboard.FocusedElement == SearchBox)
        {
            HistoryList.SelectedIndex = Math.Clamp(HistoryList.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, Math.Max(0, HistoryList.Items.Count - 1));
            if (HistoryList.SelectedItem != null) HistoryList.ScrollIntoView(HistoryList.SelectedItem); e.Handled = true;
        }
        else if ((e.Key is Key.Left or Key.Right) && Keyboard.Modifiers == ModifierKeys.None && Keyboard.FocusedElement is not TextBox)
        {
            var index = Array.FindIndex(ClipboardProvider.Categories, x => x.Key == _category);
            var next = (index + (e.Key == Key.Right ? 1 : -1) + _tabs.Count) % _tabs.Count;
            _tabs[ClipboardProvider.Categories[next].Key].IsChecked = true; e.Handled = true;
        }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _debounce.Stop(); _images.Cancel(); _images.Dispose(); }
}

public sealed class ClipboardRow(ClipRecord record) : INotifyPropertyChanged
{
    public ClipRecord Record { get; } = record;
    public string Title => ClipboardProvider.Short(Record.Summary, 180);
    public string Subtitle => $"{ClipboardProvider.Label(Record.Category)} · {Record.CopiedAt.ToLocalTime():MM-dd HH:mm} · {Record.Source}";
    public string Glyph => Record.Category switch { "image" => "\uEB9F", "files" => "\uE8B7", "url" => "\uE71B", "code" or "json" => "\uE943", _ => "\uE8A5" };
    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail { get => _thumbnail; set { _thumbnail = value; PropertyChanged?.Invoke(this, new(nameof(Thumbnail))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
