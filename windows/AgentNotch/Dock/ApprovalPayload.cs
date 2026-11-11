using AgentNotch.Models;

namespace AgentNotch.Dock;

internal static class ApprovalPayload
{
    public static string Describe(ApprovalRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.Tool)) return req.Tool!;
        var msg = req.Message;
        if (msg.Contains('\\') || msg.Contains('/'))
        {
            var parts = msg.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
            var path = parts.FirstOrDefault(p => p.Contains('/') || p.Contains('\\'));
            if (path is not null) return path;
        }
        if (msg.StartsWith("Allow", StringComparison.OrdinalIgnoreCase) && msg.Length < 120) return msg;
        return msg.Length > 80 ? msg[..77] + "…" : msg;
    }

    public static string Title(ApprovalRequest req, string agentName)
    {
        var tool = req.Tool ?? "tool";
        if (req.Message.Contains("edit", StringComparison.OrdinalIgnoreCase))
            return $"Allow {agentName} to edit?";
        if (req.Message.Contains("run", StringComparison.OrdinalIgnoreCase) || req.Message.Contains("command", StringComparison.OrdinalIgnoreCase))
            return $"Allow {agentName} to run a command?";
        return $"Allow {agentName} — {tool}?";
    }
}

internal enum ApprovalCardState { Pending, Approved, Denied }
