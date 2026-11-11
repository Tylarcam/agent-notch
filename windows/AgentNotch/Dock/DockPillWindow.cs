using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AgentNotch.Dock;

internal sealed class PillCanvas : FrameworkElement
{
    public double PulseT { get; set; }
    public bool Pulsing { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var pillW = DockTokens.CollapsedVisibleW;
        var pillH = DockTokens.CollapsedH;
        var x = w - pillW;
        var y = (h - pillH) / 2;
        var r = DockTokens.CollapsedRadius;

        var edge = DockTokens.Teal;
        if (Pulsing)
        {
            var phase = (PulseT % DockTokens.NotchPulseMs) / DockTokens.NotchPulseMs;
            var glow = 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(phase * 2 * Math.PI));
            edge = DockTokens.Yellow;
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(60 * glow), edge.R, edge.G, edge.B)), null,
                new Rect(x - 4, y - 4, pillW + 8, pillH + 8));
        }

        var fill = new SolidColorBrush(DockTokens.GlassBase);
        var geo = new RectangleGeometry(new Rect(x, y, pillW, pillH), r, r);
        dc.DrawGeometry(fill, new Pen(new SolidColorBrush(edge), 1), geo);
    }
}

internal sealed class DockPillWindow : DockOverlayWindow
{
    public readonly PillCanvas Canvas = new();
    public event Action? PillClicked;

    public DockPillWindow()
    {
        Width = DockTokens.PillHitW;
        Height = DockTokens.CollapsedH;
        Content = Canvas;
        Cursor = Cursors.Hand;
        MouseLeftButtonDown += (_, e) =>
        {
            PillClicked?.Invoke();
            e.Handled = true;
        };
    }

    public void SetPulsing(bool on) => Canvas.Pulsing = on;

    public void RenderFrame() => Canvas.InvalidateVisual();
}
