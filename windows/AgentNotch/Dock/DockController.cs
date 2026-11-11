using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AgentNotch.Discovery;
using AgentNotch.Models;
using AgentNotch.Relay;

namespace AgentNotch.Dock;

internal enum DockSurfaceState { Collapsed, Rail, Chat }

internal sealed class DockController
{
    private readonly DockPillWindow _pill = new();
    private readonly DockRailWindow _rail = new();
    private readonly DockChatWindow _chat = new();
    private readonly Dictionary<string, IOdysseusRelay> _relays = new(StringComparer.OrdinalIgnoreCase);
    private IOdysseusRelay _odysseusRelay;

    private DockSurfaceState _state = DockSurfaceState.Collapsed;
    private List<AgentSession> _sessions = [];
    private List<DockRailItem> _railItems = [];
    private string? _activeRailId;
  private double _pulseT;
    private bool _previewMode;
    private DispatcherTimer? _animTimer;

    public event Action<AgentSession, char>? ApprovalPressed;

    public DockController(bool previewDemoRelay = false)
    {
        var url = RelayConfig.TryGetBaseUrl();
        _odysseusRelay = url is not null ? new OdysseusRelayClient(url) : new StubOdysseusRelay(previewDemoRelay);
        _previewMode = previewDemoRelay;

        _pill.PillClicked += OnPillClick;
        _rail.AvatarClicked += OnAvatarClick;
        _chat.CloseRequested += CollapseAll;
        _chat.ApprovalPressed += (s, d) => ApprovalPressed?.Invoke(s, d);

        RebuildRailItems();
        PositionAll();
    }

    public void Start()
    {
        _pill.Show();
        _rail.Hide();
        _chat.Hide();
        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _animTimer.Tick += (_, _) =>
        {
            _pulseT += 50;
            _pill.Canvas.PulseT = _pulseT;
            _pill.RenderFrame();
        };
        _animTimer.Start();
    }

    public void Shutdown()
    {
        _animTimer?.Stop();
        _pill.Close();
        _rail.Close();
        _chat.Close();
        foreach (var r in _relays.Values)
            if (r is IDisposable d) d.Dispose();
        if (_odysseusRelay is IDisposable od) od.Dispose();
    }

    public void Tick(double frameT)
    {
        if (_state == DockSurfaceState.Collapsed) _pill.KeepTopmost();
        else { _rail.KeepTopmost(); _chat.KeepTopmost(); _pill.KeepTopmost(); }
        _ = frameT;
    }

    public void UpdateSessions(List<AgentSession> sessions)
    {
        _sessions = sessions;
        var pending = sessions.Any(s => s.PendingApproval is not null);
        _pill.SetPulsing(pending);
        RebuildRailItems();
        if (_activeRailId is not null)
        {
            var item = _railItems.FirstOrDefault(i => i.Id == _activeRailId);
            if (item is not null)
            {
                var relay = RelayFor(item);
                _chat.Bind(item, relay);
                _chat.UpdateApprovalCard(item.Session?.PendingApproval, item.Label);
            }
        }
        if (_state != DockSurfaceState.Collapsed)
            _rail.SetItems(_railItems, _activeRailId);
    }

    public bool IsOpen => _state != DockSurfaceState.Collapsed;

    public bool HitAny(NativeMethods.POINT pt) =>
        _pill.ContainsPoint(pt) || (_state != DockSurfaceState.Collapsed && (_rail.ContainsPoint(pt) || (_state == DockSurfaceState.Chat && _chat.ContainsPoint(pt))));

    public void HandleClickOutside()
    {
        if (_state != DockSurfaceState.Collapsed) CollapseAll();
    }

    public void HandleEscape() => CollapseAll();

    private void RebuildRailItems()
    {
        _railItems =
        [
            new DockRailItem { Id = "odysseus", Label = "Odysseus", IsOdysseus = true, Status = DockRailStatus.Idle },
        ];

        var kinds = _sessions.GroupBy(s => s.Kind).Select(g => g.First()).OrderBy(s => s.Kind);
        foreach (var s in kinds)
        {
            var status = DockRailStatus.Idle;
            if (s.PendingApproval is not null) status = DockRailStatus.WaitingApproval;
            else if (s.AnyBusy) status = DockRailStatus.Busy;
            else if (s.AnyLive) status = DockRailStatus.Busy;
            _railItems.Add(new DockRailItem
            {
                Id = s.Kind.ToString(),
                Label = s.Kind.DisplayName(),
                Kind = s.Kind,
                Session = s,
                Status = status,
            });
        }
        if (_rail.IsVisible)
            _rail.SetItems(_railItems, _activeRailId);
    }

    private IOdysseusRelay RelayFor(DockRailItem item)
    {
        if (item.IsOdysseus) return _odysseusRelay;
        if (!_relays.TryGetValue(item.Id, out var relay))
        {
            relay = new StubOdysseusRelay(_previewMode);
            _relays[item.Id] = relay;
        }
        return relay;
    }

    private void OnPillClick()
    {
        if (_state == DockSurfaceState.Collapsed)
            OpenRail();
        else if (_state == DockSurfaceState.Rail && _activeRailId is not null)
            OpenChat(_railItems.First(i => i.Id == _activeRailId));
        else
            OpenRail();
    }

    private void OnAvatarClick(DockRailItem item)
    {
        _activeRailId = item.Id;
        _rail.SetItems(_railItems, _activeRailId);
        OpenChat(item);
    }

    private void OpenRail()
    {
        _state = DockSurfaceState.Rail;
        PositionAll();
        _rail.SetItems(_railItems, _activeRailId);
        _rail.Show();
        _chat.Hide();
        AnimateOpacity(_rail, 0, 1, DockTokens.RailOpenMs);
    }

    private void OpenChat(DockRailItem item)
    {
        _state = DockSurfaceState.Chat;
        _activeRailId = item.Id;
        PositionAll();
        _rail.SetItems(_railItems, _activeRailId);
        _rail.Show();
        _chat.Bind(item, RelayFor(item));
        _chat.Show();
        AnimateOpacity(_rail, _rail.Opacity, 1, DockTokens.RailOpenMs);
        AnimateOpacity(_chat, 0, 1, DockTokens.ChatOpenMs);
    }

    private void CollapseAll()
    {
        _state = DockSurfaceState.Collapsed;
        _chat.Hide();
        _rail.Hide();
        PositionAll();
    }

    private static void AnimateOpacity(UIElement target, double from, double to, int ms)
    {
        target.Opacity = from;
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        target.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void PositionAll()
    {
        var wa = SystemParameters.WorkArea;
        var right = wa.Right;
        var midY = wa.Top + wa.Height / 2;

        _pill.Left = right - DockTokens.PillHitW;
        _pill.Top = midY - DockTokens.CollapsedH / 2;

        _rail.Left = right - DockTokens.RailW;
        _rail.Top = midY - DockTokens.RailH / 2;

        var chatLeft = right - DockTokens.RailW - DockTokens.ChatRailGap - DockTokens.ChatW;
        _chat.Left = chatLeft;
        _chat.Top = wa.Top + Math.Max(8, (wa.Height - DockTokens.ChatH) / 2);
    }

    // --- Preview helpers ---

    public DockPillWindow Pill => _pill;
    public DockRailWindow Rail => _rail;
    public DockChatWindow Chat => _chat;

    public void ForceStateForPreview(DockSurfaceState state, string? activeId = "odysseus")
    {
        _activeRailId = activeId;
        RebuildRailItems();
        PositionAll();
        switch (state)
        {
            case DockSurfaceState.Collapsed:
                CollapseAll();
                _pill.Show();
                break;
            case DockSurfaceState.Rail:
                _state = DockSurfaceState.Rail;
                _rail.SetItems(_railItems, _activeRailId);
                _rail.Opacity = 1;
                _rail.Show();
                _chat.Hide();
                _pill.Show();
                break;
            case DockSurfaceState.Chat:
                _state = DockSurfaceState.Chat;
                var item = _railItems.First(i => i.Id == activeId);
                _rail.SetItems(_railItems, activeId);
                _rail.Opacity = 1;
                _rail.Show();
                _chat.Bind(item, RelayFor(item));
                _chat.Opacity = 1;
                _chat.Show();
                _pill.Show();
                break;
        }
    }

    public void SeedPreviewApproval()
    {
        var cursor = _sessions.FirstOrDefault(s => s.Kind == AgentKind.Cursor)
            ?? new AgentSession
            {
                Id = "cursor-preview",
                ThreadId = "cursor-preview",
                Kind = AgentKind.Cursor,
                Title = "agent-notch",
                LastModified = DateTime.UtcNow,
                IsLive = true,
            };
        cursor.PendingApproval ??= new ApprovalRequest(
            "npm test -- --run",
            "Shell",
            DateTime.UtcNow);
        cursor.PendingMarkerId ??= "cursor-preview";
        if (!_sessions.Contains(cursor)) _sessions.Add(cursor);
        _pill.SetPulsing(true);
        UpdateSessions(_sessions);
        var item = _railItems.FirstOrDefault(i => i.Kind == AgentKind.Cursor) ?? _railItems[0];
        ForceStateForPreview(DockSurfaceState.Chat, item.Id);
        _chat.UpdateApprovalCard(cursor.PendingApproval, cursor.Kind.DisplayName());
    }
}
