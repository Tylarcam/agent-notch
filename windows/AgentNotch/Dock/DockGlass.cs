using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace AgentNotch.Dock;

internal static class DockGlass
{
    public static Border Panel(double radius, Thickness? padding = null)
    {
        var b = new Border
        {
            Background = new SolidColorBrush(DockTokens.GlassBase),
            BorderBrush = new SolidColorBrush(DockTokens.GlassBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radius),
            Padding = padding ?? new Thickness(0),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                Opacity = 0.55,
                BlurRadius = 24,
                ShadowDepth = 10,
                Direction = 270,
            },
        };
        return b;
    }
}
