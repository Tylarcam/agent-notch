using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AgentNotch.Models;
using AgentNotch.Relay;

namespace AgentNotch.Dock;

internal sealed class DockChatWindow : DockOverlayWindow
{
    private readonly Border _chrome;
    private readonly Grid _root = new();
    private readonly StackPanel _header = new() { Height = DockTokens.HeaderH, Orientation = Orientation.Horizontal };
    private readonly TextBlock _title = new();
    private readonly TextBlock _relayBadge = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel _messages = new();
    private readonly Grid _composer = new() { Height = DockTokens.ComposerH, Margin = new Thickness(DockTokens.ComposerInset) };
    private readonly TextBox _input = new();
    private readonly Button _sendOrMic = new();
    private readonly Button _plus = new();

    private DockRailItem? _active;
    private IOdysseusRelay? _relay;
    private string? _streamAcc;
    private FrameworkElement? _typingRow;
    private ApprovalCardState _approvalState = ApprovalCardState.Pending;
    private Border? _approvalCard;

    public event Action? CloseRequested;
    public event Action<AgentSession, char>? ApprovalPressed;

    public DockChatWindow()
    {
        _chrome = DockGlass.Panel(DockTokens.ChatRadius);
        Width = DockTokens.ChatW;
        Height = DockTokens.ChatH;
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        BuildHeader();
        _scroll.Content = _messages;
        Grid.SetRow(_scroll, 1);
        BuildComposer();

        _root.Children.Add(_header);
        _root.Children.Add(_scroll);
        _root.Children.Add(_composer);
        _chrome.Child = _root;
        Content = _chrome;
        Opacity = 0;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CloseRequested?.Invoke(); e.Handled = true; }
        };
    }

    private void BuildHeader()
    {
        _header.Margin = new Thickness(DockTokens.MessagePadX, 0, DockTokens.MessagePadX, 0);
        _title.FontFamily = DockFonts.Family;
        _title.FontSize = 13;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = new SolidColorBrush(DockTokens.Teal);
        _title.VerticalAlignment = VerticalAlignment.Center;
        _relayBadge.FontFamily = DockFonts.Family;
        _relayBadge.FontSize = 11;
        _relayBadge.Foreground = new SolidColorBrush(DockTokens.Muted);
        _relayBadge.VerticalAlignment = VerticalAlignment.Center;
        _relayBadge.Margin = new Thickness(8, 0, 0, 0);
        var spacer = new FrameworkElement { Width = 1 };
        var gear = HeaderIcon("⚙");
        var share = HeaderIcon("Share");
        gear.Click += (_, _) => { /* coming soon */ };
        share.Click += (_, _) => { /* coming soon */ };
        _header.Children.Add(_title);
        _header.Children.Add(_relayBadge);
        _header.Children.Add(spacer);
        _header.Children.Add(gear);
        _header.Children.Add(share);
    }

    private static Button HeaderIcon(string label) => new()
    {
        Content = label,
        MinWidth = 44,
        MinHeight = 44,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Foreground = new SolidColorBrush(DockTokens.Muted2),
        FontFamily = DockFonts.Family,
        FontSize = 12,
        Cursor = Cursors.Hand,
    };

    private void BuildComposer()
    {
        Grid.SetRow(_composer, 2);
        _composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _composer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _plus.Content = "+";
        _plus.Width = 32;
        _plus.Height = 32;
        _plus.FontFamily = DockFonts.Family;
        _plus.Foreground = new SolidColorBrush(DockTokens.Yellow);
        _plus.Background = new SolidColorBrush(Color.FromArgb(26, DockTokens.Yellow.R, DockTokens.Yellow.G, DockTokens.Yellow.B));
        _plus.BorderBrush = new SolidColorBrush(Color.FromArgb(90, DockTokens.Yellow.R, DockTokens.Yellow.G, DockTokens.Yellow.B));
        _plus.BorderThickness = new Thickness(1);
        _plus.Cursor = Cursors.Hand;

        var pill = new Border
        {
            Background = new SolidColorBrush(DockTokens.GlassBase),
            BorderBrush = new SolidColorBrush(DockTokens.GlassBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(DockTokens.ComposerRadius),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(8, 0, 8, 0),
        };
        _input.Background = Brushes.Transparent;
        _input.BorderThickness = new Thickness(0);
        _input.Foreground = new SolidColorBrush(DockTokens.Text);
        _input.FontFamily = DockFonts.Family;
        _input.FontSize = 13;
        _input.VerticalContentAlignment = VerticalAlignment.Center;
        _input.TextChanged += (_, _) => UpdateSendMic();
        pill.Child = _input;

        _sendOrMic.Width = 36;
        _sendOrMic.Height = 36;
        _sendOrMic.FontFamily = DockFonts.Family;
        _sendOrMic.Cursor = Cursors.Hand;
        _sendOrMic.Click += (_, _) => _ = SendAsync();
        UpdateSendMic();

        Grid.SetColumn(_plus, 0);
        Grid.SetColumn(pill, 1);
        Grid.SetColumn(_sendOrMic, 2);
        _composer.Children.Add(_plus);
        _composer.Children.Add(pill);
        _composer.Children.Add(_sendOrMic);
    }

    private void UpdateSendMic()
    {
        var t = _input.Text;
        var hasText = !string.IsNullOrWhiteSpace(t) && (_input.Tag is not string ph || t != ph);
        if (hasText)
        {
            _sendOrMic.Content = "➤";
            _sendOrMic.Foreground = new SolidColorBrush(DockTokens.Bg);
            _sendOrMic.Background = new SolidColorBrush(DockTokens.Teal);
            _sendOrMic.BorderThickness = new Thickness(0);
        }
        else
        {
            _sendOrMic.Content = "🎤";
            _sendOrMic.Foreground = new SolidColorBrush(DockTokens.MicIcon);
            _sendOrMic.Background = Brushes.Transparent;
            _sendOrMic.BorderThickness = new Thickness(0);
        }
    }

    public void Bind(DockRailItem item, IOdysseusRelay relay)
    {
        if (_relay is not null)
        {
            _relay.TranscriptChanged -= OnTranscriptChanged;
            _relay.TokenReceived -= OnToken;
        }
        _active = item;
        _relay = relay;
        _title.Text = item.Label;
        var newPlaceholder = $"Message {item.Label}";
        var previousPlaceholder = _input.Tag as string;
        _input.Tag = newPlaceholder;
        if (string.IsNullOrEmpty(_input.Text) || _input.Text == previousPlaceholder)
        {
            _input.Foreground = new SolidColorBrush(DockTokens.Muted);
            _input.Text = newPlaceholder;
        }
        UpdateSendMic();
        _input.GotFocus -= Input_Focus;
        _input.LostFocus -= Input_Blur;
        _input.GotFocus += Input_Focus;
        _input.LostFocus += Input_Blur;
        _relayBadge.Text = relay.StatusText;
        relay.TranscriptChanged += OnTranscriptChanged;
        relay.TokenReceived += OnToken;
        RebuildMessages();
        UpdateApprovalCard(item.Session?.PendingApproval, item.Label);
    }

    private void Input_Focus(object sender, RoutedEventArgs e)
    {
        if (_input.Tag is string ph && _input.Text == ph)
        {
            _input.Text = "";
            _input.Foreground = new SolidColorBrush(DockTokens.Text);
        }
    }

    private void Input_Blur(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_input.Text) && _input.Tag is string ph)
        {
            _input.Text = ph;
            _input.Foreground = new SolidColorBrush(DockTokens.Muted);
        }
    }

    public void SetSlideProgress(double t) => Opacity = Math.Clamp(t, 0, 1);

    private void OnTranscriptChanged() =>
        Application.Current.Dispatcher.Invoke(RebuildMessages);

    private void OnToken(string chunk)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _streamAcc ??= "";
            _streamAcc += chunk;
            if (_typingRow is TextBlock tb)
                tb.Text = _streamAcc;
        });
    }

    private async Task SendAsync()
    {
        var raw = _input.Text.Trim();
        if (_input.Tag is string ph && raw == ph) return;
        var text = raw;
        if (string.IsNullOrEmpty(text) || _relay is null) return;
        _input.Text = "";
        UpdateSendMic();
        var bubble = UserBubble(text);
        _messages.Children.Add(bubble);
        var pop = new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(DockTokens.SendPopMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        bubble.BeginAnimation(OpacityProperty, pop);
        ScrollBottom();

        _streamAcc = null;
        _typingRow = AssistantLine("Typing…");
        _messages.Children.Add(_typingRow);
        ScrollBottom();

        try
        {
            await _relay.SendUserMessageAsync(text);
        }
        finally
        {
            if (_typingRow is not null)
            {
                _messages.Children.Remove(_typingRow);
                _typingRow = null;
            }
            RebuildMessages();
        }
    }

    private void RebuildMessages()
    {
        if (_relay is null) return;
        _messages.Children.Clear();
        foreach (var m in _relay.Transcript)
        {
            if (m.Role == "user") _messages.Children.Add(UserBubble(m.Text));
            else _messages.Children.Add(AssistantBlock(m.Text));
        }
        if (_active?.Session?.PendingApproval is { } req)
        {
            _approvalCard = BuildApprovalCard(req, _active.Label);
            _messages.Children.Add(_approvalCard);
        }
        ScrollBottom();
    }

    private static Border UserBubble(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = DockFonts.Family,
            FontSize = DockTokens.ComposerH > 0 ? 13.5 : 13,
            Foreground = new SolidColorBrush(DockTokens.Text),
            MaxWidth = DockTokens.ChatW * 0.78 - DockTokens.MessagePadX * 2,
        };
        return new Border
        {
            Background = new SolidColorBrush(DockTokens.UserBubble),
            CornerRadius = new CornerRadius(18, 18, 6, 18),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 4, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = tb,
        };
    }

    private static FrameworkElement AssistantBlock(string text)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rule = new Border { Background = new SolidColorBrush(Color.FromArgb(140, DockTokens.Teal.R, DockTokens.Teal.G, DockTokens.Teal.B)) };
        var tb = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = DockFonts.Family,
            FontSize = 13.5,
            Foreground = new SolidColorBrush(DockTokens.Text),
            Margin = new Thickness(10, 0, 0, 0),
        };
        Grid.SetColumn(rule, 0);
        Grid.SetColumn(tb, 1);
        grid.Children.Add(rule);
        grid.Children.Add(tb);
        return grid;
    }

    private static TextBlock AssistantLine(string text) => new()
    {
        Text = text,
        FontFamily = DockFonts.Family,
        FontSize = 13.5,
        Foreground = new SolidColorBrush(DockTokens.Muted2),
        Margin = new Thickness(DockTokens.MessagePadX + 12, 4, DockTokens.MessagePadX, 4),
    };

    public void UpdateApprovalCard(ApprovalRequest? req, string agentName)
    {
        if (_approvalCard is not null)
        {
            _messages.Children.Remove(_approvalCard);
            _approvalCard = null;
        }
        if (req is null) return;
        _approvalState = ApprovalCardState.Pending;
        _approvalCard = BuildApprovalCard(req, agentName);
        _messages.Children.Add(_approvalCard);
        ScrollBottom();
    }

    public void SetApprovalResolved(ApprovalCardState state)
    {
        _approvalState = state;
        if (_approvalCard is null) return;
        var edge = state switch
        {
            ApprovalCardState.Approved => DockTokens.Teal,
            ApprovalCardState.Denied => DockTokens.Deny,
            _ => DockTokens.Yellow,
        };
        _approvalCard.BorderBrush = new SolidColorBrush(edge);
    }

    private Border BuildApprovalCard(ApprovalRequest req, string agentName)
    {
        var edge = _approvalState switch
        {
            ApprovalCardState.Approved => DockTokens.Teal,
            ApprovalCardState.Denied => DockTokens.Deny,
            _ => DockTokens.Yellow,
        };
        var stack = new StackPanel { Margin = new Thickness(12) };
        stack.Children.Add(Meta(_approvalState switch
        {
            ApprovalCardState.Approved => "Approved",
            ApprovalCardState.Denied => "Denied",
            _ => "Pending · needs you",
        }));
        stack.Children.Add(new TextBlock
        {
            Text = ApprovalPayload.Title(req, agentName),
            FontFamily = DockFonts.Family,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(DockTokens.Text),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 4),
        });
        stack.Children.Add(new TextBlock
        {
            Text = ApprovalPayload.Describe(req),
            FontFamily = DockFonts.Family,
            FontSize = 12,
            Foreground = new SolidColorBrush(DockTokens.Muted2),
            TextWrapping = TextWrapping.Wrap,
        });

        if (_approvalState == ApprovalCardState.Pending && _active?.Session is { } session)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            row.Children.Add(ApprovalBtn("Deny", false, session, '3'));
            row.Children.Add(ApprovalBtn("Approve", true, session, '1'));
            stack.Children.Add(row);
        }

        var card = new Border
        {
            Background = new SolidColorBrush(DockTokens.GlassBase),
            BorderBrush = new SolidColorBrush(edge),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(DockTokens.MessagePadX, 8, DockTokens.MessagePadX, 8),
            Child = stack,
        };
        return card;
    }

    private static TextBlock Meta(string t) => new()
    {
        Text = t,
        FontFamily = DockFonts.Family,
        FontSize = 11,
        Foreground = new SolidColorBrush(DockTokens.Muted),
    };

    private Button ApprovalBtn(string label, bool primary, AgentSession session, char digit)
    {
        var btn = new Button
        {
            Content = label,
            MinWidth = 44,
            Height = 36,
            Margin = new Thickness(0, 0, 8, 0),
            FontFamily = DockFonts.Family,
            FontSize = 12,
            Cursor = Cursors.Hand,
        };
        if (primary)
        {
            btn.Background = new SolidColorBrush(DockTokens.Teal);
            btn.Foreground = new SolidColorBrush(DockTokens.Bg);
            btn.BorderThickness = new Thickness(0);
        }
        else
        {
            btn.Background = Brushes.Transparent;
            btn.Foreground = new SolidColorBrush(DockTokens.Deny);
            btn.BorderBrush = new SolidColorBrush(DockTokens.Deny);
            btn.BorderThickness = new Thickness(1);
        }
        btn.Click += (_, _) =>
        {
            ApprovalPressed?.Invoke(session, digit);
            SetApprovalResolved(primary ? ApprovalCardState.Approved : ApprovalCardState.Denied);
        };
        return btn;
    }

    private void ScrollBottom() => _scroll.ScrollToEnd();

    public UIElement RootForCapture => _chrome;

    public void InjectPreviewContent(IEnumerable<string> assistantLines, string? userLine = null)
    {
        if (!string.IsNullOrEmpty(userLine))
            _messages.Children.Add(UserBubble(userLine));
        foreach (var line in assistantLines)
            _messages.Children.Add(AssistantBlock(line));
        if (_typingRow is null)
        {
            _typingRow = AssistantLine("Typing...");
            _messages.Children.Add(_typingRow);
        }
        ScrollBottom();
    }
}
