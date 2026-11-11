using System.IO;
using System.Net.Http;
using System.Text.Json;
using AgentNotch.Relay;

namespace AgentNotch.Aether;

internal sealed class AetherClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly string _baseUrl;

    public AetherClient() => _baseUrl = RelayConfig.TryGetAetherBaseUrl();

    public async Task<(bool ok, string text)> TranscribeFileAsync(string wavPath, CancellationToken ct = default)
    {
        if (!await HealthOkAsync(ct).ConfigureAwait(false))
            return (false, "Aether offline");

        await using var fs = File.OpenRead(wavPath);
        var groq = await PostTranscribeAsync($"{_baseUrl}/api/transcribe-groq", fs, Path.GetFileName(wavPath), ct).ConfigureAwait(false);
        if (groq.ok) return groq;

        fs.Position = 0;
        return await PostTranscribeAsync($"{_baseUrl}/api/transcribe-whisper", fs, Path.GetFileName(wavPath), ct, extraFields: [("model", "tiny")]).ConfigureAwait(false);
    }

    private async Task<bool> HealthOkAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var resp = await _http.GetAsync($"{_baseUrl}/health", cts.Token).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<(bool ok, string text)> PostTranscribeAsync(
        string url,
        Stream file,
        string fileName,
        CancellationToken ct,
        IReadOnlyList<(string, string)>? extraFields = null)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StreamContent(file), "file", fileName);
            if (extraFields is not null)
                foreach (var (k, v) in extraFields)
                    form.Add(new StringContent(v), k);

            using var resp = await _http.PostAsync(url, form, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                        return (true, t.GetString() ?? "");
                }
                catch { /* fall through */ }
                return (false, "Bad transcribe response");
            }
            var err = TryErrorDetail(body);
            return (false, string.IsNullOrWhiteSpace(err) ? $"Transcribe failed ({(int)resp.StatusCode})" : TrimErr(err));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string? TryErrorDetail(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                return e.GetString();
            if (doc.RootElement.TryGetProperty("detail", out var d))
                return d.ValueKind == JsonValueKind.String ? d.GetString() : d.GetRawText();
        }
        catch { /* ignore */ }
        return null;
    }

    private static string TrimErr(string s) => s.Length > 120 ? s[..117] + "…" : s;

    public void Dispose() => _http.Dispose();
}
