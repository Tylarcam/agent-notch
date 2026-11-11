using System.Windows.Media;

namespace AgentNotch.Dock;

/// <summary>Design tokens from docs/dock-design/tokens.json.</summary>
internal static class DockTokens
{
    public static readonly Color Bg = (Color)ColorConverter.ConvertFromString("#0d1717")!;
    public static readonly Color Text = (Color)ColorConverter.ConvertFromString("#aadcef")!;
    public static readonly Color Teal = (Color)ColorConverter.ConvertFromString("#8bd9da")!;
    public static readonly Color Muted = (Color)ColorConverter.ConvertFromString("#46575e")!;
    public static readonly Color Muted2 = (Color)ColorConverter.ConvertFromString("#7495a1")!;
    public static readonly Color Yellow = (Color)ColorConverter.ConvertFromString("#D8CB03")!;
    public static readonly Color UserBubble = (Color)ColorConverter.ConvertFromString("#162022")!;
    public static readonly Color Deny = (Color)ColorConverter.ConvertFromString("#c45b5b")!;
    public static readonly Color MicIcon = (Color)ColorConverter.ConvertFromString("#8199f5")!;

    public const double CollapsedVisibleW = 10;
    public const double CollapsedH = 196;
    public const double CollapsedRadius = 5;
    public const double PillHitW = 24;

    public const double RailW = 42;
    public const double RailH = 285;
    public const double RailRadius = 21;
    public const double RailPadY = 12;
    public const double AvatarSize = 24;
    public const double AvatarPitch = 39;
    public const double AvatarRing = 2;
    public const double StatusDot = 8;

    public const double ChatW = 428;
    public const double ChatH = 601;
    public const double ChatRadius = 16;
    public const double ChatRailGap = 19;
    public const double HeaderH = 36;
    public const double MessagePadX = 16;
    public const double ComposerH = 38;
    public const double ComposerInset = 12;
    public const double ComposerRadius = 20;

    public const int RailOpenMs = 100;
    public const int ChatOpenMs = 280;
    public const int SendPopMs = 180;
    public const int ApprovalResolveMs = 220;
    public const int NotchPulseMs = 1200;

    public static readonly Color GlassBase = Color.FromArgb(148, 12, 20, 12);
    public static readonly Color GlassBorder = Color.FromArgb(36, 139, 217, 218);
}
