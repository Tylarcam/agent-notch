using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using DrawingSize = System.Drawing.Size;

namespace AgentNotch.Dock;

/// <summary>Tracks last non–Agent Notch foreground HWND and captures it to PNG.</summary>
internal static class ForegroundCapture
{
    private static nint _lastNonDockHwnd;
    private static readonly int OurPid = Process.GetCurrentProcess().Id;

    public static void NoteForeground(nint hwnd)
    {
        if (hwnd == 0) return;
        if (IsOurWindow(hwnd)) return;
        if (!NativeMethods.IsWindowVisible(hwnd)) return;
        _lastNonDockHwnd = hwnd;
    }

    public static byte[]? CaptureForAttach()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0 || IsOurWindow(hwnd))
            hwnd = _lastNonDockHwnd;
        if (hwnd == 0 || !NativeMethods.GetWindowRect(hwnd, out var rect))
            return CapturePrimaryScreen();
        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return CapturePrimaryScreen();
        return CaptureHwnd(hwnd, w, h);
    }

    private static bool IsOurWindow(nint hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == OurPid;
    }

    private static byte[]? CaptureHwnd(nint hwnd, int w, int h)
    {
        try
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                NativeMethods.PrintWindow(hwnd, hdc, 0);
                g.ReleaseHdc(hdc);
            }
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch
        {
            return CapturePrimaryScreen();
        }
    }

    private static byte[]? CapturePrimaryScreen()
    {
        try
        {
            var wa = SystemParameters.WorkArea;
            var w = Math.Max(1, (int)wa.Width);
            var h = Math.Max(1, (int)wa.Height);
            var left = (int)wa.Left;
            var top = (int)wa.Top;
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(left, top, 0, 0, new DrawingSize(w, h), CopyPixelOperation.SourceCopy);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
