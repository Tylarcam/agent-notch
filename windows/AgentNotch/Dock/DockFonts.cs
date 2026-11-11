using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;

namespace AgentNotch.Dock;

internal static class DockFonts
{
    public static FontFamily Family { get; }

    static DockFonts()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var uri = new Uri($"pack://application:,,,/Assets/fonts/FiraCode-Variable.ttf", UriKind.Absolute);
            Family = new FontFamily(uri, "./#Fira Code");
        }
        catch
        {
            Family = new FontFamily("Consolas");
        }
    }

    public static Typeface Body(double size, FontWeight weight = default) =>
        new(Family, FontStyles.Normal, weight == default ? FontWeights.Normal : weight, FontStretches.Normal);

    public static FormattedText Ft(string text, double size, Brush brush, FontWeight weight = default) =>
        new(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Body(size, weight), size, brush, 1.0);
}
