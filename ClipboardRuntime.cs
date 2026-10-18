using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lertaro.PluginSdk.Services;
using Lertaro.PluginSdk.Windows;
using WinClipboard = System.Windows.Clipboard;

namespace Lertaro.Plugins.Clipboard;

internal sealed class ClipboardRuntime
{
    internal const string PluginId = "Lertaro.Plugins.Clipboard";
    internal static readonly Lazy<ClipboardRuntime> Shared = new(() => new ClipboardRuntime());
    private readonly object _gate = new();
    private readonly object _workerGate = new();
    private readonly PasteSequence _sequence = new();
    private Task _work = Task.CompletedTask;
    private HistoryStore? _store;
    private Dispatcher? _dispatcher;
    private HwndSource? _window;
    private PluginWindow? _panelWindow;
    private ClipboardPanel? _panel;
    private IntPtr _foregroundHook;
    private Native.WinEventProc? _foregroundCallback;
    private IntPtr _target;
    private uint _seenSequence, _selfSequence;
    private DispatcherTimer? _retryTimer, _settingsTimer;
    private int _readAttempts;
    private bool _busy;
    private volatile bool _stopping;
    private Exception? _persistenceError;
    private string _hotkeySignature = "";
    private string _startupError = "正在加载历史…";
    internal volatile bool Ready;
    internal HistoryStore? Store => _store;
    internal string Status => _startupError;
    internal string Keyword => Setting("Keyword", "clip").Trim() is { Length: > 0 } k ? k : "clip";
    internal T Setting<T>(string key, T fallback) => PluginSettingsService.GetSetting(PluginId, key, fallback);
    internal ClipLimits Limits => new(Math.Clamp(Setting("MaxItems", 1000), 1, 10000),
        Math.Clamp(Setting("Days", 90), 1, 3650), Math.Clamp(Setting("TotalMB", 256), 16, 2048) * 1024L * 1024,
        Math.Clamp(Setting("ImageMB", 128), 1, 2048) * 1024L * 1024,
        Math.Clamp(Setting("ItemMB", 10), 1, 64) * 1024L * 1024);

    private ClipboardRuntime()
    {
        var app = Application.Current;
        if (app == null) { _startupError = "宿主用户界面不可用。"; return; }
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                var data = UserDataService.GetUserDataDirectory();
                if (string.IsNullOrEmpty(data)) throw new InvalidOperationException("宿主未提供用户数据目录。");
                var loaded = await Task.Run(() => new HistoryStore(Path.Combine(data, "ClipboardHistory")));
                if (_stopping) return;
                _store = loaded;
                _startupError = "";
                var thread = new Thread(MonitorMain) { IsBackground = true, Name = "Lertaro Clipboard STA" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                app.Exit += (_, _) =>
                {
                    _stopping = true;
                    _dispatcher?.BeginInvoke(ShutdownMonitor);
                    // The worker never waits for the UI: drain accepted records before normal process exit.
                    Task pending; lock (_workerGate) pending = _work;
                    pending.GetAwaiter().GetResult();
                };
            }
            catch (Exception ex) { _startupError = ex.Message; Notify("剪贴板启动失败", ex.Message); }
        }, DispatcherPriority.ApplicationIdle);
    }

    private void MonitorMain()
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _window = new HwndSource(new HwndSourceParameters("LertaroClipboardListener")
            { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
            _window.AddHook(WindowProc);
            if (!Native.AddClipboardFormatListener(_window.Handle)) throw new InvalidOperationException("注册剪贴板监听失败。");
            _seenSequence = Native.GetClipboardSequenceNumber();
            _foregroundCallback = (_, _, hwnd, _, _, _, _) => { if (Native.ExternalWindow(hwnd)) _target = hwnd; };
            _foregroundHook = Native.SetWinEventHook(3, 3, IntPtr.Zero, _foregroundCallback, 0, 0, 0);
            if (_foregroundHook == IntPtr.Zero) throw new InvalidOperationException("注册前台窗口跟踪失败。");
            var current = Native.GetForegroundWindow();
            if (Native.ExternalWindow(current)) _target = current;
            _retryTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(35), DispatcherPriority.Background,
                (_, _) => Capture(), _dispatcher); _retryTimer.Stop();
            _settingsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
                (_, _) => RefreshSettings(), _dispatcher);
            RefreshSettings();
            Ready = true;
            QueueWork(() => { _store!.Prune(Limits); Persist(); });
            Logger.Log("[Clipboard] Native listener and hotkeys started.");
            Dispatcher.Run();
        }
        catch (Exception ex) { _startupError = ex.Message; Notify("剪贴板监听失败", ex.Message); }
        finally { ShutdownMonitor(); }
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x031D && !_stopping) { _readAttempts = 0; Capture(); }
        if (msg == 0x0312 && !_stopping)
        {
            handled = true;
            if (wParam.ToInt32() == 1) ShowHistory();
            if (wParam.ToInt32() == 2) _ = PasteNextAsync();
        }
        return IntPtr.Zero;
    }

    private bool Enabled() => PluginSettingsService.IsComponentEnabled(PluginId + ".dll", "InstantProvider", nameof(ClipboardProvider));
    private void RefreshSettings()
    {
        var signature = Enabled() + "|" + Setting("OpenHotkey", "Ctrl+Shift+D") + "|" + Setting("NextHotkey", "Ctrl+Shift+V");
        if (signature == _hotkeySignature) return;
        _hotkeySignature = signature;
        if (_window == null) return;
        Native.UnregisterHotKey(_window.Handle, 1); Native.UnregisterHotKey(_window.Handle, 2);
        if (!Enabled()) return;
        Register(1, Setting("OpenHotkey", "Ctrl+Shift+D")); Register(2, Setting("NextHotkey", "Ctrl+Shift+V"));
    }
    private void Register(int id, string value)
    {
        try
        {
            var (mods, key) = Native.ParseHotkey(value);
            if (key != 0 && !Native.RegisterHotKey(_window!.Handle, id, mods, key))
                throw new InvalidOperationException($"{value} 已被占用或无法注册，请在插件设置中修改。");
        }
        catch (Exception ex) { Notify("剪贴板快捷键", ex.Message); }
    }

    private void Capture()
    {
        _retryTimer?.Stop();
        var seq = Native.GetClipboardSequenceNumber();
        if (_stopping || !Enabled() || seq == _seenSequence || seq == _selfSequence) { _seenSequence = seq; return; }
        try
        {
            var owner = Native.GetClipboardOwner();
            var source = Native.SourceName(owner != IntPtr.Zero ? owner : Native.GetForegroundWindow());
            var record = ReadClipboard(source, out var png);
            if (Native.GetClipboardSequenceNumber() != seq) { RetryCapture(); return; }
            _seenSequence = seq;
            if (record == null) return;
            var limits = Limits;
            if (record.Bytes > limits.MaxItemBytes) { Notify("内容未记录", $"单条超过 {limits.MaxItemBytes / 1024 / 1024} MB 上限。"); return; }
            QueueWork(() =>
            {
                if (png != null) _store!.SaveImage(record, png);
                var stored = _store!.Add(record, limits);
                lock (_gate) _sequence.Add(stored, limits);
                Persist();
                RefreshSearch();
            });
        }
        catch (Exception) { RetryCapture(); }
    }
    private void RetryCapture() { if (++_readAttempts <= 8) _retryTimer?.Start(); }

    private ClipRecord? ReadClipboard(string source, out byte[]? png)
    {
        png = null;
        // FileDrop wins over its accompanying path text; useful text wins over Office's rendered image.
        if (WinClipboard.ContainsFileDropList())
        {
            var files = WinClipboard.GetFileDropList().Cast<string>().ToArray();
            if (files.Length == 0) return null;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(files);
            return new ClipRecord { Kind = ClipKind.Files, Category = "files", Files = files, Bytes = bytes.Length,
                Hash = ClipRecord.Fingerprint(ClipKind.Files, bytes), Source = source };
        }
        if (WinClipboard.ContainsText())
        {
            var text = WinClipboard.GetText();
            if (text.Length > 0) return ClipRecord.FromText(text, source);
        }
        if (!WinClipboard.ContainsImage()) return null;
        BitmapSource? image = null;
        // Prefer PNG where present to preserve alpha instead of the legacy DIB conversion.
        if (WinClipboard.GetData("PNG") is MemoryStream memory)
        {
            var data = memory.ToArray();
            using var input = new MemoryStream(data);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            image = decoder.Frames[0];
        }
        image ??= WinClipboard.GetImage();
        if (image == null || (long)image.PixelWidth * image.PixelHeight > 32L * 1024 * 1024) return null;
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream(); encoder.Save(output); png = output.ToArray();
        var hash = ClipRecord.Fingerprint(ClipKind.Image, png);
        return new ClipRecord { Kind = ClipKind.Image, Category = "image", Hash = hash, ImageFile = hash.Split(':')[1] + ".png",
            Source = source, Bytes = png.Length, Width = image.PixelWidth, Height = image.PixelHeight };
    }

    internal void ShowHistory()
    {
        var current = Native.GetForegroundWindow();
        if (Native.ExternalWindow(current)) _target = current;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (_store == null) { Notify("剪贴板尚未就绪", Status); return; }
                SearchWindowService.HideWindow();
                if (_panelWindow == null)
                {
                    _panelWindow = ClipboardPanel.CreateWindow();
                    _panel = new ClipboardPanel(_store, item => Paste(item), Copy, Delete, ResetSequence, ClipboardMenu.ConfirmClear, _panelWindow.Hide);
                    _panelWindow.ContentHostControl.Content = _panel;
                    _panelWindow.ContentHostControl.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    _panelWindow.ContentHostControl.VerticalContentAlignment = VerticalAlignment.Stretch;
                    _panelWindow.Closed += (_, _) => { _panel?.Dispose(); _panel = null; _panelWindow = null; };
                }
                _panelWindow.Show(); _panelWindow.Activate(); _panel?.Reload(); _panel?.FocusSearch();
            }
            catch (Exception ex) { Notify("剪贴板面板未能打开", ex.Message); SearchWindowService.ShowWindow(Keyword + " "); }
        });
    }
    internal void Copy(ClipRecord item) => _dispatcher?.BeginInvoke(async () =>
    {
        try { await WriteAsync(item); Notify("已复制", "历史内容已恢复到剪贴板。"); }
        catch (Exception ex) { Notify("复制失败", ex.Message); }
    });
    private IntPtr Destination()
    {
        var current = Native.GetForegroundWindow();
        return Native.ExternalWindow(current) ? current : _target;
    }
    internal void Paste(ClipRecord item, IntPtr? destination = null)
    {
        var target = destination ?? Destination();
        Native.GetWindowThreadProcessId(target, out var expectedPid);
        _dispatcher?.BeginInvoke(async () =>
        {
        if (_busy) return;
        _busy = true;
        try { await PasteAsync(item, target, expectedPid); }
        catch (Exception ex) { Notify("粘贴失败", ex.Message); }
        finally { _busy = false; }
        });
    }
    private async Task PasteNextAsync()
    {
        if (_busy) return;
        _busy = true;
        var target = Destination();
        Native.GetWindowThreadProcessId(target, out var expectedPid);
        try
        {
            // Finish queued capture work before freezing the copy-order batch.
            Task pending; lock (_workerGate) pending = _work;
            await pending;
            ClipRecord? next; lock (_gate) next = _sequence.Peek();
            if (next == null) { Notify("已粘贴完", "请先重置顺序粘贴，再复制下一批内容。"); return; }
            await PasteAsync(next, target, expectedPid);
            (int Position, int Total) progress;
            lock (_gate) { _sequence.Advance(); progress = _sequence.Progress; }
            if (progress.Position == progress.Total) Notify("已粘贴完", $"共 {progress.Total} 条；不会自动循环。");
        }
        catch (Exception ex) { Notify("顺序粘贴失败", ex.Message + " 当前条目未跳过。"); }
        finally { _busy = false; }
    }
    internal void ResetSequence() => _dispatcher?.BeginInvoke(() =>
    {
        if (_busy) return;
        QueueWork(() => { lock (_gate) _sequence.Reset(); Persist(); });
        Notify("顺序粘贴已重置", "现在开始复制下一批内容。");
    });

    private async Task PasteAsync(ClipRecord item, IntPtr target, uint expectedPid)
    {
        var current = Native.GetForegroundWindow();
        Logger.Log($"[Clipboard] Paste start target={target}, current={current}", LogLevel.Debug);
        if (!Native.ExternalWindow(target)) throw new InvalidOperationException("目标窗口已关闭，请重新选择目标应用。");
        if (Native.ExternalWindow(current) && current != target)
            throw new InvalidOperationException("前台应用已改变，已停止粘贴。");
        var targetThread = Native.GetWindowThreadProcessId(target, out var targetPid);
        if (targetPid != expectedPid) throw new InvalidOperationException("目标窗口已被其他进程替换。");
        await Application.Current.Dispatcher.InvokeAsync(SearchWindowService.HideWindow);
        await Application.Current.Dispatcher.InvokeAsync(() => _panelWindow?.Hide());
        Logger.Log("[Clipboard] Host hidden", LogLevel.Debug);
        if (Native.IsIconic(target)) Native.ShowWindowAsync(target, 9);
        Native.SetForegroundWindow(target);
        for (var i = 0; Native.ModifiersDown() && i < 100; i++) await Task.Delay(10);
        if (_stopping || Native.ModifiersDown()) throw new InvalidOperationException("请松开快捷键后重试。");
        if (Native.GetForegroundWindow() != target) throw new InvalidOperationException("未能恢复目标焦点，已停止粘贴。");
        if (Native.GetWindowThreadProcessId(target, out var checkPid) != targetThread || checkPid != targetPid)
            throw new InvalidOperationException("目标窗口已变化。");
        await WriteAsync(item);
        Logger.Log("[Clipboard] Clipboard restored", LogLevel.Debug);
        if (Native.GetForegroundWindow() != target) throw new InvalidOperationException("焦点发生变化，内容已复制但未发送粘贴。");
        Native.PasteKeys();
        Logger.Log("[Clipboard] Paste input submitted", LogLevel.Debug);
    }
    private async Task WriteAsync(ClipRecord item)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (_stopping) throw new InvalidOperationException("插件正在停止。");
                switch (item.Kind)
                {
                    case ClipKind.Text:
                        WinClipboard.SetText(item.Text);
                        _selfSequence = Native.GetClipboardSequenceNumber();
                        if (WinClipboard.GetText() != item.Text) throw new IOException("文本写入未能确认。");
                        break;
                    case ClipKind.Files:
                        if (item.Files.Any(x => !File.Exists(x) && !Directory.Exists(x))) throw new FileNotFoundException("部分文件已移动或删除。");
                        var list = new System.Collections.Specialized.StringCollection(); list.AddRange(item.Files);
                        WinClipboard.SetFileDropList(list); _selfSequence = Native.GetClipboardSequenceNumber();
                        if (!WinClipboard.GetFileDropList().Cast<string>().SequenceEqual(item.Files)) throw new IOException("文件列表写入未能确认。");
                        break;
                    case ClipKind.Image:
                        var png = await File.ReadAllBytesAsync(_store!.ImagePath(item));
                        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        using (var input = new MemoryStream(png)) { bitmap.StreamSource = input; bitmap.EndInit(); } bitmap.Freeze();
                        var data = new DataObject(); data.SetImage(bitmap); data.SetData("PNG", new MemoryStream(png));
                        WinClipboard.SetDataObject(data, true); _selfSequence = Native.GetClipboardSequenceNumber();
                        if (!WinClipboard.ContainsImage()) throw new IOException("图片写入未能确认。");
                        break;
                }
                _seenSequence = _selfSequence; return;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 5) { await Task.Delay(35); }
        }
    }

    internal void Delete(string id) => QueueWork(() =>
    { _store!.Delete(id); lock (_gate) _sequence.Remove(id); Persist(); RefreshSearch(); });
    internal void Clear() => QueueWork(() =>
    { _store!.Clear(); lock (_gate) _sequence.Reset(); Persist(); RefreshSearch(); });
    internal void ApplySettings() => QueueWork(() => { _store!.Prune(Limits); Persist(); RefreshSearch(); });
    private void Persist()
    {
        ClipRecord[] retained; lock (_gate) retained = _sequence.Retained.ToArray();
        _store!.Save(retained);
        _persistenceError = null;
    }
    private void QueueWork(Action action)
    {
        if (_stopping || _store == null) return;
        lock (_workerGate) _work = _work.ContinueWith(_ =>
        {
            try { action(); }
            catch (Exception ex)
            {
                _persistenceError = ex;
                Logger.Log("[Clipboard] " + ex.Message, LogLevel.Error); Notify("剪贴板保存失败", ex.Message);
            }
        }, TaskScheduler.Default);
    }
    private void RefreshSearch() => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        SearchRefreshService.RefreshIfMatches(q => TriggerWord.TryMatch(q, Keyword, out _));
        if (_panelWindow?.IsVisible == true) _panel?.Reload();
    });
    internal static void Notify(string title, string text)
    {
        var ui = Application.Current?.Dispatcher;
        if (ui == null || ui.HasShutdownStarted) return;
        if (ui.CheckAccess()) PluginNotificationService.Show(title, text);
        else ui.BeginInvoke(() => PluginNotificationService.Show(title, text));
    }
    internal async Task StopAsync()
    {
        _stopping = true;
        if (_dispatcher != null && !_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
            await _dispatcher.InvokeAsync(ShutdownMonitor);
        Task task; lock (_workerGate) task = _work; await task;
        if (_persistenceError != null) throw new IOException("剪贴板历史存在未解决的保存错误，已取消设置迁移。", _persistenceError);
    }
    private void ShutdownMonitor()
    {
        Ready = false;
        _retryTimer?.Stop(); _settingsTimer?.Stop();
        if (_foregroundHook != IntPtr.Zero) { Native.UnhookWinEvent(_foregroundHook); _foregroundHook = IntPtr.Zero; }
        if (_window != null)
        {
            Native.UnregisterHotKey(_window.Handle, 1); Native.UnregisterHotKey(_window.Handle, 2);
            Native.RemoveClipboardFormatListener(_window.Handle); _window.Dispose(); _window = null;
        }
        if (_dispatcher != null && !_dispatcher.HasShutdownStarted) _dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
    }
}
