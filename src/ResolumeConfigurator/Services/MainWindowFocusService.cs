using System.Runtime.InteropServices;
using System.Text;

namespace ResolumeConfigurator.Services;

public static class MainWindowFocusService
{
    private const uint DecoderHelperStatusMessage = 0x8001;
    private static readonly IntPtr HwndNotTopmost = new(-2);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;
    private const int SwRestore = 9;

    public static async Task ReturnToMainWindowAsync(string status, CancellationToken ct)
    {
        IntPtr window = IntPtr.Zero;
        for (var attempt = 0; attempt < 40 && window == IntPtr.Zero; attempt++)
        {
            window = FindMainWindow();
            if (window == IntPtr.Zero) await Task.Delay(250, ct).ConfigureAwait(false);
        }
        if (window == IntPtr.Zero) return;

        SetWindowText(window, $"Resolume Arena Configurator — {status}");
        ShowWindow(window, SwRestore);
        SetWindowPos(window, HwndNotTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
        SetForegroundWindow(window);
        PostMessage(window, DecoderHelperStatusMessage, status.StartsWith("complete", StringComparison.OrdinalIgnoreCase) ? new IntPtr(1) : new IntPtr(2), IntPtr.Zero);
    }

    private static IntPtr FindMainWindow()
    {
        var found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            GetWindowThreadProcessId(window, out var processId);
            if (processId == (uint)Environment.ProcessId) return true;
            var length = GetWindowTextLength(window);
            if (length <= 0) return true;
            var title = new StringBuilder(length + 1);
            GetWindowText(window, title, title.Capacity);
            if (!title.ToString().StartsWith("Resolume Arena Configurator", StringComparison.OrdinalIgnoreCase)) return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowText(IntPtr window, string text);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
