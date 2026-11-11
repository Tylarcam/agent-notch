using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AgentNotch.Relay;

/// <summary>Authenticated Odysseus client: session, chat_stream SSE, and upload.</summary>
internal sealed class OdysseusRelayClient : IOdysseusRelay, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string? _token;
    private readonly List<ChatMessage> _transcript = [];
    private readonly object _gate = new();
    private string? _sessionId;
    private bool _sessionReady;

    public bool IsConnected { get; private set; }
    public string StatusText { get; private set; } = "relay offline";
    public IReadOnlyList<ChatMessage> Transcript { get { lock (_gate) return _transcript.ToList(); } }
    public event Action? TranscriptChanged;
    public event Action<string>? TokenReceived;

    public OdysseusRelayClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _token = RelayConfig.TryGetApiToken();
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _ = ProbeAsync();
    }

    private async Task ProbeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var resp = await _http.GetAsync($"{_baseUrl}/api/health", cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                IsConnected = false;
                StatusText = $"relay unreachable ({(int)resp.StatusCode})";
                return;
            }
            if (_token is null)
            {
                IsConnected = false;
                StatusText = "relay needs token";
                return;
            }
            IsConnected = true;
            StatusText = "relay connected";
            _sessionId = RelayConfig.TryGetStoredSessionId();
        }
        catch
        {
            IsConnected = false;
            StatusText = "relay offline";
        }
    }

    public async Task SendUserMessageAsync(string text, IReadOnlyList<string>? attachmentIds = null, CancellationToken ct = default)
    {
        var hasAtts = attachmentIds is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(text) && !hasAtts) return;

        lock (_gate) _transcript.Add(new ChatMessage("user", text, DateTime.UtcNow));
        TranscriptChanged?.Invoke();

        if (_token is null)
        {
            AppendAssistant("Relay needs ODYSSEUS_API_TOKEN in the environment.");
            return;
        }
        if (!IsConnected)
        {
            await ProbeAsync().ConfigureAwait(false);
            if (!IsConnected)
            {
                AppendAssistant("Relay is offline — message kept locally.");
                return;
            }
        }

        try
        {
            var session = await EnsureSessionAsync(ct).ConfigureAwait(false);
            if (session is null)
            {
                AppendAssistant("Relay error (session).");
                return;
            }

            var payload = new Dictionary<string, object?>
            {
                ["message"] = text,
                ["session"] = session,
                ["mode"] = "chat",
            };
            if (hasAtts) payload["attachments"] = attachmentIds!.ToList();

            var status = await StreamChatAsync(payload, ct).ConfigureAwait(false);
            if (status == 400 && _sessionReady)
            {
                _sessionId = null;
                _sessionReady = false;
                RelayConfig.ClearStoredSessionId();
                session = await EnsureSessionAsync(ct).ConfigureAwait(false);
                if (session is null)
                {
                    AppendAssistant("Relay error (session).");
                    return;
                }
                payload["session"] = session;
                status = await StreamChatAsync(payload, ct).ConfigureAwait(false);
            }
            if (status is not null)
                AppendAssistant($"Relay error ({status}).");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppendAssistant($"Relay failed: {ex.Message}");
        }
    }

    public async Task<string?> UploadFileAsync(string fileName, byte[] data, string mimeType, CancellationToken ct = default)
    {
        if (_token is null || data.Length == 0) return null;
        if (!IsConnected) await ProbeAsync().ConfigureAwait(false);
        if (!IsConnected) return null;

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(data);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        form.Add(fileContent, "files", fileName);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/upload") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (doc.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in files.EnumerateArray())
            {
                if (f.TryGetProperty("id", out var idEl))
                {
                    var id = idEl.GetString();
                    if (!string.IsNullOrWhiteSpace(id)) return id;
                }
            }
        }
        return null;
    }

    private async Task<string?> EnsureSessionAsync(CancellationToken ct)
    {
        if (_sessionReady && !string.IsNullOrEmpty(_sessionId)) return _sessionId;

        _sessionId ??= RelayConfig.TryGetStoredSessionId();
        if (!string.IsNullOrEmpty(_sessionId))
        {
            _sessionReady = true;
            return _sessionId;
        }

        using var dcReq = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/default-chat");
        dcReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var dcResp = await _http.SendAsync(dcReq, ct).ConfigureAwait(false);
        if (!dcResp.IsSuccessStatusCode) return null;

        await using var dcStream = await dcResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var dcDoc = await JsonDocument.ParseAsync(dcStream, cancellationToken: ct).ConfigureAwait(false);
        var root = dcDoc.RootElement;
        var endpointId = root.TryGetProperty("endpoint_id", out var eid) ? eid.GetString() ?? "" : "";
        var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(endpointId) || string.IsNullOrWhiteSpace(model))
        {
            var picked = await PickModelFromCatalogAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(endpointId)) endpointId = picked.endpointId;
            if (string.IsNullOrWhiteSpace(model)) model = picked.model;
        }

        using var form = new MultipartFormDataContent
        {
            { new StringContent("Agent Notch"), "name" },
            { new StringContent(endpointId), "endpoint_id" },
            { new StringContent(model), "model" },
            { new StringContent("true"), "skip_validation" },
        };
        using var sessReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/session") { Content = form };
        sessReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var sessResp = await _http.SendAsync(sessReq, ct).ConfigureAwait(false);
        if (!sessResp.IsSuccessStatusCode) return null;

        await using var sessStream = await sessResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var sessDoc = await JsonDocument.ParseAsync(sessStream, cancellationToken: ct).ConfigureAwait(false);
        if (!sessDoc.RootElement.TryGetProperty("id", out var idEl)) return null;
        var newId = idEl.GetString();
        if (string.IsNullOrWhiteSpace(newId)) return null;

        _sessionId = newId;
        _sessionReady = true;
        RelayConfig.PersistSessionId(newId);
        return newId;
    }

    private async Task<(string endpointId, string model)> PickModelFromCatalogAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return ("", "");
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return ("", "");
            foreach (var item in items.EnumerateArray())
            {
                var eid = item.TryGetProperty("endpoint_id", out var ep) ? ep.GetString() ?? "" : "";
                if (!item.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) continue;
                foreach (var m in models.EnumerateArray())
                {
                    var name = m.GetString();
                    if (!string.IsNullOrWhiteSpace(eid) && !string.IsNullOrWhiteSpace(name))
                        return (eid, name);
                }
            }
        }
        catch { /* ignore */ }
        return ("", "");
    }

    private async Task<int?> StreamChatAsync(Dictionary<string, object?> payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        async Task<HttpResponseMessage> PostJsonAsync()
        {
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat_stream") { Content = content };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }

        using var resp = await PostJsonAsync().ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            resp.Dispose();
            using var formResp = await PostMultipartChatAsync(payload, ct).ConfigureAwait(false);
            return await ReadSseAsync(formResp, ct).ConfigureAwait(false);
        }
        return await ReadSseAsync(resp, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> PostMultipartChatAsync(Dictionary<string, object?> payload, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(payload["message"]?.ToString() ?? ""), "message");
        form.Add(new StringContent(payload["session"]?.ToString() ?? ""), "session");
        form.Add(new StringContent("chat"), "mode");
        if (payload.TryGetValue("attachments", out var atts) && atts is List<string> ids)
            form.Add(new StringContent(JsonSerializer.Serialize(ids)), "attachments");

        var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat_stream") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private async Task<int?> ReadSseAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        using (resp)
        {
        if (!resp.IsSuccessStatusCode) return (int)resp.StatusCode;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var acc = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (line.Length == 0) continue;
            if (line[0] == ':') continue; // SSE heartbeat / comment
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line["data:".Length..].Trim();
            if (payload is "[DONE]" or "done") break;

            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.String)
                {
                    var piece = delta.GetString();
                    if (!string.IsNullOrEmpty(piece))
                    {
                        acc.Append(piece);
                        TokenReceived?.Invoke(piece);
                    }
                }
            }
            catch (JsonException)
            {
                // non-JSON data lines — ignore
            }
        }
        if (acc.Length > 0) AppendAssistant(acc.ToString());
        return null;
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
