using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AgentNotch.Dock;

internal sealed class RailAvatar : FrameworkElement
{
    public DockRailItem? Item { get; set; }
    public bool Active { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        if (Item is null) return;
        const double s = DockTokens.AvatarSize;
        var cx = ActualWidth / 2;
        var cy = ActualHeight / 2;
        var color = Item.IsOdysseus ? DockTokens.Teal
            : Item.Kind switch
            {
                Models.AgentKind.Claude => PixelArt.ClaudeOrange,
                Models.AgentKind.Codex => PixelArt.CodexTeal,
                Models.AgentKind.Cursor => PixelArt.CursorBlue,
                _ => PixelArt.OtherGray,
            };
        dc.DrawEllipse(new SolidColorBrush(color), null, new Point(cx, cy), s / 2, s / 2);

        if (Active)
        {
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(DockTokens.Teal), DockTokens.AvatarRing),
                new Point(cx, cy), s / 2 + 1, s / 2 + 1);
        }

        if (Item.Status != DockRailStatus.Idle)
        {
            var dot = DockTokens.StatusDot;
            var dotColor = Item.Status is DockRailStatus.WaitingApproval or DockRailStatus.NeedsYou
                ? DockTokens.Yellow : DockTokens.Teal;
            var dx = cx + s * 0.28;
            var dy = cy + s * 0.28;
            dc.DrawEllipse(new SolidColorBrush(DockTokens.Bg), null, new Point(dx, dy), dot / 2 + 1.5, dot / 2 + 1.5);
            dc.DrawEllipse(new SolidColorBrush(dotColor), null, new Point(dx, dy), dot / 2, dot / 2);
            if (Item.Status == DockRailStatus.WaitingApproval)
            {
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(DockTokens.Yellow), 2),
                    new Point(cx, cy), s / 2 + 3, s / 2 + 3);
            }
        }
    }
}

internal sealed class DockRailWindow : DockOverlayWindow
{
    private readonly StackPanel _stack = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _chrome;
    public event Action<DockRailItem>? AvatarClicked;

    public DockRailWindow()
    {
        _chrome = DockGlass.Panel(DockTokens.RailRadius, new Thickness(0, DockTokens.RailPadY, 0, DockTokens.RailPadY));
        _chrome.CornerRadius = new CornerRadius(DockTokens.RailRadius, 0, 0, DockTokens.RailRadius);
        _chrome.Child = _stack;
        Width = DockTokens.RailW;
        Height = DockTokens.RailH;
        Content = _chrome;
        Opacity = 0;
    }

    public void SetItems(IReadOnlyList<DockRailItem> items, string? activeId)
    {
        _stack.Children.Clear();
        foreach (var item in items)
        {
            var host = new Border
            {
                Width = DockTokens.RailW,
                Height = DockTokens.AvatarPitch,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = new RailAvatar { Item = item, Active = item.Id == activeId, Width = DockTokens.RailW, Height = DockTokens.AvatarPitch },
            };
            host.MouseLeftButtonDown += (_, _) => AvatarClicked?.Invoke(item);
            _stack.Children.Add(host);
        }
    }

    public void SetSlideProgress(double t) => Opacity = Math.Clamp(t, 0, 1);
}
