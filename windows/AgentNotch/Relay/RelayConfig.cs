using System.IO;
using System.Text.Json;

namespace AgentNotch.Relay;

/// <summary>Reads relay base URL, session id, and Aether URL from env / relay.json — no secrets on disk.</summary>
internal static class RelayConfig
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "agent-notch", "relay.json");

    public static string? TryGetBaseUrl()
    {
        var fromEnv = Environment.GetEnvironmentVariable("AGENT_NOTCH_RELAY_URL")
            ?? Environment.GetEnvironmentVariable("AN_AGENT_NOTCH_RELAY_URL");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.TrimEnd('/');

        if (!File.Exists(ConfigPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            if (doc.RootElement.TryGetProperty("baseUrl", out var u) && u.ValueKind == JsonValueKind.String)
            {
                var s = u.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s.TrimEnd('/');
            }
        }
        catch { /* ignore corrupt config */ }
        return null;
    }

    public static string TryGetAetherBaseUrl()
    {
        var fromEnv = Environment.GetEnvironmentVariable("AGENT_NOTCH_AETHER_URL");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.TrimEnd('/');

        if (File.Exists(ConfigPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                if (doc.RootElement.TryGetProperty("aetherBaseUrl", out var u) && u.ValueKind == JsonValueKind.String)
                {
                    var s = u.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s.TrimEnd('/');
                }
            }
            catch { /* ignore */ }
        }
        return "http://127.0.0.1:3000";
    }

    /// <summary>Odysseus Bearer token — env only, never relay.json.</summary>
    public static string? TryGetApiToken() =>
        FirstNonEmpty(
            Environment.GetEnvironmentVariable("ODYSSEUS_API_TOKEN"),
            Environment.GetEnvironmentVariable("AN_ODYSSEUS_API_TOKEN"));

    public static string? TryGetStoredSessionId()
    {
        if (!File.Exists(ConfigPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            if (doc.RootElement.TryGetProperty("sessionId", out var sid) && sid.ValueKind == JsonValueKind.String)
            {
                var s = sid.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            }
        }
        catch { /* ignore */ }
        return null;
    }

    public static void ClearStoredSessionId()
    {
        if (!File.Exists(ConfigPath)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            if (!doc.RootElement.TryGetProperty("sessionId", out _)) return;
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name.Equals("sessionId", StringComparison.OrdinalIgnoreCase)) continue;
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => prop.Value.GetRawText(),
                };
            }
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* ignore */ }
    }

    public static void PersistSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (File.Exists(ConfigPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number => prop.Value.GetRawText(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => prop.Value.GetRawText(),
                    };
                }
            }
            catch { /* overwrite corrupt */ }
        }
        dict["sessionId"] = sessionId.Trim();
        if (!dict.ContainsKey("baseUrl") && TryGetBaseUrl() is { } url)
            dict["baseUrl"] = url;
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string ConfigFilePath => ConfigPath;

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v!.Trim();
        return null;
    }
}
