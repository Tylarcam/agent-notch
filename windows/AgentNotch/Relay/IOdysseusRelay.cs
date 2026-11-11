namespace AgentNotch.Relay;

internal interface IOdysseusRelay
{
    bool IsConnected { get; }
    string StatusText { get; }
    IReadOnlyList<ChatMessage> Transcript { get; }
    event Action? TranscriptChanged;
    event Action<string>? TokenReceived;

    Task SendUserMessageAsync(string text, IReadOnlyList<string>? attachmentIds = null, CancellationToken ct = default);

  /// <summary>Upload bytes to Odysseus; returns file id or null.</summary>
    Task<string?> UploadFileAsync(string fileName, byte[] data, string mimeType, CancellationToken ct = default);

    void ClearTranscript();
}

internal sealed record ChatMessage(string Role, string Text, DateTime At);
