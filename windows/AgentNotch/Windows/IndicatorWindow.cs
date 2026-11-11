using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentNotch.Models;

namespace AgentNotch.Windows;

public enum AgentGlyphState { Inactive, Running, Done }

/// <summary>Branded pixel animations for whichever agents are running — the
/// mini Claude mascot walking, the Codex pet spritesheet, or a green "done"
/// blob. Ported from IndicatorView in main.swift.</summary>
internal sealed class IndicatorCanvas : FrameworkElement
{
    public AgentGlyphState ClaudeState { get; set; } = AgentGlyphState.Inactive;
    public AgentGlyphState CodexState { get; set; } = AgentGlyphState.Inactive;
    public AgentGlyphState CursorState { get; set; } = AgentGlyphState.Inactive;
    public double T { get; set; }

    // distinguishes "alive but quiet" (draw a dim, static glyph) from
    // "never ran" (draw nothing) — AgentGlyphState.Inactive alone can't tell
    // those apart, since it's also the enum's default value.
    public bool ClaudeLive { get; set; }
    public bool CodexLive { get; set; }
    public bool CursorLive { get; set; }

    // total live top-level sessions across both agents, shown as a badge —
    // "at work" means alive, not just mid-turn-busy, since a session sitting
    // between turns still has your attention on it.
    public int LiveCount { get; set; }

    // any session (either agent) is blocked on a permission prompt
    public bool NeedsApproval { get; set; }

    private static readonly Dictionary<string, BitmapSource> SpriteCache = new(StringComparer.OrdinalIgnoreCase);

    public static BitmapSource? LoadCodexSprite(string petId)
    {
        if (SpriteCache.TryGetValue(petId, out var cached)) return cached;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri($"pack://application:,,,/Assets/pets/pet-{petId}.png", UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            SpriteCache[petId] = bmp;
            return bmp;
        }
        catch (IOException)
        {
            // unknown pet id — no matching compiled resource
            return null;
        }
    }

    // DIP width reserved on the right for the settings gear (and its hit target).
    public const double SettingsHitWidth = 22;

    protected override void OnRender(DrawingContext dc)
    {
        var cy = ActualHeight / 2;
        var x = 6.0; // sprites sit on the left; count + gear on the right

        // Codex then Claude then Cursor so relative order stays Codex | Claude | Cursor.
        x = PlaceGlyph(dc, x, cy, CodexState, CodexLive, AgentKind.Codex);
        x = PlaceGlyph(dc, x, cy, ClaudeState, ClaudeLive, AgentKind.Claude);
        PlaceGlyph(dc, x, cy, CursorState, CursorLive, AgentKind.Cursor);

        DrawSettingsGear(dc, cy);
        if (NeedsApproval) DrawApprovalBadge(dc, cy);
        if (LiveCount > 0) DrawCountBadge(dc, cy);
    }

    private double PlaceGlyph(DrawingContext dc, double x, double cy, AgentGlyphState state, bool live, AgentKind kind)
    {
        switch (state)
        {
            case AgentGlyphState.Running:
                return DrawKind(dc, x, cy, kind, dim: false) + 4;
            case AgentGlyphState.Done:
                return DrawGreenBlob(dc, x, cy) + 4;
            case AgentGlyphState.Inactive when live:
                return DrawKind(dc, x, cy, kind, dim: true) + 4;
            default:
                return x;
        }
    }

    private double DrawKind(DrawingContext dc, double x, double cy, AgentKind kind, bool dim) => kind switch
    {
        AgentKind.Claude => DrawMascot(dc, x, cy, dim),
        AgentKind.Codex => DrawCodexPet(dc, x, cy, dim),
        _ => DrawCursorMark(dc, x, cy, dim),
    };

    private double DrawCursorMark(DrawingContext dc, double left, double cy, bool dim)
    {
        const double cell = 2.2;
        var cells = new (int x, int y)[]
        {
            (3, 0), (2, 1), (3, 1), (4, 1), (1, 2), (2, 2), (3, 2), (4, 2), (5, 2), (3, 3), (3, 4), (3, 5),
        };
        var bob = dim ? 0 : ((int)(T * 2.5) % 2 == 0 ? -0.5 : 0.5);
        var x0 = left;
        var y0 = cy - 3 * cell + bob;
        var alpha = dim ? 0.8 : 1.0;
        foreach (var (gx, gy) in cells)
        {
            dc.DrawRectangle(PixelArt.Brush(PixelArt.CursorBlue, alpha), null,
                new Rect(x0 + gx * cell, y0 + gy * cell, cell - 0.3, cell - 0.3));
        }
        return left + 7 * cell;
    }

    /// <summary>Right-side "N" badge — how many agent sessions are alive right
    /// now, independent of whether any of them happen to be mid-turn.</summary>
    private void DrawCountBadge(DrawingContext dc, double cy)
    {
        var typeface = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var text = new FormattedText(LiveCount.ToString(), System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var tx = ActualWidth - SettingsHitWidth - text.Width - (NeedsApproval ? 12 : 0);
        dc.DrawText(text, new Point(tx, cy - text.Height / 2));
    }

    private static readonly Color Amber = Color.FromRgb(255, 159, 10);

    /// <summary>A pulsing amber dot just left of the count — opposite the
    /// mascots (now left-aligned) so it never gets crowded out.</summary>
    private void DrawApprovalBadge(DrawingContext dc, double cy)
    {
        var pulse = 0.5 + 0.5 * Math.Sin(T * 3);
        const double r = 3.5;
        dc.DrawEllipse(PixelArt.Brush(Amber, 0.55 + 0.45 * pulse), null,
            new Point(ActualWidth - SettingsHitWidth - 14, cy), r, r);
    }

    private static readonly (int x, int y)[] GearCells =
    [
        (3, 0), (4, 0),
        (1, 1), (6, 1),
        (2, 2), (3, 2), (4, 2), (5, 2),
        (0, 3), (2, 3), (5, 3), (7, 3),
        (0, 4), (2, 4), (5, 4), (7, 4),
        (2, 5), (3, 5), (4, 5), (5, 5),
        (1, 6), (6, 6),
        (3, 7), (4, 7),
    ];

    private void DrawSettingsGear(DrawingContext dc, double cy)
    {
        const double cell = 1.35;
        const int grid = 8;
        var x0 = ActualWidth - 5 - grid * cell;
        var y0 = cy - grid * cell / 2;
        foreach (var (gx, gy) in GearCells)
        {
            dc.DrawRectangle(PixelArt.Brush(Colors.White, 0.72), null,
                new Rect(x0 + gx * cell, y0 + gy * cell, cell - 0.25, cell - 0.25));
        }
    }

    private double DrawGreenBlob(DrawingContext dc, double left, double cy)
    {
        const double cell = 2.5;
        const int grid = 7;
        var c = grid / 2.0;
        var step = (int)(T * 2);
        var x0 = left;
        var y0 = cy - grid * cell / 2;
        for (var i = 0; i < grid; i++)
        {
            for (var j = 0; j < grid; j++)
            {
                var dx = i + 0.5 - c;
                var dy = j + 0.5 - c;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                var r = PixelArt.Noise(i, j, step);
                if (r <= 0.1 + dist / c * 0.8) continue;
                dc.DrawRectangle(PixelArt.Brush(PixelArt.Green, 0.5 + 0.5 * r), null,
                    new Rect(x0 + i * cell, y0 + j * cell, cell - 0.5, cell - 0.5));
            }
        }
        return left + grid * cell;
    }

    /// <summary>Returns the right edge of what was drawn. <paramref name="dim"/>
    /// freezes the walk cycle and lowers opacity — "alive, listening, not
    /// mid-turn" rather than a full stop.</summary>
    private double DrawMascot(DrawingContext dc, double left, double cy, bool dim)
    {
        const double subW = 1.6, subH = 3.2;
        var walk = dim ? 0 : (int)(T * 2.5);
        var frame = PixelArt.MascotFrames[walk % 2];
        var cols = frame[0].Length * 2;
        var rows = frame.Length * 2;
        var x0 = left;
        var bob = dim ? 0 : (walk % 2 == 0) ? -0.5 : 0.5;
        var y0 = cy - rows * subH / 2 + bob;
        var step = (int)(T * 3);
        var dimMul = dim ? 0.8 : 1.0;

        for (var j = 0; j < frame.Length; j++)
        {
            var line = frame[j];
            for (var i = 0; i < line.Length; i++)
            {
                if (!PixelArt.Quadrants.TryGetValue(line[i], out var q)) continue;
                var cells = new[] { (q.ul, 0, 0), (q.ur, 1, 0), (q.ll, 0, 1), (q.lr, 1, 1) };
                foreach (var (on, qx, qy) in cells)
                {
                    if (!on) continue;
                    var n = PixelArt.Noise(i * 97 + qx, j * 97 + qy, step);
                    // feet (last row) stay solid; body shimmers gently
                    var isFeet = j == frame.Length - 1;
                    var alpha = (isFeet ? 1.0 : 0.8 + 0.2 * n) * dimMul;
                    dc.DrawRectangle(PixelArt.Brush(PixelArt.ClaudeOrange, alpha), null,
                        new Rect(x0 + (i * 2 + qx) * subW, y0 + (j * 2 + qy) * subH, subW - 0.3, subH - 0.4));
                }
            }
        }
        return left + cols * subW;
    }

    private double DrawCodexPet(DrawingContext dc, double left, double cy, bool dim)
    {
        var sprite = LoadCodexSprite(Config.PetConfig.CurrentPetId);
        if (sprite is null) { DrawRing(dc, left, cy, PixelArt.CodexTeal); return left + 22.5; }

        const double fw = 192, fh = 208;
        var idx = dim ? 0 : (int)(T / 0.12) % 8;
        var src = new Int32Rect(idx * (int)fw, (int)fh, (int)fw, (int)fh); // row 1 (0-indexed): running-right
        const double h = 26;
        var w = h * fw / fh;
        var dest = new Rect(left, cy - h / 2, w, h);

        var cropped = new CroppedBitmap(sprite, src);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        dc.PushOpacity(dim ? 0.8 : 1.0);
        dc.DrawImage(cropped, dest);
        dc.Pop();
        return left + w;
    }

    /// <summary>Fallback ring spinner when no sprite is found on disk.</summary>
    private void DrawRing(DrawingContext dc, double left, double cy, Color color)
    {
        const double cell = 2.5;
        const int grid = 9;
        var x0 = left;
        var y0 = cy - grid * cell / 2;
        var c = grid / 2.0;
        var phase = T * 1.4;
        var step = (int)(T * 3);
        for (var i = 0; i < grid; i++)
        {
            for (var j = 0; j < grid; j++)
            {
                var dx = i + 0.5 - c;
                var dy = j + 0.5 - c;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist <= c - 2.4 || dist >= c) continue;
                var angle = Math.Atan2(dy, dx) - phase;
                angle -= Math.Floor(angle / (2 * Math.PI)) * 2 * Math.PI;
                var intensity = 1 - angle / (2 * Math.PI);
                var n = PixelArt.Noise(i, j, step);
                var a = intensity * intensity * (0.55 + 0.45 * n);
                if (a <= 0.08) continue;
                dc.DrawRectangle(PixelArt.Brush(color, a), null,
                    new Rect(x0 + i * cell, y0 + j * cell, cell - 0.5, cell - 0.5));
            }
        }
    }
}

/// <summary>Tiny always-on-top overlay. Tucked (hidden) while collapsed until
/// the cursor is within 5px of its rest slot; hidden while the session panel
/// is open. Drag the tab to move it anywhere; position persists across
/// restarts. The mouse hook still owns click / drag actions because routing
/// input to a borderless overlay is unreliable.</summary>
internal sealed class IndicatorWindow : System.Windows.Window
{
    public readonly IndicatorCanvas Canvas = new();
    public bool TabVisible { get; private set; } = true;

    private static readonly Brush PillBrush = Freeze(new SolidColorBrush(Color.FromRgb(48, 48, 54)));
    private static readonly Brush PillEdge = Freeze(new SolidColorBrush(Color.FromRgb(120, 120, 128)));

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public IndicatorWindow()
    {
        WindowStyle = WindowStyle.None;
        // Solid HWND, not a WPF layered window. AllowsTransparency +
        // WS_EX_TRANSPARENT was creating a 140x30 window that never
        // composited onto the desktop (visible to PrintWindow, invisible
        // on screen).
        AllowsTransparency = false;
        Background = PillBrush;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowActivated = false;
        Cursor = Cursors.Hand;

        Content = new Border
        {
            BorderBrush = PillEdge,
            BorderThickness = new Thickness(1, 0, 1, 1),
            Child = Canvas,
        };
        Loaded += (_, _) =>
        {
            RefreshShape(hangFromTop: Top <= 1);
            NativeMethods.MakeOverlay(new WindowInteropHelper(this).Handle);
        };
    }

    /// <summary>Hanging-tab silhouette when docked at the top edge; full pill
    /// once the user has dragged it into free space.</summary>
    public void RefreshShape(bool hangFromTop)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        if (Content is Border border)
            border.BorderThickness = hangFromTop
                ? new Thickness(1, 0, 1, 1)
                : new Thickness(1);
        var radius = (int)Math.Round(10 * VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (hangFromTop) NativeMethods.ApplyHangingTabRegion(hwnd, radius);
        else NativeMethods.ApplyRoundedPillRegion(hwnd, radius);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.MakeOverlay(new WindowInteropHelper(this).Handle);
    }

    public void SetClickThrough(bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0) NativeMethods.SetClickThrough(hwnd, clickThrough);
    }

    public void KeepTopmost()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0) NativeMethods.MakeTopmost(hwnd);
    }

    public void Render()
    {
        TabVisible = true;
        Canvas.InvalidateVisual();
    }

    /// <summary>True when <paramref name="pt"/> (screen pixels) lands on the
    /// settings gear at the tab's right edge. The mouse hook hit-tests this
    /// itself because the overlay may be click-through while the panel is open.</summary>
    public bool HitSettings(NativeMethods.POINT pt)
    {
        if (!TabVisible) return false;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || !IsVisible) return false;
        if (!NativeMethods.GetWindowRect(hwnd, out var r)) return false;
        var scale = (r.Right - r.Left) / Math.Max(Width, 1);
        var hitPx = IndicatorCanvas.SettingsHitWidth * scale;
        return pt.X >= r.Right - hitPx && pt.X <= r.Right && pt.Y >= r.Top && pt.Y <= r.Bottom;
    }
}
