namespace AgentNotch.Relay;

internal interface IOdysseusRelay
{
    bool IsConnected { get; }
    string StatusText { get; }
    IReadOnlyList<ChatMessage> Transcript { get; }
    event Action? TranscriptChanged;
    event Action<string>? TokenReceived;
    Task SendUserMessageAsync(string text, CancellationToken ct = default);
    void ClearTranscript();
}

internal sealed record ChatMessage(string Role, string Text, DateTime At);
