using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
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
    private readonly Action<IReadOnlyList<ClipRecord>> _paste;
    private readonly Action<ClipRecord> _copy;
    private readonly Action<string> _delete;
    private readonly Action _reset, _clear, _close;
    private readonly DispatcherTimer _debounce, _singleClickTimer;
    private CancellationTokenSource _images = new();
    private readonly Dictionary<string, RadioButton> _tabs = [];
    private string _category = "all";
    private string? _clickRowId;
    private bool _doubleClickCompleted;
    private bool _disposed;
    private static readonly SemaphoreSlim ThumbnailSlots = new(3);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    internal static PluginWindow CreateWindow()
    {
        var window = new PluginWindow("剪贴板历史", 1120, 760, PluginWindowMode.Window)
        { ShowFooter = false, MinWidth = 950, MinHeight = 620, ResizeMode = ResizeMode.CanResize };
        window.ContentHostControl.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        window.ContentHostControl.VerticalContentAlignment = VerticalAlignment.Stretch;
        return window;
    }

    internal ClipboardPanel(HistoryStore store, Action<IReadOnlyList<ClipRecord>> paste, Action<ClipRecord> copy, Action<string> delete,
        Action reset, Action clear, Action close)
    {
        _store = store; _paste = paste; _copy = copy; _delete = delete; _reset = reset; _clear = clear; _close = close;
        InitializeComponent();
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(140), DispatcherPriority.Background,
            (_, _) => ApplyFilter(), Dispatcher); _debounce.Stop();
        _singleClickTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(GetDoubleClickTime()), DispatcherPriority.Background,
            (_, _) => FinishPendingSingleClick(), Dispatcher);
        _singleClickTimer.Stop();
        foreach (var (key, name) in ClipboardProvider.Categories)
        {
            var tab = new RadioButton { Tag = key, Content = name, GroupName = "ClipboardTypes", Style = (Style)Resources["TypeTab"] };
            System.Windows.Automation.AutomationProperties.SetName(tab, name);
            tab.Checked += (_, _) => { _category = key; if (IsLoaded) ApplyFilter(); };
            _tabs.Add(key, tab); TypeTabs.Children.Add(tab);
        }
        _tabs["all"].IsChecked = true;
        HistoryList.ContextMenu = new ContextMenu();
        PreviewKeyDown += HandleKeys;
        IsVisibleChanged += (_, _) => { if (!IsVisible) ResetClickGesture(); };
    }

    private void FocusSearch() => Dispatcher.BeginInvoke(() => { SearchBox.Focus(); SearchBox.SelectAll(); }, DispatcherPriority.Input);

    internal (string Search, string Category) Filter => (SearchBox.Text, _category);

    internal void RestoreFilter((string Search, string Category) filter)
    {
        SearchBox.Text = filter.Search;
        _tabs[filter.Category].IsChecked = true;
    }

    internal void Open()
    {
        ResetClickGesture();
        _debounce.Stop();
        Reload(preserveSelection: false);
        Dispatcher.BeginInvoke(() =>
        {
            if (!_disposed && IsVisible && FindScrollViewer(HistoryList) is { } scrollViewer)
                scrollViewer.ScrollToTop();
        }, DispatcherPriority.Loaded);
        FocusSearch();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(element, i)) is { } childViewer) return childViewer;
        return null;
    }

    internal void Reload() => Reload(preserveSelection: true);

    private void ApplyFilter()
    {
        _debounce.Stop();
        ResetClickGesture();
        Reload(preserveSelection: true, selectFirst: true);
    }

    private void Reload(bool preserveSelection, bool selectFirst = false)
    {
        if (_disposed) return;
        _images.Cancel(); _images.Dispose(); _images = new();
        var token = _images.Token;
        var selected = preserveSelection
            ? HistoryList.SelectedItems.Cast<ClipboardRow>().Select(row => row.Record.Id).ToHashSet()
            : [];
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
        foreach (var row in rows.Where(row => selected.Contains(row.Record.Id))) HistoryList.SelectedItems.Add(row);
        if (selectFirst && rows.Length > 0)
        {
            if (HistoryList.SelectedItems.Count == 0) SelectOnly(rows[0]);
            if (SingleSelection is { } row) HistoryList.ScrollIntoView(row);
        }
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

    private ClipboardRow? SingleSelection => HistoryList.SelectedItems.Count == 1 ? (ClipboardRow)HistoryList.SelectedItems[0]! : null;

    private void UpdatePreview()
    {
        var item = SingleSelection?.Record;
        var count = HistoryList.SelectedItems.Count;
        PasteButton.IsEnabled = CopyButton.IsEnabled = DeleteButton.IsEnabled = item != null;
        PreviewHost.Content = item != null ? new ClipboardPreview(item, _store) : new TextBlock
        { Text = count > 1 ? $"已选 {count} 条，双击其中一条可按列表顺序粘贴" : "选择一条记录，查看完整内容",
          HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6, FontSize = 12 };
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) { _debounce?.Stop(); _debounce?.Start(); }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e) { if (PreviewHost != null) UpdatePreview(); }
    private void PasteClick(object sender, RoutedEventArgs e) { if (SingleSelection is { } row) _paste([row.Record]); }
    private void CopyClick(object sender, RoutedEventArgs e) { if (SingleSelection is { } row) _copy(row.Record); }
    private void DeleteClick(object sender, RoutedEventArgs e) { if (SingleSelection is { } row) _delete(row.Record.Id); }
    private void ResetClick(object sender, RoutedEventArgs e) => _reset();
    private void ClearClick(object sender, RoutedEventArgs e) => _clear();
    private ClipboardRow? RowFromSource(object source) =>
        source is DependencyObject element && ItemsControl.ContainerFromElement(HistoryList, element) is ListBoxItem item
            ? item.Content as ClipboardRow : null;

    private void ClickRow(object sender, MouseButtonEventArgs e)
    {
        if (RowFromSource(e.OriginalSource) is not { } row) return;
        e.Handled = true;
        HistoryList.Focus();
        HandleRowClick(row, e.ClickCount, Keyboard.Modifiers);
    }

    internal void HandleRowClick(ClipboardRow row, int clickCount, ModifierKeys modifiers)
    {
        if (modifiers == ModifierKeys.Shift)
        {
            FinishPendingSingleClick();
            if (HistoryList.SelectedItems.Contains(row))
                HistoryList.SelectedItems.Remove(row);
            else
                HistoryList.SelectedItems.Add(row);
            return;
        }
        if (modifiers != ModifierKeys.None)
        {
            FinishPendingSingleClick();
            return;
        }

        // WPF can count a preceding Shift-click in the same mouse sequence.
        // Pair two unmodified clicks on the same row even if that count starts at 2.
        if (clickCount > 1 && row.Record.Id == _clickRowId)
        {
            if (_doubleClickCompleted) return;
            _doubleClickCompleted = true;
            _singleClickTimer.Stop();
            if (HistoryList.SelectedItems.Contains(row))
                _paste(HistoryList.Items.Cast<ClipboardRow>().Where(HistoryList.SelectedItems.Contains).Select(item => item.Record).ToArray());
            return;
        }

        ResetClickGesture();
        _clickRowId = row.Record.Id;
        if (HistoryList.SelectedItems.Count > 1 && HistoryList.SelectedItems.Contains(row))
        {
            // WPF reports the double-click on mouse-down number two. Leave the
            // selection intact until Windows can distinguish a single click.
            _singleClickTimer.Start();
        }
        else
            SelectOnly(row);
    }

    private void SelectOnly(ClipboardRow row)
    {
        if (SingleSelection == row) return;
        HistoryList.SelectedItems.Clear();
        HistoryList.SelectedItems.Add(row);
    }

    private void ResetClickGesture()
    {
        _singleClickTimer.Stop();
        _clickRowId = null;
        _doubleClickCompleted = false;
    }

    private void FinishPendingSingleClick()
    {
        var pendingId = _singleClickTimer.IsEnabled ? _clickRowId : null;
        ResetClickGesture();
        if (pendingId != null && HistoryList.SelectedItems.Cast<ClipboardRow>().FirstOrDefault(row => row.Record.Id == pendingId) is { } row)
            SelectOnly(row);
    }

    private void SelectRightClickedRow(object sender, MouseButtonEventArgs e)
    {
        if (HistoryList.SelectedItems.Count > 1) { e.Handled = true; return; }
        if (RowFromSource(e.OriginalSource) is { } row)
        {
            e.Handled = true;
            ResetClickGesture();
            SelectOnly(row);
        }
    }
    private void OpenRowMenu(object sender, ContextMenuEventArgs e)
    {
        if (!PopulateRowMenu()) e.Handled = true;
    }
    private bool PopulateRowMenu()
    {
        var menu = HistoryList.ContextMenu; menu.Items.Clear();
        if (SingleSelection is not { } row) return false;
        void Add(string text, string key, Action<ClipRecord> action)
        {
            var item = new MenuItem { Header = text, InputGestureText = key };
            item.Click += (_, _) =>
            {
                if (SingleSelection?.Record is { } current && current.Id == row.Record.Id) action(current);
            };
            menu.Items.Add(item);
        }
        Add("粘贴到原窗口", "Enter", record => _paste([record]));
        Add("仅复制", "Ctrl+C", _copy);
        menu.Items.Add(new Separator());
        Add("删除记录", "Ctrl+D", record => _delete(record.Id));
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
        else if (HistoryList.IsKeyboardFocusWithin && ctrl && e.Key == Key.A) e.Handled = true;
        else if (HistoryList.IsKeyboardFocusWithin && e.Key is Key.Space or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            // Prevent ListBox's built-in multiple selection gestures.
            if (Keyboard.Modifiers == ModifierKeys.None && HistoryList.Items.Count > 0)
            {
                var index = e.Key switch
                {
                    Key.Home => 0,
                    Key.End => HistoryList.Items.Count - 1,
                    Key.PageUp => HistoryList.SelectedIndex - ScrollPageSize(),
                    Key.PageDown => HistoryList.SelectedIndex + ScrollPageSize(),
                    _ => HistoryList.SelectedIndex
                };
                SelectListIndex(index);
            }
            e.Handled = true;
        }
        else if ((e.Key is Key.Up or Key.Down) && (Keyboard.FocusedElement == SearchBox || HistoryList.IsKeyboardFocusWithin))
        {
            if (Keyboard.Modifiers == ModifierKeys.None) SelectListIndex(HistoryList.SelectedIndex + (e.Key == Key.Down ? 1 : -1));
            e.Handled = true;
        }
        else if ((e.Key is Key.Left or Key.Right) && Keyboard.Modifiers == ModifierKeys.None && Keyboard.FocusedElement is not TextBox)
        {
            var index = Array.FindIndex(ClipboardProvider.Categories, x => x.Key == _category);
            var next = (index + (e.Key == Key.Right ? 1 : -1) + _tabs.Count) % _tabs.Count;
            _tabs[ClipboardProvider.Categories[next].Key].IsChecked = true; e.Handled = true;
        }
    }

    private int ScrollPageSize() => Math.Max(1, (int)(FindScrollViewer(HistoryList)?.ViewportHeight ?? 1));

    private void SelectListIndex(int index)
    {
        if (HistoryList.Items.Count == 0) return;
        ResetClickGesture();
        var row = (ClipboardRow)HistoryList.Items[Math.Clamp(index, 0, HistoryList.Items.Count - 1)];
        SelectOnly(row);
        HistoryList.ScrollIntoView(row);
    }

    public void Dispose() { if (_disposed) return; _disposed = true; ResetClickGesture(); _debounce.Stop(); _images.Cancel(); _images.Dispose(); }
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
