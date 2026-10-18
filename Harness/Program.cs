using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lertaro.PluginSdk;
using Lertaro.PluginSdk.Abstractions;
using Lertaro.PluginSdk.Services;
using Lertaro.Plugins.Clipboard;
using WinClipboard = System.Windows.Clipboard;

internal static class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [STAThread]
    private static void Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var root = Path.GetFullPath(args.Last()); Directory.CreateDirectory(root);
        if (args.Contains("--live-probe")) { LiveProbe.Run(app, root); return; }
        if (args.Contains("--render") || args.Contains("--demo"))
        {
            var hostIndex = Array.IndexOf(args, "--host");
            var hostDirectory = hostIndex >= 0 && hostIndex + 1 < args.Length ? args[hostIndex + 1] : Environment.GetEnvironmentVariable("LERTARO_DIR");
            if (string.IsNullOrWhiteSpace(hostDirectory)) throw new ArgumentException("Specify --host with the full Lertaro installation directory.");
            RenderUi.Run(app, root, hostDirectory, args.Contains("--demo")); return;
        }
        if (args.Contains("--target"))
        {
            var text = new TextBox { AcceptsReturn = true, FontSize = 22, Margin = new Thickness(16) };
            var window = new Window { Title = "Clipboard isolated paste target", Width = 620, Height = 260, Content = text };
            text.TextChanged += (_, _) => File.WriteAllText(Path.Combine(root, "target.txt"), text.Text);
            text.PreviewKeyDown += (_, e) => File.AppendAllText(Path.Combine(root, "target-keys.txt"), e.Key + "\n");
            window.Loaded += (_, _) => text.Focus();
            window.Closed += (_, _) => app.Shutdown(); window.Show(); app.Run(); return;
        }
        var report = new List<object>();
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
        Logger.LogAction = (message, level) => File.AppendAllText(Path.Combine(root, "harness.log"), $"{level}: {message}\n");
        UserDataService.GetUserDataDirectoryFunc = () => root;
        var settings = new Dictionary<string, object?> { ["Keyword"] = "testclip", ["OpenHotkey"] = "Ctrl+Alt+F9", ["NextHotkey"] = "Ctrl+Alt+F10" };
        PluginSettingsService.GetSettingFunc = (_, key, fallback) => settings.GetValueOrDefault(key, fallback);
        PluginSettingsService.IsComponentEnabledFunc = (_, _, _) => true;
        SearchRefreshService.RefreshMatchingFunc = _ => { };
        var host = new Window { Title = "Clipboard isolated SDK host", Width = 640, Height = 300 };
        SearchWindowService.ShowWindowFunc = query => { host.Content = new TextBlock { Text = query, FontSize = 24 }; host.Show(); host.Activate(); };
        SearchWindowService.HideWindowFunc = host.Hide;
        PluginNotificationService.ShowRequestFunc = (request, _) =>
        { File.AppendAllText(Path.Combine(root, "notifications.log"), request.Title + ": " + request.Message + "\n"); return null; };
        var plugin = new ClipboardPlugin(); var provider = new ClipboardProvider();
        Process? targetProcess = null;
        app.Startup += async (_, _) =>
        {
            try
            {
                await Wait(() => ClipboardRuntime.Shared.Value.Ready, "runtime initialization");
                var runtime = ClipboardRuntime.Shared.Value;
                runtime.ResetSequence(); await Task.Delay(150);
                foreach (var value in new[] { "  A😀\r\n", "B中文", "  A😀\r\n" })
                {
                    WinClipboard.SetText(value);
                    await Wait(() => runtime.Store!.Snapshot().Any(x => x.Text == value), "individual clipboard event");
                    await Task.Delay(80);
                }
                await Wait(() => runtime.Store!.Snapshot().Length == 2, "clipboard notifications and deduplication");
                Check(runtime.Store!.Snapshot().Any(x => x.Text == "  A😀\r\n"), "whitespace Unicode capture", report);
                var everyResult = provider.GetInstantResults("testclip 😀").ToArray();
                File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(everyResult.Select(x => new { x.Title, x.ActionArgument })));
                var results = everyResult.Where(x => PluginPreviewCache.GetEntry(x.ActionArgument) != null).ToArray();
                Check(results.Length == 1, "native result and preview registration", report);
                var entry = PluginPreviewCache.GetEntry(results[0].ActionArgument);
                Check(entry?.GetElement() is UserControl, "WPF preview materialization", report);

                var rgba = new byte[] { 0, 0, 255, 128, 0, 255, 0, 255, 255, 0, 0, 255, 0, 0, 0, 0 };
                var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, rgba, 8);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var bytes = new MemoryStream(); png.Save(bytes);
                var data = new DataObject(); data.SetImage(bitmap); data.SetData("PNG", new MemoryStream(bytes.ToArray()));
                WinClipboard.SetDataObject(data, true);
                await Wait(() => runtime.Store!.Snapshot().Any(x => x.Kind == ClipKind.Image), "image capture");
                var image = runtime.Store!.Snapshot().First(x => x.Kind == ClipKind.Image);
                Check(File.Exists(runtime.Store.ImagePath(image)), "PNG persistence", report);
                var testFile = Path.Combine(root, "sample.txt"); File.WriteAllText(testFile, "test");
                var paths = new System.Collections.Specialized.StringCollection(); paths.Add(testFile); paths.Add(root);
                WinClipboard.SetFileDropList(paths);
                await Wait(() => runtime.Store!.Snapshot().Any(x => x.Kind == ClipKind.Files), "file capture");
                var files = runtime.Store!.Snapshot().First(x => x.Kind == ClipKind.Files);
                runtime.Copy(image); await Task.Delay(350);
                Check(WinClipboard.ContainsImage(), "restore image", report);
                runtime.Copy(files); await Task.Delay(350);
                Check(WinClipboard.GetFileDropList().Cast<string>().SequenceEqual(files.Files), "restore file list", report);
                runtime.Copy(ClipRecord.FromText("self-write")); await Task.Delay(350);
                Check(!runtime.Store.Snapshot().Any(x => x.Text == "self-write"), "self write suppressed", report);

                var target = targetProcess = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--target \"" + root + "\"") { UseShellExecute = false })!;
                await Wait(() => { target.Refresh(); return target.MainWindowHandle != IntPtr.Zero; }, "target window");
                File.WriteAllText(Path.Combine(root, "ready.txt"), target.MainWindowHandle.ToString());
                Native.SetForegroundWindow(target.MainWindowHandle);
                await Wait(() => Native.GetForegroundWindow() == target.MainWindowHandle, "activate isolated target", 1200);
                runtime.Paste(ClipRecord.FromText("paste-ok"), target.MainWindowHandle);
                await Wait(() => File.Exists(Path.Combine(root, "target.txt")) && File.ReadAllText(Path.Combine(root, "target.txt")) == "paste-ok", "cross-process paste");
                Check(true, "cross-process text paste", report);
                runtime.ResetSequence(); await Task.Delay(200);
                foreach (var value in new[] { "A", "B", "A" }) { WinClipboard.SetText(value); await Task.Delay(180); }
                for (var n = 0; n < 3; n++)
                {
                    Native.SetForegroundWindow(target.MainWindowHandle);
                    await Wait(() => Native.GetForegroundWindow() == target.MainWindowHandle, "sequence target focus", 1200);
                    var listener = FindWindowEx(new IntPtr(-3), IntPtr.Zero, null, "LertaroClipboardListener");
                    if (listener == IntPtr.Zero) throw new InvalidOperationException("Listener window not found.");
                    PostMessage(listener, 0x0312, new IntPtr(2), IntPtr.Zero);
                    await Task.Delay(300);
                }
                Check(File.ReadAllText(Path.Combine(root, "target.txt")) == "paste-okABA", "copy-order A B A", report);
                target.CloseMainWindow();
                await plugin.PrepareForSettingsTransferAsync();
                var reloaded = new HistoryStore(Path.Combine(root, "ClipboardHistory"));
                Check(reloaded.Snapshot().Length >= 4, "restart persistence", report);
            }
            catch (Exception ex) { report.Add(new { name = "failure", passed = false, detail = ex.ToString() }); Environment.ExitCode = 1; }
            finally
            {
                await plugin.PrepareForSettingsTransferAsync();
                try { WinClipboard.SetDataObject(original, true); } catch { }
                if (targetProcess is { HasExited: false }) targetProcess.CloseMainWindow();
                targetProcess?.Dispose();
                File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })); app.Shutdown();
            }
        };
        app.Run();
    }
    private static void Check(bool condition, string name, List<object> report)
    { report.Add(new { name, passed = condition }); if (!condition) throw new InvalidOperationException(name); }
    private static async Task Wait(Func<bool> condition, string name, int attempts = 120)
    {
        for (var i = 0; i < attempts; i++) { if (condition()) return; await Task.Delay(50); }
        throw new TimeoutException(name);
    }
}
