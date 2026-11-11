using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AgentNotch.Relay;

/// <summary>HTTP client for Odysseus <c>POST /api/chat_stream</c> when a base URL is configured.</summary>
internal sealed class OdysseusRelayClient : IOdysseusRelay, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly List<ChatMessage> _transcript = [];
    private readonly object _gate = new();

    public bool IsConnected { get; private set; }
    public string StatusText { get; private set; } = "relay offline";
    public IReadOnlyList<ChatMessage> Transcript { get { lock (_gate) return _transcript.ToList(); } }
    public event Action? TranscriptChanged;
    public event Action<string>? TokenReceived;

    public OdysseusRelayClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _ = ProbeAsync();
    }

    private async Task ProbeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var resp = await _http.GetAsync($"{_baseUrl}/api/health", cts.Token).ConfigureAwait(false);
            IsConnected = resp.IsSuccessStatusCode;
            StatusText = IsConnected ? "relay connected" : "relay unreachable";
        }
        catch
        {
            IsConnected = false;
            StatusText = "relay offline";
        }
    }

    public async Task SendUserMessageAsync(string text, CancellationToken ct = default)
    {
        lock (_gate) _transcript.Add(new ChatMessage("user", text, DateTime.UtcNow));
        TranscriptChanged?.Invoke();

        if (!IsConnected)
        {
            lock (_gate) _transcript.Add(new ChatMessage("assistant",
                "Relay is offline — message kept locally. Configure %USERPROFILE%\\.config\\agent-notch\\relay.json when Odysseus is up.",
                DateTime.UtcNow));
            TranscriptChanged?.Invoke();
            return;
        }

        var body = JsonSerializer.Serialize(new { message = text, stream = true });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        try
        {
            using var resp = await _http.PostAsync($"{_baseUrl}/api/chat_stream", content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                AppendAssistant($"Relay error ({(int)resp.StatusCode}).");
                return;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var acc = new StringBuilder();
            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    var chunk = line["data:".Length..].Trim();
                    if (chunk is "done" or "[DONE]") break;
                    acc.Append(chunk);
                    TokenReceived?.Invoke(chunk);
                }
                else acc.Append(line);
            }
            if (acc.Length > 0) AppendAssistant(acc.ToString());
        }
        catch (Exception ex)
        {
            AppendAssistant($"Relay failed: {ex.Message}");
        }
    }

    private void AppendAssistant(string text)
    {
        lock (_gate) _transcript.Add(new ChatMessage("assistant", text, DateTime.UtcNow));
        TranscriptChanged?.Invoke();
    }

    public void ClearTranscript()
    {
        lock (_gate) _transcript.Clear();
        TranscriptChanged?.Invoke();
    }

    public void Dispose() => _http.Dispose();
}
