using System.Windows.Media;

namespace AgentNotch;

/// <summary>Shared pixel-art data/helpers ported from IndicatorView + DitherIconView
/// in main.swift: the block-character mascot, the quadrant bitmap it's built from,
/// and the tiny pseudo-random "dither shimmer" used throughout.</summary>
internal static class PixelArt
{
    public static readonly Color ClaudeOrange = Color.FromRgb(217, 120, 87);   // Anthropic coral
    public static readonly Color CodexTeal = Color.FromRgb(15, 163, 128);      // OpenAI teal
    public static readonly Color CursorBlue = Color.FromRgb(80, 160, 255);
    public static readonly Color OtherGray = Color.FromRgb(170, 170, 176);
    public static readonly Color Green = Color.FromRgb(48, 209, 88);

    // The Claude Code launch-banner mascot, drawn from its real block characters.
    // Two frames: the feet alternate so it walks. Rows read top-to-bottom as
    // they appear on screen (WPF is a top-down coordinate system, no flip needed).
    public static readonly string[][] MascotFrames =
    [
        [" ▐▛███▜▌ ", "▝▜█████▙▘", "  ▘▘ ▝▝  "],
        [" ▐▛███▜▌ ", "▝▜█████▙▘", "  ▝▝ ▘▘  "],
    ];

    // quadrant bits: (upper-left, upper-right, lower-left, lower-right)
    public static readonly Dictionary<char, (bool ul, bool ur, bool ll, bool lr)> Quadrants = new()
    {
        ['█'] = (true, true, true, true),   // █
        ['▐'] = (false, true, false, true), // ▐
        ['▌'] = (true, false, true, false), // ▌
        ['▛'] = (true, true, true, false),  // ▛
        ['▜'] = (true, true, false, true),  // ▜
        ['▙'] = (true, false, true, true),  // ▙
        ['▟'] = (false, true, true, true),  // ▟
        ['▘'] = (true, false, false, false),// ▘
        ['▝'] = (false, true, false, false),// ▝
        ['▖'] = (false, false, true, false),// ▖
        ['▗'] = (false, false, false, true),// ▗
        [' '] = (false, false, false, false),
    };

    // Green pixel checkmark, drawn top-down (row 0 = top) — the mirror of
    // DitherIconView.checkmark, which flips for AppKit's bottom-up views.
    public static readonly (int x, int y)[] CheckmarkCells =
    [
        (6, 6), (5, 5), (4, 4), (0, 4), (1, 3), (3, 3), (2, 2),
    ];

    /// <summary>Deterministic pseudo-random in [0,1) — the same hash-based
    /// "shimmer" noise the Swift views use instead of a real RNG (keeps frames
    /// reproducible from (i, j, step) alone, no seeded generator to carry around).</summary>
    public static double Noise(int i, int j, int step)
    {
        double n = Math.Sin(i * 374761 + j * 668265 + step * 982451) * 43758.5453;
        return n - Math.Floor(n);
    }

    public static SolidColorBrush Brush(Color c, double alpha) =>
        new(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));
}
