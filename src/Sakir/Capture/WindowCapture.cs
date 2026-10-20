using System.Runtime.InteropServices;
using System.Text;

namespace Sakir.Capture;

public sealed record WindowInfo(IntPtr Handle, string Title, string ProcessName)
{
    public string Display => $"{Title}  -  {ProcessName}";
}

/// <summary>
/// Captures a single window's contents.
///
/// Uses PrintWindow with PW_RENDERFULLCONTENT, which asks the window to redraw
/// itself into our device context. Unlike copying pixels off the screen, that
/// works while the window is completely covered by other windows - which is the
/// whole point: you should not have to keep the thing you are mirroring visible.
///
/// Two honest limits: a MINIMISED window has nothing to render, and some
/// hardware-accelerated or DRM-protected surfaces come back black. If a
/// particular app does that, the modern Windows.Graphics.Capture API handles it,
/// at the cost of a lot more Direct3D plumbing.
/// </summary>
public static class WindowCapture
{
    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const int DWMWA_CLOAKED = 14;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    public static Bitmap? Capture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || IsIconic(hwnd)) return null;

        if (!GetWindowRect(hwnd, out RECT r)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0 || w > 16384 || h > 16384) return null;

        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            using Graphics g = Graphics.FromImage(bmp);
            IntPtr hdc = g.GetHdc();
            bool ok;
            try { ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT); }
            finally { g.ReleaseHdc(hdc); }

            if (!ok) { bmp.Dispose(); return null; }
            return bmp;
        }
        catch { bmp.Dispose(); return null; }
    }

    /// <summary>Top-level windows worth offering as a mirror source.</summary>
    public static List<WindowInfo> List()
    {
        var found = new List<WindowInfo>();
        int self = Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;

            int len = GetWindowTextLength(hwnd);
            if (len == 0) return true;

            // Windows 10+ keeps invisible "cloaked" shells around (suspended UWP
            // apps, ghost windows); they enumerate as visible but render nothing.
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            string title = sb.ToString().Trim();
            if (title.Length == 0) return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == self) return true;               // our own window

            string proc;
            try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
            catch { return true; }

            found.Add(new WindowInfo(hwnd, title, proc));
            return true;
        }, IntPtr.Zero);

        return found.OrderBy(f => f.ProcessName).ThenBy(f => f.Title).ToList();
    }

    /// <summary>
    /// Re-finds a window after a restart. Window handles do not survive the
    /// target app closing and reopening, so the choice is stored as process name
    /// plus title and resolved again here.
    /// </summary>
    public static IntPtr Resolve(string processName, string title)
    {
        if (string.IsNullOrWhiteSpace(processName)) return IntPtr.Zero;
        List<WindowInfo> all = List();

        WindowInfo? hit = all.FirstOrDefault(w =>
            w.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase) &&
            w.Title.Equals(title, StringComparison.OrdinalIgnoreCase));

        // title drifts constantly (browsers, players), so fall back to any
        // window of the same process
        hit ??= all.FirstOrDefault(w =>
            w.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase));

        return hit?.Handle ?? IntPtr.Zero;
    }

    // ---------------------------------------------------------------- interop --
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
}
