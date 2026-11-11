using System.IO;
using System.Text.Json;
using AgentNotch.Models;

namespace AgentNotch.Discovery;

/// <summary>Reads pending-approval markers written by the opt-in Notification
/// hook (hooks/notify.ps1) and attaches them to matching sessions. A marker
/// is cleared once the transcript advances past its timestamp (proof the
/// prompt was answered — from the terminal, or via SendApprovalKey), the
/// session's process exits, or it goes stale.</summary>
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
            var id = SessionId(s);
            if (!string.IsNullOrEmpty(id)) byId[id] = s;
        }

        foreach (var file in Directory.EnumerateFiles(Dir, "*.json"))
        {
            var sessionId = Path.GetFileNameWithoutExtension(file);
            var req = TryRead(file);
            if (req is null) { TryDelete(file); continue; }

            if (!byId.TryGetValue(sessionId, out var session))
            {
                if (DateTime.UtcNow - req.RequestedAt > StaleAfter) TryDelete(file);
                continue;
            }

            // resolved (from the terminal or by us) once the transcript moved on
            var resolved = !session.AnyLive ||
                (session.LastActivity is { } act && act > req.RequestedAt) ||
                DateTime.UtcNow - req.RequestedAt > StaleAfter;

            if (resolved) { TryDelete(file); continue; }

            session.PendingApproval = req;
        }
    }

    /// <summary>Called right after a quick-approve button fires, so the
    /// banner doesn't linger until the next poll notices the transcript moved.</summary>
    public static void Clear(AgentSession session)
    {
        var id = SessionId(session);
        if (string.IsNullOrEmpty(id)) return;
        TryDelete(Path.Combine(Dir, $"{id}.json"));
    }

    private static string SessionId(AgentSession s) =>
        s.Kind == AgentKind.Claude ? Path.GetFileNameWithoutExtension(s.Id) : s.ThreadId;

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
            // marker mid-write or corrupt — skip this poll, it'll be retried
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* already gone, or briefly locked */ }
    }
}
