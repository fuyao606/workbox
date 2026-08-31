using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Timenotifier.Services.Evidence;

public sealed class ManualEvidenceHotkeyService(Action onHotkey) : IDisposable
{
    private const int HotkeyId = 9001;
    private HwndSource? _source;
    private IntPtr _handle;

    public bool Register(string hotkey, WindowInteropHelper window)
    {
        Unregister();
        _handle = window.Handle;
        if (_handle == IntPtr.Zero || !TryParseHotkey(hotkey, out var modifiers, out var key))
        {
            return false;
        }

        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);
        return RegisterHotKey(_handle, HotkeyId, modifiers, key);
    }

    public void Unregister()
    {
        if (_handle != IntPtr.Zero)
        {
            UnregisterHotKey(_handle, HotkeyId);
        }

        _source?.RemoveHook(WndProc);
        _source = null;
        _handle = IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            onHotkey();
        }

        return IntPtr.Zero;
    }

    private static bool TryParseHotkey(string value, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        foreach (var part in value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= 0x0002;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= 0x0001;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= 0x0004;
            }
            else if (part.Length == 1)
            {
                key = char.ToUpperInvariant(part[0]);
            }
        }

        return modifiers != 0 && key != 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
