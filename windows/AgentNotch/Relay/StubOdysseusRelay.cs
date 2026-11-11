namespace AgentNotch.Relay;

/// <summary>In-memory transcript; fake stream only in preview/demo mode.</summary>
internal sealed class StubOdysseusRelay : IOdysseusRelay
{
    private readonly bool _demoStream;
    private readonly List<ChatMessage> _transcript = [];
    private readonly object _gate = new();

    public bool IsConnected => false;
    public string StatusText => "relay offline";
    public IReadOnlyList<ChatMessage> Transcript { get { lock (_gate) return _transcript.ToList(); } }
    public event Action? TranscriptChanged;
    public event Action<string>? TokenReceived;

    public StubOdysseusRelay(bool demoStream) => _demoStream = demoStream;

    public async Task SendUserMessageAsync(string text, CancellationToken ct = default)
    {
        lock (_gate) _transcript.Add(new ChatMessage("user", text, DateTime.UtcNow));
        TranscriptChanged?.Invoke();

        if (!_demoStream)
        {
            lock (_gate) _transcript.Add(new ChatMessage("assistant",
                "Relay is offline. Point relay.json at Odysseus /api/chat_stream when ready.",
                DateTime.UtcNow));
            TranscriptChanged?.Invoke();
            return;
        }

        await Task.Delay(400, ct).ConfigureAwait(false);
        const string reply = "Analyzing your request — here's a short streamed reply for the dock preview.";
        foreach (var word in reply.Split(' '))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(80, ct).ConfigureAwait(false);
            TokenReceived?.Invoke(word + " ");
        }
        lock (_gate) _transcript.Add(new ChatMessage("assistant", reply, DateTime.UtcNow));
        TranscriptChanged?.Invoke();
    }

    public void ClearTranscript()
    {
        lock (_gate) _transcript.Clear();
        TranscriptChanged?.Invoke();
    }

    public void SeedDemo(IEnumerable<ChatMessage> msgs)
    {
        lock (_gate)
        {
            _transcript.Clear();
            _transcript.AddRange(msgs);
        }
        TranscriptChanged?.Invoke();
    }
}
