using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lertaro.Plugins.Clipboard;

// Cache only small, frozen managed images. Each SDK result gets a fresh native
// bitmap; the host owns and deletes that handle after materializing the row.
internal sealed class ClipboardThumbnails
{
    internal const int Edge = 80;
    private const int Capacity = 128;
    private readonly object _gate = new();
    private readonly Dictionary<string, Task<BitmapSource?>> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(string Path, Task<BitmapSource?> Task)> _order = new();
    private static readonly SemaphoreSlim LoadSlots = new(3);

    internal Task<BitmapSource?> GetAsync(string path, Action? ready = null)
    {
        lock (_gate)
        {
            if (_images.TryGetValue(path, out var cached))
            {
                if (!cached.IsCompleted || (cached.IsCompletedSuccessfully && cached.Result != null)) return cached;
                _images.Remove(path);
            }
            while (_order.Count >= Capacity && _order.TryDequeue(out var oldest))
                if (_images.TryGetValue(oldest.Path, out var current) && ReferenceEquals(current, oldest.Task)) _images.Remove(oldest.Path);
            var task = Task.Run(async () =>
            {
                await LoadSlots.WaitAsync();
                try { return Load(path); }
                finally { LoadSlots.Release(); }
            });
            _images[path] = task; _order.Enqueue((path, task));
            _ = task.ContinueWith(completed =>
            {
                _ = completed.Exception;
                if (completed.IsCompletedSuccessfully && completed.Result != null) { try { ready?.Invoke(); } catch { } }
                else lock (_gate)
                {
                    if (_images.TryGetValue(path, out var current) && ReferenceEquals(current, task)) _images.Remove(path);
                }
            }, TaskScheduler.Default);
            return task;
        }
    }

    internal IntPtr GetHBitmap(string path, Action ready, bool dark)
    {
        var task = GetAsync(path, ready);
        return task.IsCompletedSuccessfully && task.Result is { } image ? CreateHBitmap(image, dark) : IntPtr.Zero;
    }

    private static BitmapSource? Load(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            var width = frame.PixelWidth; var height = frame.PixelHeight;
            if (width <= 0 || height <= 0 || (long)width * height > 32L * 1024 * 1024) return null;
            stream.Position = 0;
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            if (width >= height) image.DecodePixelWidth = Math.Min(width, Edge);
            else image.DecodePixelHeight = Math.Min(height, Edge);
            image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException or FileFormatException or System.Runtime.InteropServices.COMException)
        { return null; }
    }

    internal static IntPtr CreateHBitmap(BitmapSource source, bool dark)
    {
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0 || source.PixelWidth > Edge || source.PixelHeight > Edge) return IntPtr.Zero;
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0);
        // The SDK's HBITMAP materializer may discard alpha. Use an opaque theme
        // matte so transparent source pixels never turn into a black rectangle.
        var matte = dark ? 40 : 245;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            for (var channel = 0; channel < 3; channel++) pixels[i + channel] = (byte)((pixels[i + channel] * alpha + matte * (255 - alpha) + 127) / 255);
            pixels[i + 3] = 255;
        }
        var info = new BitmapInfo { Size = 40, Width = bitmap.PixelWidth, Height = -bitmap.PixelHeight, Planes = 1, Bits = 32 };
        var handle = CreateDIBSection(IntPtr.Zero, ref info, 0, out var data, IntPtr.Zero, 0);
        if (handle == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            if (data == IntPtr.Zero) { DeleteObject(handle); return IntPtr.Zero; }
            Marshal.Copy(pixels, 0, data, pixels.Length); return handle;
        }
        catch { DeleteObject(handle); throw; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, Bits;
        public uint Compression, ImageSize; public int XResolution, YResolution; public uint ColorsUsed, ColorsImportant;
    }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr handle);
}
