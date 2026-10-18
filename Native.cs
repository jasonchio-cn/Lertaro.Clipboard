using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace Lertaro.Plugins.Clipboard;

internal static class Native
{
    [DllImport("user32.dll")] internal static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] internal static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    internal delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module,
        WinEventProc callback, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    { public ushort Key; public ushort Scan; public uint Flags; public uint Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }

    internal static bool ExternalWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid != Environment.ProcessId && pid != 0;
    }
    internal static string SourceName(IntPtr hwnd)
    {
        try { GetWindowThreadProcessId(hwnd, out var pid); using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch { return "未知应用"; }
    }
    internal static bool ModifiersDown() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(x => (GetAsyncKeyState(x) & 0x8000) != 0);
    internal static void PasteKeys()
    {
        Input Key(ushort key, bool up) => new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = up ? 2u : 0 } } };
        var inputs = new[] { Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true) };
        if (SendInput(4, inputs, Marshal.SizeOf<Input>()) != 4)
        {
            // Release synthetic keys if Windows accepted only a prefix of the input batch.
            SendInput(2, [Key(0x56, true), Key(0x11, true)], Marshal.SizeOf<Input>());
            throw new InvalidOperationException("无法发送粘贴，请检查目标窗口权限。");
        }
    }
    internal static (uint Modifiers, uint Key) ParseHotkey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (0, 0);
        var tokens = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) throw new ArgumentException("全局快捷键至少包含一个修饰键。");
        uint mods = 0x4000;
        foreach (var token in tokens[..^1]) mods |= token.ToLowerInvariant() switch
        {
            "ctrl" or "control" => 2u, "alt" => 1u, "shift" => 4u, "win" or "windows" => 8u,
            _ => throw new ArgumentException("快捷键修饰键无效。")
        };
        if (tokens.Length < 2 || mods == 0x4000) throw new ArgumentException("全局快捷键至少包含一个修饰键。");
        var name = tokens[^1];
        if (name.Length == 1 && char.IsDigit(name[0])) name = "D" + name;
        if (!Enum.TryParse<Key>(name, true, out var key) || key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            throw new ArgumentException("快捷键按键无效。");
        return (mods, (uint)KeyInterop.VirtualKeyFromKey(key));
    }
}
