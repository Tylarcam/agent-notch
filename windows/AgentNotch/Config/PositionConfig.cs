using System.Globalization;
using System.IO;

namespace AgentNotch.Config;

/// <summary>Optional on-disk position file at ~/.config/agent-notch/position.
/// The live app uses session-only placement (default top-center on launch;
/// drag moves it until quit), so this file is cleared on startup. Kept so a
/// future "remember position" preference can reuse the same path.</summary>
internal static class PositionConfig
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "agent-notch", "position");

    public static double? Left { get; private set; }
    public static double? Top { get; private set; }

    public static bool HasCustomPosition => Left is not null && Top is not null;

    public static void Refresh()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                Left = Top = null;
                return;
            }
            var text = File.ReadAllText(ConfigPath).Trim();
            var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var left)) return;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var top)) return;
            if (double.IsNaN(left) || double.IsNaN(top) || double.IsInfinity(left) || double.IsInfinity(top)) return;
            Left = left;
            Top = top;
        }
        catch
        {
            // mid-write / locked — keep last good in-memory values
        }
    }

    public static void Save(double left, double top)
    {
        Left = left;
        Top = top;
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(ConfigPath,
                string.Create(CultureInfo.InvariantCulture, $"{left:0.###},{top:0.###}"));
        }
        catch
        {
            // directory permissions or locked file — in-memory value still applies
        }
    }

    public static void Clear()
    {
        Left = Top = null;
        try
        {
            if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
        }
        catch
        {
            // ignore — default positioning still applies in-memory
        }
    }
}
