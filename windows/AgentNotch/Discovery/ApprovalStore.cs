using System.IO;
using System.Text.Json;
using AgentNotch.Models;

namespace AgentNotch.Discovery;

/// <summary>Reads pending-approval markers from Claude (hooks/notify.ps1) and
/// Cursor (hooks/cursor-permission.ps1). Cursor Approve/Deny writes a sibling
/// <c>.decision.json</c> file; Claude still uses SendApprovalKey keystrokes.</summary>
public static class ApprovalStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "agent-notch", "pending");
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    public static void Attach(List<AgentSession> sessions)
    {
        if (!Directory.Exists(Dir)) return;

        var byId = new Dictionary<string, AgentSession>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sessions)
        {
            foreach (var id in MarkerIds(s))
            {
                if (!string.IsNullOrEmpty(id)) byId[id] = s;
            }
        }

        foreach (var file in Directory.EnumerateFiles(Dir, "*.json"))
        {
            if (file.EndsWith(".decision.json", StringComparison.OrdinalIgnoreCase)) continue;

            var markerId = Path.GetFileNameWithoutExtension(file);
            var req = TryRead(file);
            if (req is null) { TryDelete(file); continue; }

            AgentSession? session = null;
            if (!byId.TryGetValue(markerId, out session))
            {
                var jsonSid = TryReadSessionId(file);
                if (jsonSid is not null) byId.TryGetValue(jsonSid, out session);
            }
            if (session is null)
            {
                session = sessions.FirstOrDefault(s => s.Kind == AgentKind.Cursor && (s.IsLive || s.AnyLive));
                if (session is null)
                {
                    if (DateTime.UtcNow - req.RequestedAt > StaleAfter) TryDelete(file);
                    continue;
                }
            }

            var resolved = session.Kind != AgentKind.Cursor &&
                (!session.AnyLive ||
                (session.LastActivity is { } act && act > req.RequestedAt) ||
                DateTime.UtcNow - req.RequestedAt > StaleAfter);

            if (resolved && session.Kind != AgentKind.Cursor) { TryDelete(file); continue; }

            if (session.Kind == AgentKind.Cursor && DateTime.UtcNow - req.RequestedAt > StaleAfter)
            {
                TryDelete(file);
                continue;
            }

            session.PendingApproval = req;
            session.PendingMarkerId = markerId;
        }
    }

    public static void WriteDecision(AgentSession session, bool allow)
    {
        Directory.CreateDirectory(Dir);
        var id = session.PendingMarkerId ?? PendingMarkerId(session);
        if (string.IsNullOrEmpty(id)) return;
        var payload = JsonSerializer.Serialize(new
        {
            decision = allow ? "allow" : "deny",
            timestamp = DateTimeOffset.UtcNow.ToString("o"),
        });
        var path = Path.Combine(Dir, $"{id}.decision.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, payload);
        File.Move(tmp, path, overwrite: true);
    }

    public static void Clear(AgentSession session)
    {
        var id = session.PendingMarkerId ?? PendingMarkerId(session);
        if (string.IsNullOrEmpty(id)) return;
        TryDelete(Path.Combine(Dir, $"{id}.json"));
    }

    private static IEnumerable<string> MarkerIds(AgentSession s)
    {
        yield return PendingMarkerId(s);
        if (s.Kind == AgentKind.Cursor && s.Id.Contains(Path.DirectorySeparatorChar))
        {
            var bn = Path.GetFileNameWithoutExtension(s.Id);
            if (!string.IsNullOrEmpty(bn)) yield return bn;
        }
    }

    private static string PendingMarkerId(AgentSession s) => s.Kind switch
    {
        AgentKind.Claude => Path.GetFileNameWithoutExtension(s.Id),
        AgentKind.Cursor => !string.IsNullOrEmpty(s.ThreadId)
            ? s.ThreadId
            : Path.GetFileNameWithoutExtension(s.Id),
        _ => s.ThreadId,
    };

    private static string? TryReadSessionId(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("sessionId", out var sid) && sid.ValueKind == JsonValueKind.String)
                return sid.GetString();
        }
        catch { /* ignore */ }
        return null;
    }

    private static ApprovalRequest? TryRead(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var tool = root.TryGetProperty("tool", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var ts = root.TryGetProperty("timestamp", out var tsEl) &&
                DateTimeOffset.TryParse(tsEl.GetString(), out var parsed) ? parsed.UtcDateTime : DateTime.UtcNow;
            return new ApprovalRequest(message, tool, ts);
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* already gone, or briefly locked */ }
    }
}
