using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Mike.Desktop;

public sealed class ComputerActionRequest
{
    public string Action { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public string Text { get; set; } = "";
    public long WindowHandle { get; set; }
}

public static class WindowsComputerOperator
{
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;

    public static bool Validate(ComputerActionRequest request, out string error)
    {
        error = "";
        if (request is null) { error = "Acao ausente."; return false; }
        if (request.Action is not ("focus" or "move" or "click" or "type" or "key"))
        { error = "Acao de computador nao permitida."; return false; }
        if (request.Action is "move" or "click")
        {
            int width = GetSystemMetrics(0), height = GetSystemMetrics(1);
            if (request.X < 0 || request.Y < 0 || request.X >= width || request.Y >= height)
            { error = "Coordenada fora da tela principal."; return false; }
        }
        if (request.Action == "type" && (request.Text.Length == 0 || request.Text.Length > 2000 || request.Text.Any(char.IsControl)))
        { error = "Texto vazio, longo demais ou com controles."; return false; }
        if (request.Action == "key" && !AllowedKeys.ContainsKey(request.Text))
        { error = "Tecla nao permitida."; return false; }
        if (request.Action == "focus" && request.WindowHandle <= 0)
        { error = "Janela invalida."; return false; }
        return true;
    }

    public static object Observe()
    {
        if (!OperatingSystem.IsWindows()) return new { ok = false, error = "Windows necessario." };
        var windows = new List<object>();
        EnumWindows((handle, _) => {
            if (!IsWindowVisible(handle)) return true;
            int length = GetWindowTextLength(handle);
            if (length <= 0 || length > 2048) return true;
            var title = new StringBuilder(length + 1);
            GetWindowText(handle, title, title.Capacity);
            GetWindowThreadProcessId(handle, out uint processId);
            string process = "";
            try { process = Process.GetProcessById((int)processId).ProcessName; } catch { }
            windows.Add(new { handle = handle.ToInt64(), title = title.ToString(), process });
            return windows.Count < 100;
        }, IntPtr.Zero);
        return new { ok = true, windows, cursor = CursorPosition() };
    }

    public static object Execute(ComputerActionRequest request)
    {
        if (!OperatingSystem.IsWindows()) return new { ok = false, error = "Windows necessario." };
        if (!Validate(request, out string error)) return new { ok = false, error };
        bool result = request.Action switch
        {
            "focus" => SetForegroundWindow(new IntPtr(request.WindowHandle)),
            "move" => SetCursorPos(request.X, request.Y),
            "click" => Click(request.X, request.Y),
            "type" => TypeUnicode(request.Text),
            "key" => PressKey(AllowedKeys[request.Text]),
            _ => false
        };
        return new { ok = result, action = request.Action };
    }

    private static object CursorPosition() => GetCursorPos(out Point point) ? new { x = point.X, y = point.Y } : new { x = -1, y = -1 };
    private static bool Click(int x, int y) { if (!SetCursorPos(x, y)) return false; mouse_event(MouseLeftDown, 0, 0, 0, UIntPtr.Zero); mouse_event(MouseLeftUp, 0, 0, 0, UIntPtr.Zero); return true; }
    private static bool PressKey(byte key) { keybd_event(key, 0, 0, UIntPtr.Zero); keybd_event(key, 0, 2, UIntPtr.Zero); return true; }
    private static bool TypeUnicode(string text)
    {
        foreach (char character in text)
        {
            var inputs = new[] {
                new Input { Type = 1, Union = new InputUnion { Keyboard = new KeyboardInput { Scan = character, Flags = 0x0004 } } },
                new Input { Type = 1, Union = new InputUnion { Keyboard = new KeyboardInput { Scan = character, Flags = 0x0004 | 0x0002 } } }
            };
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length) return false;
        }
        return true;
    }

    private static readonly Dictionary<string, byte> AllowedKeys = new(StringComparer.OrdinalIgnoreCase) {
        ["enter"] = 0x0D, ["escape"] = 0x1B, ["tab"] = 0x09, ["backspace"] = 0x08,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22
    };

    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern void keybd_event(byte virtualKey, byte scan, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey; public ushort Scan; public uint Flags; public uint Time; public UIntPtr ExtraInfo; }
}
