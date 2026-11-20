using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Lertaro.PluginSdk;

namespace Lertaro.Plugins.Clipboard;

// An intermediate clipboard item is offered with delayed rendering so a paste
// cannot be followed by another write merely because an arbitrary time elapsed.
internal sealed class PanelPasteClipboard(IntPtr owner)
{
    internal sealed class Entry(uint format, byte[] data, uint targetProcess)
    {
        internal uint Format { get; } = format;
        internal byte[] Data { get; } = data;
        internal uint TargetProcess { get; } = targetProcess;
        internal bool InputSent { get; set; }
        internal bool Requested { get; set; }
        internal bool WaitForTarget { get; set; }
        internal Win32Exception? RenderError { get; set; }
    }

    private Entry? _pending;

    internal static Entry Prepare(ClipRecord segment, uint targetProcess) =>
        segment.Kind == ClipKind.Text
            ? new Entry(13, Encoding.Unicode.GetBytes(segment.Text + '\0'), targetProcess)
            : new Entry(15, FileDropPayload(segment.Files), targetProcess);

    internal bool TryWrite(Entry entry, uint? expectedSequence, bool delayed, Action verifyTarget)
    {
        if (!OpenClipboard(owner)) return false;
        try
        {
            if (expectedSequence.HasValue ? Native.GetClipboardSequenceNumber() != expectedSequence.Value : Native.GetClipboardOwner() != owner)
                throw new InvalidOperationException("剪贴板已变化，停止粘贴剩余内容。");
            verifyTarget();
            if (!EmptyClipboard()) throw Error("无法清空剪贴板。");
            if (delayed)
            {
                SetClipboardData(entry.Format, IntPtr.Zero);
                if (Marshal.GetLastPInvokeError() != 0) throw Error("无法登记待粘贴内容。");
                entry.WaitForTarget = true;
                _pending = entry;
            }
            else SetData(entry);
        }
        finally { CloseClipboard(); }
        return true;
    }

    internal bool HandleMessage(int message, IntPtr format)
    {
        if (_pending is not { } entry) return false;
        switch (message)
        {
            case 0x0305 when format.ToInt64() == entry.Format: // WM_RENDERFORMAT
                var requester = GetOpenClipboardWindow();
                Native.GetWindowThreadProcessId(requester, out var requesterProcess);
                if (requester == IntPtr.Zero)
                    Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out requesterProcess);
                var targetRequest = entry.InputSent && requesterProcess == entry.TargetProcess;
                if (!entry.WaitForTarget || targetRequest)
                {
                    Render(entry);
                    entry.Requested = entry.RenderError == null && targetRequest;
                }
                Logger.Log($"[Clipboard] Panel format {entry.Format} requested; target request={targetRequest}.", LogLevel.Debug);
                return true;
            case 0x0306: // WM_RENDERALLFORMATS
                if (OpenClipboard(owner))
                {
                    try { if (Native.GetClipboardOwner() == owner) Render(entry); }
                    finally { CloseClipboard(); }
                }
                return true;
            case 0x0307: // WM_DESTROYCLIPBOARD
                _pending = null;
                return true;
            default:
                return false;
        }
    }

    internal void EndWait()
    {
        if (_pending != null) _pending.WaitForTarget = false;
    }

    private void Render(Entry entry)
    {
        try { SetData(entry); _pending = null; }
        catch (Win32Exception ex) { entry.RenderError = ex; }
    }

    private static void SetData(Entry entry)
    {
        var memory = GlobalAlloc(2, (nuint)entry.Data.Length); // GMEM_MOVEABLE
        if (memory == IntPtr.Zero) throw Error("无法分配剪贴板内存。");
        try
        {
            var pointer = GlobalLock(memory);
            if (pointer == IntPtr.Zero) throw Error("无法访问剪贴板内存。");
            try { Marshal.Copy(entry.Data, 0, pointer, entry.Data.Length); }
            finally { GlobalUnlock(memory); }
            if (SetClipboardData(entry.Format, memory) == IntPtr.Zero) throw Error("无法写入剪贴板。");
            memory = IntPtr.Zero; // Windows owns the handle after SetClipboardData succeeds.
        }
        finally { if (memory != IntPtr.Zero) GlobalFree(memory); }
    }

    private static byte[] FileDropPayload(string[] paths)
    {
        const int headerSize = 20; // DROPFILES
        var pathsText = string.Join('\0', paths) + "\0\0";
        var buffer = new byte[headerSize + Encoding.Unicode.GetByteCount(pathsText)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), 1); // fWide
        Encoding.Unicode.GetBytes(pathsText, buffer.AsSpan(headerSize));
        return buffer;
    }

    private static Win32Exception Error(string message) => new(Marshal.GetLastPInvokeError(), message);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetOpenClipboardWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}
