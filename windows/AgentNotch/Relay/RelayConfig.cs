using System.IO;
using System.Text.Json;

namespace AgentNotch.Relay;

/// <summary>Reads relay base URL from AN_AGENT_NOTCH_RELAY_URL, relay.json, or env AGENT_NOTCH_RELAY_URL — no secrets.</summary>
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
}
