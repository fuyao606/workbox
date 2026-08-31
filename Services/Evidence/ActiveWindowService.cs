using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Timenotifier.Services.Evidence;

public interface IActiveWindowService
{
    ActiveWindowInfo GetActiveWindow();
}

public sealed record ActiveWindowInfo(string Title, string ProcessName);

public sealed class ActiveWindowService : IActiveWindowService
{
    public ActiveWindowInfo GetActiveWindow()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return new ActiveWindowInfo("", "");
        }

        var title = ReadWindowTitle(handle);
        GetWindowThreadProcessId(handle, out var processId);
        var processName = "";
        try
        {
            processName = Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
        }

        return new ActiveWindowInfo(title, processName);
    }

    private static string ReadWindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return "";
        }

        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
