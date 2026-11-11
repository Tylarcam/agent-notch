using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AgentNotch.Discovery;
using AgentNotch.Models;

namespace AgentNotch.Windows;

/// <summary>Row icon: mini mascot / Codex pet while running, green pixel
/// checkmark when done. Ported from DitherIconView in main.swift.</summary>
internal sealed class RowIcon : FrameworkElement
{
    public bool Running { get; set; }
    public bool Idle { get; set; } // alive but quiet: dim, static
    public AgentKind Kind { get; set; }
    public double T { get; set; }

    public RowIcon() { Width = 18; Height = 16; }

    protected override void OnRender(DrawingContext dc)
    {
        if (!Running && !Idle)
        {
            const double cell = 2.2;
            foreach (var (x, y) in PixelArt.CheckmarkCells)
            {
                dc.DrawRectangle(new SolidColorBrush(PixelArt.Green), null,
                    new Rect(1 + x * cell, 1 + y * cell, cell - 0.4, cell - 0.4));
            }
            return;
        }
        var alpha = Idle ? 0.4 : 1.0;
        if (Kind == AgentKind.Codex)
        {
            var sprite = IndicatorCanvas.LoadCodexSprite(Config.PetConfig.CurrentPetId);
            if (sprite is not null)
            {
                const double fw = 192, fh = 208;
                var idx = Idle ? 0 : (int)(T / 0.12) % 8;
                var src = new Int32Rect(idx * (int)fw, (int)fh, (int)fw, (int)fh);
                var cropped = new System.Windows.Media.Imaging.CroppedBitmap(sprite, src);
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
                var w = 16 * fw / fh;
                dc.PushOpacity(alpha);
                dc.DrawImage(cropped, new Rect(1, 0, w, 16));
                dc.Pop();
                return;
            }
        }
        if (Kind != AgentKind.Claude)
        {
            var color = Kind == AgentKind.Cursor ? PixelArt.CursorBlue : PixelArt.OtherGray;
            DrawCursorMark(dc, color, alpha);
            return;
        }
        // mini Claude mascot walking, with a visible bob (static + dim when idle)
        const double subW = 1.0, subH = 2.0;
        var walk = Idle ? 0 : (int)(T * 2.5);
        var frame = PixelArt.MascotFrames[walk % 2];
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
                    dc.DrawRectangle(PixelArt.Brush(PixelArt.ClaudeOrange, alpha), null,
                        new Rect((i * 2 + qx) * subW, (j * 2 + qy) * subH, subW - 0.2, subH - 0.3));
                }
            }
        }
    }

    private void DrawCursorMark(DrawingContext dc, Color color, double alpha)
    {
        const double cell = 2.0;
        var cells = new (int x, int y)[] { (3, 0), (2, 1), (3, 1), (4, 1), (1, 2), (2, 2), (3, 2), (4, 2), (5, 2), (3, 3), (3, 4), (3, 5) };
        var bob = Running ? ((int)(T * 2.5) % 2) : 0;
        foreach (var (x, y) in cells)
        {
            dc.DrawRectangle(PixelArt.Brush(color, alpha), null,
                new Rect(1 + x * cell, bob + y * cell, cell - 0.3, cell - 0.3));
        }
    }
}

/// <summary>A sparse row of gray pixels — the dithered stand-in for a separator line.</summary>
internal sealed class DitherSeparator : FrameworkElement
{
    public DitherSeparator() { Height = 4; }

    protected override void OnRender(DrawingContext dc)
    {
        const double cell = 2;
        double x = 0;
        ulong seed = 0x9E3779B9;
        while (x < ActualWidth)
        {
            unchecked { seed = seed * 6364136223846793005 + 1442695040888963407; }
            var r = (seed >> 33 & 0xFFFF) / 65535.0;
            if (r > 0.55)
            {
                dc.DrawRectangle(PixelArt.Brush(Colors.White, 0.06 + 0.10 * r), null,
                    new Rect(x, 1, cell - 0.4, cell - 0.4));
            }
            x += cell;
        }
    }
}

/// <summary>Expandable session-list panel: one row per session, subagents
/// folded under a "▸ N subagents" toggle. Ported from SessionListController.</summary>
internal sealed class PanelWindow : System.Windows.Window
{
    private readonly StackPanel _stack = new() { Margin = new Thickness(12, 10, 12, 10) };
    private readonly HashSet<string> _expanded = [];
    private readonly HashSet<string> _archived = [];
    private readonly List<RowIcon> _icons = [];
    private List<AgentSession> _sessions = [];
    private IReadOnlyList<ServiceStatus> _services = [];
    private bool _systemsOpen;
    private string? _serviceDetailId;
    private string? _selectedId;
    private static readonly string ArchivePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "agent-notch", "archived");

    /// <summary>Raised when the harness pill is clicked: live sessions focus
    /// their host window, finished ones relaunch resuming the chat. Row click
    /// only enlarges/collapses the row.</summary>
    public event Action<AgentSession>? SessionActivated;

    /// <summary>Raised when a quick-approve button is clicked, with the digit
    /// (1/2/3) matching Claude Code's own permission-prompt choices.</summary>
    public event Action<AgentSession, char>? ApprovalKeyPressed;

    public PanelWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowActivated = false;
        SizeToContent = SizeToContent.Height; // grows/shrinks with content; Top stays anchored
        // Alpha=1 fill: with AllowsTransparency, fully transparent pixels
        // pass clicks through to the window behind the panel.
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0, 0, 0)),
            CornerRadius = new CornerRadius(0, 0, 16, 16),
            Child = _stack,
        };
        Content = border;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.MakeToolWindow(new WindowInteropHelper(this).Handle);
    }

    public void SetSessions(List<AgentSession> sessions)
    {
        _sessions = sessions;
        Rebuild();
    }

    /// <summary>Replace the systems catalog. Pass rebuild:false when the
    /// caller is about to SetSessions, which rebuilds once with both.</summary>
    public void SetServices(IReadOnlyList<ServiceStatus> services, bool rebuild = true)
    {
        _services = services;
        if (rebuild) Rebuild();
    }

    public void Tick(double dt)
    {
        foreach (var icon in _icons) icon.T += dt;
    }

    private void Rebuild()
    {
        _stack.Children.Clear();
        _icons.Clear();

        var showedSystems = AddSystems();

        LoadArchived();
        var visible = _sessions.Where(s => !_archived.Contains(s.Id)).ToList();
        if (visible.Count == 0)
        {
            if (showedSystems)
                _stack.Children.Add(new DitherSeparator { Margin = new Thickness(0, 4, 0, 4) });
            _stack.Children.Add(Label("No recent agent sessions", 12, Color.FromRgb(152, 152, 157), false));
            return;
        }

        if (showedSystems)
            _stack.Children.Add(new DitherSeparator { Margin = new Thickness(0, 4, 0, 4) });

        var shown = visible.Take(8).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            var s = shown[i];
            if (i > 0) _stack.Children.Add(new DitherSeparator { Margin = new Thickness(0, 2, 0, 2) });

            _stack.Children.Add(Row(s));

            if (s.Children.Count > 0)
            {
                var open = _expanded.Contains(s.Id);
                var btn = new Button
                {
                    Content = $"{(open ? "▾" : "▸")} {s.Children.Count} subagent{(s.Children.Count == 1 ? "" : "s")}",
                    Foreground = Brushes.DodgerBlue,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(24, 0, 0, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                };
                btn.Click += (_, _) =>
                {
                    if (!_expanded.Add(s.Id)) _expanded.Remove(s.Id);
                    Rebuild();
                };
                _stack.Children.Add(btn);

                if (open)
                {
                    foreach (var child in s.Children.Take(8))
                        _stack.Children.Add(ChildRow(child));
                }
            }
        }
    }

    private bool AddSystems()
    {
        if (_services.Count == 0) return false;

        var down = _services.Count(s => !s.Up);
        var count = down > 0 ? $"{down} down" : $"{_services.Count(s => s.Up)} up";
        var toggle = new Button
        {
            Content = $"{(_systemsOpen ? "▾" : "▸")} Systems",
            Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 214)),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand,
        };
        var countLabel = Label(count, 10, down > 0 ? Color.FromRgb(255, 159, 10) : PixelArt.Green, true);
        var header = new DockPanel { Margin = new Thickness(0, 0, 4, 2) };
        DockPanel.SetDock(countLabel, System.Windows.Controls.Dock.Right);
        header.Children.Add(countLabel);
        header.Children.Add(toggle);
        toggle.Click += (_, _) =>
        {
            _systemsOpen = !_systemsOpen;
            Rebuild();
        };
        _stack.Children.Add(header);

        if (!_systemsOpen) return true;

        foreach (var svc in _services)
            _stack.Children.Add(ServiceRow(svc));
        return true;
    }

    private FrameworkElement ServiceRow(ServiceStatus svc)
    {
        var open = _serviceDetailId == svc.Id;
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = new SolidColorBrush(svc.Up ? PixelArt.Green : Color.FromRgb(110, 110, 115)),
            Margin = new Thickness(10, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var name = Label(svc.Name, 11, Colors.White, true);
        name.VerticalAlignment = VerticalAlignment.Center;
        var pill = Pill(svc.Summary, Color.FromArgb(30, 255, 255, 255),
            svc.Up ? PixelArt.Green : Color.FromRgb(152, 152, 157));
        pill.VerticalAlignment = VerticalAlignment.Center;

        var top = new DockPanel();
        DockPanel.SetDock(dot, System.Windows.Controls.Dock.Left);
        DockPanel.SetDock(pill, System.Windows.Controls.Dock.Right);
        top.Children.Add(dot);
        top.Children.Add(pill);
        top.Children.Add(name);

        var col = new StackPanel { Margin = new Thickness(0, 1, 0, 1) };
        col.Children.Add(top);
        if (open && !string.IsNullOrEmpty(svc.Detail))
        {
            var detail = Label(svc.Detail, 10, Color.FromRgb(152, 152, 157), false);
            detail.Margin = new Thickness(24, 0, 8, 2);
            detail.MaxWidth = 460;
            detail.TextWrapping = TextWrapping.Wrap;
            col.Children.Add(detail);
        }

        var card = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(4, 2, 4, 2),
            Child = col,
            Cursor = Cursors.Hand,
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _serviceDetailId = open ? null : svc.Id;
            Rebuild();
        };
        return card;
    }

    private FrameworkElement Row(AgentSession s)
    {
        var open = _selectedId == s.Id;
        var icon = new RowIcon { Running = s.AnyBusy, Idle = s.AnyLive && !s.AnyBusy, Kind = s.Kind };
        _icons.Add(icon);

        var headline = (s.Title, s.ChatTitle) switch
        {
            ("", "") => s.Harness,
            (_, "") => s.Title,
            ("", _) => s.ChatTitle,
            _ => $"{s.Title} · {s.ChatTitle}",
        };
        var title = Label(headline, 12, Colors.White, true);
        title.VerticalAlignment = VerticalAlignment.Center;

        var pills = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(-15, 0, 0, 0),
        };
        var hue = s.Kind switch
        {
            AgentKind.Claude => PixelArt.ClaudeOrange,
            AgentKind.Codex => PixelArt.CodexTeal,
            AgentKind.Cursor => PixelArt.CursorBlue,
            _ => PixelArt.OtherGray,
        };
        if (!open)
            pills.Children.Add(HarnessPill(s, Color.FromArgb(224, hue.R, hue.G, hue.B)));
        if (open && !string.IsNullOrEmpty(s.Model))
            pills.Children.Add(Pill(s.Model, Color.FromArgb(30, 255, 255, 255), Color.FromRgb(152, 152, 157)));
        pills.Children.Add(Pill(Relative(s.LastModified), Color.FromArgb(30, 255, 255, 255), Color.FromRgb(152, 152, 157)));
        if (open) pills.Children.Add(ArchiveButton(s));

        var top = new DockPanel();
        DockPanel.SetDock(icon, System.Windows.Controls.Dock.Left);
        var spacer = new Border { Width = 6 };
        DockPanel.SetDock(spacer, System.Windows.Controls.Dock.Left);
        DockPanel.SetDock(pills, System.Windows.Controls.Dock.Right);
        top.Children.Add(icon);
        top.Children.Add(spacer);
        top.Children.Add(pills);
        top.Children.Add(title);

        var col = new StackPanel();
        col.Children.Add(top);

        var query = string.IsNullOrEmpty(s.Prompt) ? s.Snippet : "You: " + s.Prompt;
        if (!string.IsNullOrEmpty(query))
        {
            var q = Label(query, 11, Color.FromRgb(152, 152, 157), false);
            q.MaxWidth = 480;
            col.Children.Add(q);
        }
        if (open)
        {
            if (!string.IsNullOrEmpty(s.Goal))
            {
                var g = Label("Goal: " + s.Goal, 10, Color.FromRgb(152, 152, 157), false);
                g.MaxWidth = 480;
                g.TextWrapping = TextWrapping.Wrap;
                g.MaxHeight = 36;
                col.Children.Add(g);
            }
            if (!string.IsNullOrEmpty(s.Snippet) && s.Snippet != s.Prompt && s.Snippet != s.Goal)
            {
                var latest = Label("Latest: " + s.Snippet, 10, Color.FromRgb(152, 152, 157), false);
                latest.MaxWidth = 480;
                latest.TextWrapping = TextWrapping.Wrap;
                latest.MaxHeight = 36;
                col.Children.Add(latest);
            }
        }
        if (!string.IsNullOrEmpty(s.FileName))
            col.Children.Add(Label(s.FileName, 9, Color.FromRgb(110, 110, 115), false));
        if (s.PendingApproval is { } req) col.Children.Add(ApprovalBanner(s, req));

        var card = new Border
        {
            Background = open
                ? new SolidColorBrush(Color.FromArgb(15, 255, 255, 255))
                : Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, open ? 8 : 4, 8, open ? 8 : 4),
            Child = col,
            Cursor = Cursors.Hand,
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject src)
            {
                if (FindAncestor<Button>(src) is not null) return;
                if (FindAncestor<Border>(src) is Border { Tag: "harness-pill" }) return;
            }
            e.Handled = true;
            _selectedId = _selectedId == s.Id ? null : s.Id;
            Rebuild();
        };
        return card;
    }

    private Border HarnessPill(AgentSession s, Color fill)
    {
        var pill = Pill(s.Harness, fill);
        pill.Tag = "harness-pill";
        pill.Cursor = Cursors.Hand;
        pill.ToolTip = "Show this agent's window";
        pill.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SessionActivated?.Invoke(s);
        };
        return pill;
    }

    private static Border Pill(string text, Color fill, Color? textColor = null) => new()
    {
        Background = new SolidColorBrush(fill),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(6, 2, 6, 2),
        Margin = new Thickness(0, 0, 4, 0),
        Child = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(textColor ?? Colors.White),
        },
    };

    private Button ArchiveButton(AgentSession s)
    {
        var btn = new Button
        {
            Content = "\uE7B8",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(110, 110, 115)),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2, 4, 2),
            Cursor = Cursors.Hand,
            ToolTip = "Archive",
        };
        btn.Click += (_, e) =>
        {
            _archived.Add(s.Id);
            if (_selectedId == s.Id) _selectedId = null;
            PersistArchived();
            Rebuild();
            e.Handled = true;
        };
        return btn;
    }

    private void LoadArchived()
    {
        try
        {
            if (!File.Exists(ArchivePath)) return;
            _archived.Clear();
            foreach (var line in File.ReadAllLines(ArchivePath))
            {
                if (!string.IsNullOrWhiteSpace(line)) _archived.Add(line.Trim());
            }
        }
        catch
        {
            // missing or unreadable dismiss list — show everything
        }
    }

    private void PersistArchived()
    {
        try
        {
            var dir = Path.GetDirectoryName(ArchivePath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllLines(ArchivePath, _archived.OrderBy(x => x));
        }
        catch
        {
            // best-effort persist
        }
    }

    private static readonly Color Amber = Color.FromRgb(255, 159, 10);

    private FrameworkElement ApprovalBanner(AgentSession s, ApprovalRequest req)
    {
        var col = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

        var tool = req.Tool;
        var hasTool = !string.IsNullOrEmpty(tool);
        var headline = hasTool ? $"Needs permission: {tool}" : "Needs permission";
        col.Children.Add(Label(headline, 11, Amber, true));

        var msg = Label(req.Message, 10, Color.FromRgb(210, 210, 214), false);
        msg.MaxWidth = 440;
        msg.TextWrapping = TextWrapping.Wrap;
        col.Children.Add(msg);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(ApprovalButton("1 · Yes", '1', s));
        buttons.Children.Add(ApprovalButton(hasTool ? "2 · Always" : "2 · Yes, don't ask again", '2', s));
        buttons.Children.Add(ApprovalButton("3 · No", '3', s));

        col.Children.Add(buttons);

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(30, Amber.R, Amber.G, Amber.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, Amber.R, Amber.G, Amber.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Child = col,
        };
        return border;
    }

    private Button ApprovalButton(string text, char digit, AgentSession s)
    {
        var btn = new Button
        {
            Content = text,
            Foreground = new SolidColorBrush(Amber),
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, Amber.R, Amber.G, Amber.B)),
            BorderThickness = new Thickness(1),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        btn.Click += (_, _) => ApprovalKeyPressed?.Invoke(s, digit);
        return btn;
    }

    private FrameworkElement ChildRow(AgentSession s)
    {
        var icon = new RowIcon { Running = s.IsBusy, Idle = s.IsLive && !s.IsBusy, Kind = s.Kind };
        _icons.Add(icon);

        var name = Label(s.Nickname ?? (string.IsNullOrEmpty(s.Title) ? s.Kind.DisplayName() : s.Title), 11, Color.FromRgb(152, 152, 157), true);
        var tagColor = s.IsBusy ? Color.FromRgb(10, 132, 255) : s.IsLive ? Color.FromRgb(152, 152, 157) : PixelArt.Green;
        var tag = Label($"{(string.IsNullOrEmpty(s.Model) ? s.Kind.DisplayName() : s.Model)} · {Relative(s.LastModified)}", 9, tagColor, false);

        var top = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(icon, System.Windows.Controls.Dock.Left);
        DockPanel.SetDock(tag, System.Windows.Controls.Dock.Right);
        top.Children.Add(icon);
        top.Children.Add(new Border { Width = 4 });
        top.Children.Add(name);
        top.Children.Add(tag);

        var col = new StackPanel { Margin = new Thickness(28, 2, 4, 2) };
        col.Children.Add(top);
        if (!string.IsNullOrEmpty(s.Snippet))
        {
            var snip = Label(s.Snippet, 11, Color.FromRgb(152, 152, 157), false);
            snip.MaxWidth = 400;
            col.Children.Add(snip);
        }
        return col;
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var d = start; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is T match) return match;
        }
        return null;
    }

    private static TextBlock Label(string text, double size, Color color, bool bold) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Consolas"),
        FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = new SolidColorBrush(color),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static string Relative(DateTime d)
    {
        var s = (int)(DateTime.UtcNow - d).TotalSeconds;
        if (s < 60) return "now";
        if (s < 3600) return $"{s / 60}m";
        if (s < 86400) return $"{s / 3600}h";
        return $"{s / 86400}d";
    }
}
