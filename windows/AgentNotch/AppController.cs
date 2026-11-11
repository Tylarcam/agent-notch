using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AgentNotch.Config;
using AgentNotch.Discovery;
using AgentNotch.Dock;
using AgentNotch.Models;
using AgentNotch.Windows;

namespace AgentNotch;

/// <summary>Owns the two overlay windows, the poll/animation timers, and the
/// global input hooks. Ported from AppDelegate in main.swift.</summary>
internal sealed class AppController
{
    private readonly ProcessDiscovery _discovery = new();
    private readonly ServiceProbe _probe = new();
    private readonly SessionScanner _scanner = new();
    private readonly IndicatorWindow _indicatorWindow = new();
    private readonly PanelWindow _panelWindow = new();
    private readonly DockController _dock = new();

    private bool _expanded;
    private bool _scanning;
    private double _frameT;
    private List<AgentSession> _sessions = [];
    private AgentGlyphState _claudeState = AgentGlyphState.Inactive;
    private AgentGlyphState _codexState = AgentGlyphState.Inactive;
    private AgentGlyphState _cursorState = AgentGlyphState.Inactive;
    private bool _claudeWasLive;
    private bool _codexWasLive;
    private bool _cursorWasLive;
    private DateTime _lastToggle = DateTime.MinValue;
    private int _tickN;
    private bool _indicatorClickThrough;
    private bool _settingsMenuOpen;
    private bool _tabRevealed;
    private bool _tabHideScheduled;
    /// <summary>After Esc tucks the tab, suppress hover-reveal until the
    /// cursor leaves the hot zone — otherwise Esc would hide it and the next
    /// mouse-move would pop it right back.</summary>
    private bool _tabEscDismissed;
    private DispatcherTimer? _tabHideTimer;
    private const int TabHoverPadPx = 5;
    private const int DragThresholdPx = 10;
    private static readonly TimeSpan DragWatchdogIdle = TimeSpan.FromSeconds(5);
    private const double IndicatorWidth = 140;
    private const double IndicatorHeight = 42;

    // Press on the tab starts a gesture: moving past DragThresholdPx drags it,
    // releasing before that is a click (opens the panel). The gesture is driven
    // entirely by the mouse hook's own messages — the hook eats LBUTTONDOWN, so
    // the system's async key state never sees the button as held. Session-only;
    // a restart returns to the default top-center notch slot.
    private bool _dragPending;
    private bool _dragging;
    private NativeMethods.POINT _dragStartPt;
    private NativeMethods.POINT _dragLatestPt;
    private bool _dragMoveQueued;
    private DateTime _lastGestureEventUtc;
    private int _dragGrabOffsetPxX;
    private int _dragGrabOffsetPxY;
    private nint _indicatorHwnd;
    private bool _hangFromTop = true;
    private bool _hasSessionPosition;
    // Cached DIPs for hover hit-testing while the window is tucked.
    private double _indicatorLeftDip;
    private double _indicatorTopDip;
    private double _pixelsPerDip = 1;
    private int _virtLeftPx;
    private int _virtTopPx;
    private int _virtMaxLeftPx;
    private int _virtMaxTopPx;
    private DispatcherTimer? _dragWatchdog;

    // Kept alive for the lifetime of the app — GC'd delegates passed to
    // SetWindowsHookEx/SetWinEventHook silently break the native callback.
    private NativeMethods.LowLevelMouseProc? _mouseProc;
    private NativeMethods.LowLevelKeyboardProc? _keyboardProc;
    private NativeMethods.WinEventDelegate? _winEventProc;
    private nint _mouseHook;
    private nint _keyboardHook;
    private nint _winEventHook;

    public void Start()
    {
        // Session-only placement: always launch at the default top-center notch
        // slot. Drag moves it for this run; a restart returns here.
        PositionConfig.Clear();
        PositionIndicator();
        PositionPanel();
        _panelWindow.Hide();
        _indicatorWindow.Show(); // create HWND + hanging region, then tuck
        CacheIndicatorHwnd();
        RefreshVirtualBoundsPx();
        _indicatorWindow.Hide();
        _tabRevealed = false;

        InstallMouseHook();
        InstallKeyboardHook();
        InstallForegroundHook();
        _panelWindow.SessionActivated += OnSessionActivated;
        _panelWindow.ApprovalKeyPressed += OnApprovalKeyPressed;
        _dock.ApprovalPressed += OnApprovalKeyPressed;
        _dock.Start();

        var animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        animTimer.Tick += (_, _) => Tick();
        animTimer.Start();

        var scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        scanTimer.Tick += (_, _) => _ = RescanAsync();
        scanTimer.Start();

        _ = RescanAsync();
    }

    private void PositionIndicator()
    {
        _indicatorWindow.Width = IndicatorWidth;
        _indicatorWindow.Height = IndicatorHeight;
        _indicatorWindow.Canvas.Width = IndicatorWidth;
        _indicatorWindow.Canvas.Height = IndicatorHeight;
        _panelWindow.Width = 520;

        var (left, top) = DefaultIndicatorPosition();
        _hasSessionPosition = false;
        ApplyIndicatorPosition(left, top);
    }

    /// <summary>Top-center of the primary screen — the normal notch slot.</summary>
    private static (double Left, double Top) DefaultIndicatorPosition() =>
        (SystemParameters.PrimaryScreenWidth / 2 - IndicatorWidth / 2, 0);

    /// <summary>Where the tab rests right now: wherever the user dragged it this
    /// session, or the default notch slot if they haven't.</summary>
    private (double Left, double Top) CurrentRestPosition()
    {
        if (_hasSessionPosition)
            return ClampToVirtualScreen(_indicatorLeftDip, _indicatorTopDip, IndicatorWidth, IndicatorHeight);
        return DefaultIndicatorPosition();
    }

    private void ApplyIndicatorPosition(double left, double top)
    {
        _indicatorWindow.Left = left;
        _indicatorWindow.Top = top;
        _indicatorLeftDip = left;
        _indicatorTopDip = top;
        var scale = VisualTreeHelper.GetDpi(_indicatorWindow).PixelsPerDip;
        if (scale > 0) _pixelsPerDip = scale;
        RefreshVirtualBoundsPx();
        var hang = top <= 1;
        if (hang != _hangFromTop || _indicatorHwnd == 0)
        {
            _hangFromTop = hang;
            _indicatorWindow.RefreshShape(hangFromTop: hang);
        }
        CacheIndicatorHwnd();
    }

    private void CacheIndicatorHwnd()
    {
        var hwnd = new WindowInteropHelper(_indicatorWindow).Handle;
        if (hwnd != 0) _indicatorHwnd = hwnd;
    }

    private void RefreshVirtualBoundsPx()
    {
        if (_pixelsPerDip <= 0) _pixelsPerDip = 1;
        var wPx = (int)Math.Round(IndicatorWidth * _pixelsPerDip);
        var hPx = (int)Math.Round(IndicatorHeight * _pixelsPerDip);
        _virtLeftPx = (int)Math.Round(SystemParameters.VirtualScreenLeft * _pixelsPerDip);
        _virtTopPx = (int)Math.Round(SystemParameters.VirtualScreenTop * _pixelsPerDip);
        var virtRight = _virtLeftPx + (int)Math.Round(SystemParameters.VirtualScreenWidth * _pixelsPerDip);
        var virtBottom = _virtTopPx + (int)Math.Round(SystemParameters.VirtualScreenHeight * _pixelsPerDip);
        _virtMaxLeftPx = Math.Max(_virtLeftPx, virtRight - wPx);
        _virtMaxTopPx = Math.Max(_virtTopPx, virtBottom - hPx);
    }

    /// <summary>Place the tab from a physical-pixel top-left. Always runs on
    /// the UI thread so WPF Left/Top and the HWND stay aligned.</summary>
    private void PlaceIndicatorPhysical(int leftPx, int topPx)
    {
        if (_pixelsPerDip <= 0) _pixelsPerDip = 1;
        if (_virtMaxLeftPx <= _virtLeftPx || _virtMaxTopPx <= _virtTopPx)
            RefreshVirtualBoundsPx();
        if (_virtMaxLeftPx <= _virtLeftPx || _virtMaxTopPx <= _virtTopPx)
        {
            _virtMaxLeftPx = _virtLeftPx + 10000;
            _virtMaxTopPx = _virtTopPx + 10000;
        }

        leftPx = Math.Clamp(leftPx, _virtLeftPx, _virtMaxLeftPx);
        topPx = Math.Clamp(topPx, _virtTopPx, _virtMaxTopPx);
        var left = leftPx / _pixelsPerDip;
        var top = topPx / _pixelsPerDip;
        ApplyIndicatorPosition(left, top);
        CacheIndicatorHwnd();
        if (_indicatorHwnd != 0)
        {
            NativeMethods.SetWindowPos(_indicatorHwnd, NativeMethods.HWND_TOPMOST, leftPx, topPx, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        _hasSessionPosition = true;
    }

    /// <summary>Sync WPF Left/Top from the live HWND after a drag.</summary>
    private void SyncIndicatorFromHwnd()
    {
        CacheIndicatorHwnd();
        if (_indicatorHwnd == 0 || !NativeMethods.GetWindowRect(_indicatorHwnd, out var r)) return;
        if (_pixelsPerDip <= 0) _pixelsPerDip = 1;
        ApplyIndicatorPosition(r.Left / _pixelsPerDip, r.Top / _pixelsPerDip);
        _hasSessionPosition = true;
    }

    private void PositionPanel()
    {
        // Sit just under the indicator, horizontally centered on it.
        var panelW = _panelWindow.Width;
        var left = _indicatorWindow.Left + IndicatorWidth / 2 - panelW / 2;
        var top = _indicatorWindow.Top + IndicatorHeight + 4;
        // When the tab is still the default top dock, keep the familiar flush-top panel.
        if (_indicatorWindow.Top <= 1)
            top = 0;
        (left, top) = ClampToVirtualScreen(left, top, panelW, Math.Max(_panelWindow.ActualHeight, 120));
        _panelWindow.Left = left;
        _panelWindow.Top = top;
    }

    private static (double Left, double Top) ClampToVirtualScreen(double left, double top, double width, double height)
    {
        var minX = SystemParameters.VirtualScreenLeft;
        var minY = SystemParameters.VirtualScreenTop;
        var maxX = minX + SystemParameters.VirtualScreenWidth - width;
        var maxY = minY + SystemParameters.VirtualScreenHeight - height;
        if (maxX < minX) maxX = minX;
        if (maxY < minY) maxY = minY;
        return (Math.Clamp(left, minX, maxX), Math.Clamp(top, minY, maxY));
    }

    private void Tick()
    {
        _frameT += 0.12;
        _indicatorWindow.Canvas.T = _frameT;
        _indicatorWindow.Render();
        if (_expanded) _panelWindow.Tick(0.12);
        _dock.Tick(_frameT);
        if (!_expanded && _tabRevealed && ++_tickN % 25 == 0) _indicatorWindow.KeepTopmost();
    }

    private async Task RescanAsync()
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            var (live, sessions, services) = await Task.Run(() =>
            {
                var l = _discovery.LiveAgentProcesses();
                var s = _scanner.Scan(l);
                var svc = _probe.Probe();
                return (l, s, svc);
            });

            PetConfig.Refresh();
            _sessions = sessions;
            _panelWindow.SetServices(services, rebuild: false);
            _panelWindow.SetSessions(sessions);

            bool AnyLive(AgentKind k) => sessions.Any(s => s.Kind == k && s.AnyLive);
            bool AnyBusy(AgentKind k) => sessions.Any(s => s.Kind == k && s.AnyBusy);

            var claudeLive = AnyLive(AgentKind.Claude);
            var claudeBusy = AnyBusy(AgentKind.Claude);
            var codexLive = AnyLive(AgentKind.Codex);
            var codexBusy = AnyBusy(AgentKind.Codex);
            var cursorLive = AnyLive(AgentKind.Cursor);
            var cursorBusy = AnyBusy(AgentKind.Cursor);

            // busy -> mascot; alive-but-quiet -> dim glyph; process exited -> done blob
            _claudeState = claudeBusy ? AgentGlyphState.Running
                : claudeLive ? AgentGlyphState.Inactive
                : _claudeWasLive ? AgentGlyphState.Done : _claudeState;
            _codexState = codexBusy ? AgentGlyphState.Running
                : codexLive ? AgentGlyphState.Inactive
                : _codexWasLive ? AgentGlyphState.Done : _codexState;
            _cursorState = cursorBusy ? AgentGlyphState.Running
                : cursorLive ? AgentGlyphState.Inactive
                : _cursorWasLive ? AgentGlyphState.Done : _cursorState;
            _claudeWasLive = claudeLive;
            _codexWasLive = codexLive;
            _cursorWasLive = cursorLive;

            _indicatorWindow.Canvas.ClaudeState = _claudeState;
            _indicatorWindow.Canvas.CodexState = _codexState;
            _indicatorWindow.Canvas.CursorState = _cursorState;
            _indicatorWindow.Canvas.ClaudeLive = claudeLive;
            _indicatorWindow.Canvas.CodexLive = codexLive;
            _indicatorWindow.Canvas.CursorLive = cursorLive;
            _indicatorWindow.Canvas.LiveCount = sessions.Count(s => s.AnyLive);
            _indicatorWindow.Canvas.NeedsApproval = sessions.Any(s => s.PendingApproval is not null);
            _indicatorWindow.Render();
            _dock.UpdateSessions(sessions);
            _ = live; // discovery snapshots already folded into `sessions`
        }
        catch
        {
            // WMI/transcript scan can fail without taking down the overlay
        }
        finally
        {
            _scanning = false;
        }
    }

    /// <summary>Live session -> bring its host app to the front (herdr's own
    /// API when it's managing the pane, else the pid-based window walk).
    /// Finished session -> open a new terminal resuming that exact chat.</summary>
    private void OnSessionActivated(AgentSession s)
    {
        System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentnotch-debug.log"),
            $"{DateTime.Now:HH:mm:ss.fff} OnSessionActivated: title={s.Title} kind={s.Kind} isLive={s.IsLive} anyLive={s.AnyLive} herdr={s.HerdrTerminalId ?? "nil"} pid={s.Pid}\n");
        SetExpanded(false);
        // AnyLive covers grouped Codex threads; HerdrTerminalId covers herdr-
        // managed panes even when process discovery missed the agent pid.
        if (s.AnyLive || !string.IsNullOrEmpty(s.HerdrTerminalId) || s.Id.StartsWith("herdr:", StringComparison.Ordinal))
        {
            _ = Task.Run(() => ProcessDiscovery.FocusSession(s));
        }
        else if (!string.IsNullOrEmpty(s.Cwd))
        {
            _ = Task.Run(() => ProcessDiscovery.LaunchResume(s));
        }
    }

    /// <summary>Injects the chosen digit into the session's console, then
    /// clears the banner immediately rather than waiting for the next poll
    /// to notice the transcript moved on.</summary>
    private void OnApprovalKeyPressed(AgentSession s, char digit)
    {
        if (s.Kind == AgentKind.Cursor)
            ApprovalStore.WriteDecision(s, digit == '1');
        else if (s.Pid is int pid)
            _ = Task.Run(() => ProcessDiscovery.SendApprovalKey(pid, digit));
        ApprovalStore.Clear(s);
        s.PendingApproval = null;
        s.PendingMarkerId = null;
        _panelWindow.SetSessions(_sessions);
    }

    private void SetExpanded(bool on)
    {
        if (_expanded == on) return;
        _expanded = on;
        if (on)
        {
            _tabHideTimer?.Stop();
            _tabHideScheduled = false;
            _tabEscDismissed = false;
            _tabRevealed = false;
            PositionPanel();
            _panelWindow.Opacity = 0;
            _panelWindow.Show();
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));
            _panelWindow.BeginAnimation(UIElement.OpacityProperty, fade);
            _indicatorWindow.Hide();
        }
        else
        {
            _panelWindow.Hide();
            SetIndicatorClickThrough(false);
            // Stay tucked after Esc, or when the pointer isn't in the hover pad.
            if (!_tabEscDismissed
                && NativeMethods.GetCursorPos(out var pt) && CursorNearTab(pt))
                RevealTab();
            else
                SetTabRevealed(false);
        }
    }

    private void SetIndicatorClickThrough(bool clickThrough)
    {
        if (_indicatorClickThrough == clickThrough) return;
        _indicatorClickThrough = clickThrough;
        _indicatorWindow.SetClickThrough(clickThrough);
    }

    /// <summary>Collapsed tab rest rect, inflated by 5 physical pixels. Works
    /// while the window is hidden so the hot zone still exists when tucked.</summary>
    private bool CursorNearTab(NativeMethods.POINT pt)
    {
        var (leftDip, topDip) = CurrentRestPosition();
        var scale = VisualTreeHelper.GetDpi(_indicatorWindow).PixelsPerDip;
        if (scale <= 0) scale = 1;

        // Prefer the live HWND rect when visible (tracks an in-progress drag);
        // otherwise use the persisted/default rest slot so hover still works while tucked.
        double left, top, right, bottom;
        var hwnd = new WindowInteropHelper(_indicatorWindow).Handle;
        if (_tabRevealed && hwnd != 0 && NativeMethods.GetWindowRect(hwnd, out var r) && r.Right > r.Left)
        {
            left = r.Left;
            right = r.Right;
            top = r.Top;
            bottom = r.Bottom;
        }
        else
        {
            left = leftDip * scale;
            top = topDip * scale;
            right = left + IndicatorWidth * scale;
            bottom = top + IndicatorHeight * scale;
        }
        return pt.X >= left - TabHoverPadPx && pt.X <= right + TabHoverPadPx
            && pt.Y >= top - TabHoverPadPx && pt.Y <= bottom + TabHoverPadPx;
    }

    private void RevealTab()
    {
        // Esc-dismiss: stay tucked until the cursor leaves the hot zone and
        // re-enters. Ignore queued mouse-hook BeginInvokes that raced Esc.
        if (_tabEscDismissed) return;
        _tabHideTimer?.Stop();
        _tabHideScheduled = false;
        SetTabRevealed(true);
    }

    private void ScheduleTabHide()
    {
        if (_tabHideScheduled || _expanded || _settingsMenuOpen || _dragging || _dragPending) return;
        _tabHideScheduled = true;
        _tabHideTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _tabHideTimer.Tick -= HideTabTick;
        _tabHideTimer.Tick += HideTabTick;
        _tabHideTimer.Stop();
        _tabHideTimer.Start();
    }

    private void HideTabTick(object? sender, EventArgs e)
    {
        _tabHideTimer?.Stop();
        _tabHideScheduled = false;
        if (!_expanded && !_settingsMenuOpen) SetTabRevealed(false);
    }

    private void SetTabRevealed(bool on)
    {
        if (_expanded) on = false;
        if (_tabRevealed == on && _indicatorWindow.IsVisible == on) return;
        _tabRevealed = on;
        if (on)
        {
            if (!_dragging && !_dragPending)
            {
                var (left, top) = CurrentRestPosition();
                ApplyIndicatorPosition(left, top);
            }
            _indicatorWindow.Show();
            CacheIndicatorHwnd();
            SetIndicatorClickThrough(false);
            _indicatorWindow.KeepTopmost();
        }
        else
        {
            _indicatorWindow.Hide();
        }
    }

    // --- input hooks ---

    private void InstallMouseHook()
    {
        _mouseProc = MouseHookCallback;
        using var proc = Process.GetCurrentProcess();
        using var module = proc.MainModule!;
        _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc,
            NativeMethods.GetModuleHandle(module.ModuleName), 0);
    }

    private nint MouseHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var data = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            var pt = data.pt;

            // An in-flight press/drag owns every mouse message until release.
            if (_dragPending || _dragging)
            {
                HandleGestureMessage(wParam, pt);
                return 1;
            }

            var overTab = !_expanded && CursorNearTab(pt);
            // After Esc, the hot zone is inert: clicks pass through to windows
            // behind, and hover won't reveal until the cursor leaves then returns.
            var tabArmed = overTab && !_tabEscDismissed;
            var overPanel = _expanded && PointInWindow(_panelWindow, pt, pad: 0);

            if (wParam == NativeMethods.WM_MOUSEMOVE)
            {
                if (!_expanded)
                {
                    if (!overTab && _tabEscDismissed)
                        _tabEscDismissed = false;
                    if (tabArmed && !_tabRevealed)
                        Application.Current.Dispatcher.BeginInvoke(RevealTab);
                    else if (!overTab && _tabRevealed && !_settingsMenuOpen)
                        Application.Current.Dispatcher.BeginInvoke(ScheduleTabHide);
                }
            }
            else if (wParam == NativeMethods.WM_LBUTTONDOWN)
            {
                var now = DateTime.UtcNow;
                if (!_expanded)
                {
                    if (tabArmed && (now - _lastToggle).TotalMilliseconds > 150)
                    {
                        _lastToggle = now;
                        if (_indicatorWindow.HitSettings(pt))
                            Application.Current.Dispatcher.BeginInvoke(() => ShowSettingsMenu(pt));
                        else
                        {
                            // Arm synchronously so the very next move / up is
                            // routed to HandleGestureMessage.
                            _dragPending = true;
                            _dragging = false;
                            _dragStartPt = pt;
                            _lastGestureEventUtc = now;
                            Application.Current.Dispatcher.BeginInvoke(() => BeginSessionDrag(pt));
                        }
                    }
                    else if (_dock.IsOpen && !_dock.HitAny(pt))
                        Application.Current.Dispatcher.BeginInvoke(() => _dock.HandleClickOutside());
                }
                else if (!overPanel && !_dock.HitAny(pt))
                {
                    Application.Current.Dispatcher.BeginInvoke(() => SetExpanded(false));
                }
                else if (_dock.IsOpen && !overPanel && !_dock.HitAny(pt))
                {
                    Application.Current.Dispatcher.BeginInvoke(() => _dock.HandleClickOutside());
                }
            }
            else if (wParam == NativeMethods.WM_RBUTTONDOWN && tabArmed)
            {
                Application.Current.Dispatcher.BeginInvoke(() => ShowSettingsMenu(pt));
            }

            // Eat pointer actions only while the tab is interactively armed —
            // after Esc the zone must be click-through until re-armed.
            if (IsPointerAction(wParam) && tabArmed && !_expanded && !_settingsMenuOpen)
                return 1;
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>Press → (move past threshold → drag) → release. Runs inside the
    /// hook, so it only records state and defers window work to the dispatcher.</summary>
    private void HandleGestureMessage(nint wParam, NativeMethods.POINT pt)
    {
        _lastGestureEventUtc = DateTime.UtcNow;
        if (wParam == NativeMethods.WM_MOUSEMOVE)
        {
            if (!_dragging)
            {
                var dx = pt.X - _dragStartPt.X;
                var dy = pt.Y - _dragStartPt.Y;
                if (dx * dx + dy * dy < DragThresholdPx * DragThresholdPx) return;
                _dragging = true;
                _dragPending = false;
            }
            _dragLatestPt = pt;
            if (_dragMoveQueued) return;
            _dragMoveQueued = true;
            Application.Current.Dispatcher.BeginInvoke(ApplyDragMove);
        }
        else if (wParam == NativeMethods.WM_LBUTTONUP || wParam == NativeMethods.WM_LBUTTONDOWN)
        {
            // Clear synchronously so the next click is routed normally. A second
            // press (instead of a release) means the release was missed.
            var wasDragging = _dragging;
            var openPanel = wParam == NativeMethods.WM_LBUTTONUP;
            ClearGesture();
            Application.Current.Dispatcher.BeginInvoke(() => FinishGesture(wasDragging, openPanel));
        }
    }

    private void ApplyDragMove()
    {
        _dragMoveQueued = false;
        if (!_dragging) return;
        PlaceIndicatorPhysical(_dragLatestPt.X - _dragGrabOffsetPxX, _dragLatestPt.Y - _dragGrabOffsetPxY);
    }

    private void BeginSessionDrag(NativeMethods.POINT pt)
    {
        if (!_dragPending && !_dragging) return;
        RevealTab();
        _tabHideTimer?.Stop();
        _tabHideScheduled = false;
        CacheIndicatorHwnd();

        if (_indicatorHwnd != 0 && NativeMethods.GetWindowRect(_indicatorHwnd, out var rect) && rect.Right > rect.Left)
        {
            _dragGrabOffsetPxX = pt.X - rect.Left;
            _dragGrabOffsetPxY = pt.Y - rect.Top;
            _pixelsPerDip = (rect.Right - rect.Left) / IndicatorWidth;
            if (_pixelsPerDip <= 0) _pixelsPerDip = 1;
            RefreshVirtualBoundsPx();
        }
        else
        {
            var scale = _pixelsPerDip > 0 ? _pixelsPerDip : 1;
            _dragGrabOffsetPxX = (int)Math.Round(pt.X - _indicatorLeftDip * scale);
            _dragGrabOffsetPxY = (int)Math.Round(pt.Y - _indicatorTopDip * scale);
        }

        // Safety net if the release is never delivered (hook skipped, focus
        // steal): drop a gesture that has seen no mouse input for a while.
        _dragWatchdog ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _dragWatchdog.Tick -= DragWatchdogTick;
        _dragWatchdog.Tick += DragWatchdogTick;
        _dragWatchdog.Start();
    }

    private void DragWatchdogTick(object? sender, EventArgs e)
    {
        if (!_dragPending && !_dragging) { _dragWatchdog?.Stop(); return; }
        if (DateTime.UtcNow - _lastGestureEventUtc > DragWatchdogIdle)
            EndSessionDrag(openPanelOnClick: false);
    }

    private void EndSessionDrag(bool openPanelOnClick)
    {
        if (!_dragPending && !_dragging) return;
        var wasDragging = _dragging;
        ClearGesture();
        FinishGesture(wasDragging, openPanelOnClick);
    }

    private void ClearGesture()
    {
        _dragPending = false;
        _dragging = false;
        _dragWatchdog?.Stop();
    }

    private void FinishGesture(bool wasDragging, bool openPanelOnClick)
    {
        _dragMoveQueued = false;
        if (wasDragging)
        {
            PlaceIndicatorPhysical(_dragLatestPt.X - _dragGrabOffsetPxX, _dragLatestPt.Y - _dragGrabOffsetPxY);
            SyncIndicatorFromHwnd();
            return;
        }
        if (openPanelOnClick) SetExpanded(true);
    }

    private static bool IsPointerAction(nint wParam) =>
        wParam == NativeMethods.WM_LBUTTONDOWN ||
        wParam == NativeMethods.WM_LBUTTONUP ||
        wParam == NativeMethods.WM_RBUTTONUP ||
        wParam == NativeMethods.WM_RBUTTONDOWN ||
        wParam == NativeMethods.WM_MBUTTONDOWN ||
        wParam == NativeMethods.WM_MBUTTONUP ||
        wParam == NativeMethods.WM_MOUSEWHEEL ||
        wParam == NativeMethods.WM_MOUSEHWHEEL;

    private static bool PointInWindow(System.Windows.Window window, NativeMethods.POINT pt, int pad)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || !window.IsVisible) return false;
        if (!NativeMethods.GetWindowRect(hwnd, out var r)) return false;
        return pt.X >= r.Left - pad && pt.X <= r.Right + pad && pt.Y >= r.Top - pad && pt.Y <= r.Bottom + pad;
    }

    private void ShowSettingsMenu(NativeMethods.POINT pt)
    {
        RevealTab();
        _settingsMenuOpen = true;
        var ownerHwnd = new WindowInteropHelper(_indicatorWindow).Handle;
        var hMenu = NativeMethods.CreatePopupMenu();
        var hPets = NativeMethods.CreatePopupMenu();
        for (var i = 0; i < PetConfig.PetIds.Length; i++)
        {
            var id = PetConfig.PetIds[i];
            var flags = NativeMethods.MF_STRING |
                (string.Equals(id, PetConfig.CurrentPetId, StringComparison.OrdinalIgnoreCase)
                    ? NativeMethods.MF_CHECKED : 0);
            NativeMethods.AppendMenu(hPets, flags, 2000 + i, id);
        }
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_POPUP, hPets, "Codex pet");
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, 0, string.Empty);
        if (_hasSessionPosition)
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, 1002, "Reset position");
        NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, 1001, "Quit Agent Notch");
        NativeMethods.SetForegroundWindow(ownerHwnd);
        int cmd;
        try
        {
            cmd = NativeMethods.TrackPopupMenuEx(hMenu, NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON,
                pt.X, pt.Y, ownerHwnd, 0);
        }
        finally
        {
            NativeMethods.DestroyMenu(hMenu);
            _settingsMenuOpen = false;
        }
        if (cmd == 1001)
        {
            Application.Current.Shutdown();
            return;
        }
        if (cmd == 1002)
        {
            PositionIndicator();
            return;
        }
        var petIndex = cmd - 2000;
        if (petIndex >= 0 && petIndex < PetConfig.PetIds.Length)
            PetConfig.SetCurrentPet(PetConfig.PetIds[petIndex]);
    }

    private void InstallKeyboardHook()
    {
        _keyboardProc = KeyboardHookCallback;
        using var proc = Process.GetCurrentProcess();
        using var module = proc.MainModule!;
        _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardProc,
            NativeMethods.GetModuleHandle(module.ModuleName), 0);
    }

    /// <summary>Esc tucks the panel or the small hover tab so windows behind
    /// are reachable. Neither overlay activates, so a WPF KeyDown would never
    /// fire — same reason expand/collapse uses a mouse hook.</summary>
    private nint KeyboardHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && !_settingsMenuOpen
            && (wParam == NativeMethods.WM_KEYDOWN || wParam == NativeMethods.WM_SYSKEYDOWN)
            && (_expanded || _tabRevealed || _dock.IsOpen))
        {
            var data = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            if (data.vkCode == NativeMethods.VK_ESCAPE && (_dragPending || _dragging))
            {
                // Esc mid-gesture just drops it; the tab stays where it is.
                Application.Current.Dispatcher.BeginInvoke(() => EndSessionDrag(openPanelOnClick: false));
                return 1;
            }
            if (data.vkCode == NativeMethods.VK_ESCAPE)
            {
                // Set the latch on the hook thread first so a concurrent mouse
                // move can't queue a RevealTab that races the UI dismiss.
                _tabEscDismissed = true;
                Application.Current.Dispatcher.BeginInvoke(DismissWithEscape);
                return 1; // eat Esc so the app underneath doesn't also handle it
            }
            if (data.vkCode == NativeMethods.VK_ESCAPE && _dock.IsOpen)
            {
                Application.Current.Dispatcher.BeginInvoke(() => _dock.HandleEscape());
                return 1;
            }
        }
        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private void DismissWithEscape()
    {
        _tabEscDismissed = true;
        EndSessionDrag(openPanelOnClick: false);
        if (_dock.IsOpen) _dock.HandleEscape();
        if (_expanded) SetExpanded(false);
        else SetTabRevealed(false);
    }

    private void InstallForegroundHook()
    {
        _winEventProc = OnForegroundChanged;
        _winEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            0, _winEventProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
    }

    /// <summary>Revisiting a terminal acknowledges finished agents — green clears.</summary>
    private void OnForegroundChanged(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        Dock.ForegroundCapture.NoteForeground(hwnd);
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            using var process = Process.GetProcessById(pid);
            if (!PetConfig.KnownHostProcessNames.Contains(process.ProcessName)) return;

            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                var changed = false;
                if (_claudeState == AgentGlyphState.Done) { _claudeState = AgentGlyphState.Inactive; changed = true; }
                if (_codexState == AgentGlyphState.Done) { _codexState = AgentGlyphState.Inactive; changed = true; }
                if (_cursorState == AgentGlyphState.Done) { _cursorState = AgentGlyphState.Inactive; changed = true; }
                if (!changed) return;
                _indicatorWindow.Canvas.ClaudeState = _claudeState;
                _indicatorWindow.Canvas.CodexState = _codexState;
                _indicatorWindow.Canvas.CursorState = _cursorState;
                _indicatorWindow.Render();
            });
        }
        catch (ArgumentException)
        {
            // process already exited between the event firing and our lookup
        }
    }

    public void Shutdown()
    {
        _dock.Shutdown();
        if (_mouseHook != 0) NativeMethods.UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != 0) NativeMethods.UnhookWindowsHookEx(_keyboardHook);
        if (_winEventHook != 0) NativeMethods.UnhookWinEvent(_winEventHook);
    }
}
