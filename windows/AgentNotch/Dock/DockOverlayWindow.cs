using System.Windows;
using System.Windows.Interop;

namespace AgentNotch.Dock;

internal abstract class DockOverlayWindow : Window
{
    protected DockOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowActivated = false;
        Loaded += (_, _) => NativeMethods.MakeOverlay(new WindowInteropHelper(this).Handle);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.MakeOverlay(new WindowInteropHelper(this).Handle);
    }

    public void KeepTopmost()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0) NativeMethods.MakeTopmost(hwnd);
    }

    public bool ContainsPoint(NativeMethods.POINT pt)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || !IsVisible) return false;
        return NativeMethods.GetWindowRect(hwnd, out var r)
            && pt.X >= r.Left && pt.X <= r.Right && pt.Y >= r.Top && pt.Y <= r.Bottom;
    }
}
