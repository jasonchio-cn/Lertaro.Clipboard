using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace Lertaro.Plugins.Clipboard;

// Keep the source application's cell representation opaque. The receiving
// spreadsheet interprets formulas and adjusts references for the destination.
internal static class SpreadsheetClipboard
{
    internal const string Format = "Biff12";
    private static readonly uint FormatId = (uint)DataFormats.GetDataFormat(Format).Id;
    internal static bool IsAvailable() => IsClipboardFormatAvailable(FormatId);

    internal static ClipRecord Capture(ClipRecord text, long maxBytes)
    {
        var remaining = maxBytes - text.Bytes;
        if (remaining <= 0) return text;
        if (!OpenClipboard(IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        byte[] bytes;
        try
        {
            var handle = GetClipboardData(FormatId);
            var size = handle == IntPtr.Zero ? 0 : GlobalSize(handle);
            if (size == 0 || size > (ulong)remaining || size > int.MaxValue) return text;
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) return text;
            try
            {
                bytes = new byte[(int)size];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
            }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
        return WithBiff12(text, bytes);
    }

    internal static ClipRecord WithBiff12(ClipRecord text, byte[] bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes(text.Hash));
        hash.AppendData(bytes);
        return text with { Biff12 = bytes, Bytes = text.Bytes + bytes.Length, Hash = "biff12:" + Convert.ToHexString(hash.GetHashAndReset()) };
    }

    internal static DataObject ForPaste(ClipRecord item)
    {
        var data = new DataObject();
        data.SetData(Format, new MemoryStream(item.Biff12!, writable: false), false);
        data.SetText(item.Text, TextDataFormat.UnicodeText);
        return data;
    }

    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
}
