namespace AgentNotch.Models;

/// <summary>A permission prompt the agent is currently blocked on, surfaced by
/// the opt-in Notification hook (see hooks/notify.ps1). <see cref="Tool"/> is
/// a best-effort parse of the hook's message text, not a guarantee.</summary>
public sealed record ApprovalRequest(string Message, string? Tool, DateTime RequestedAt);

public enum AgentKind { Claude, Codex, Cursor, Other }

public static class AgentKindExtensions
{
    public static string DisplayName(this AgentKind kind) => kind switch
    {
        AgentKind.Claude => "Claude Code",
        AgentKind.Codex => "Codex",
        AgentKind.Cursor => "Cursor",
        _ => "Agent",
    };

    public static string ShortName(this AgentKind kind) => kind switch
    {
        AgentKind.Claude => "Claude",
        AgentKind.Codex => "Codex",
        AgentKind.Cursor => "Cursor",
        _ => "Agent",
    };
}

public sealed class AgentSession
{
    public required string Id { get; init; }
    public required AgentKind Kind { get; init; }
    public required string Title { get; init; }
    public string Snippet { get; set; } = "";
    public string Model { get; set; } = "";
    public required DateTime LastModified { get; init; }
    public string Prompt { get; set; } = "";
    public string ChatTitle { get; set; } = "";
    public string Goal { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? HarnessOverride { get; set; }
    public string ThreadId { get; set; } = "";
    public string? ParentId { get; set; }
    public string? Nickname { get; set; }
    public List<AgentSession> Children { get; set; } = [];

    // working directory, for focusing/resuming; process id when a specific
    // live process could be matched to this transcript (null if ambiguous)
    public string? Cwd { get; set; }
    public int? Pid { get; set; }

    // herdr terminal_id when herdr is managing this session — used to focus
    // the host app (Termius, etc.) without walking the agent process tree
    public string? HerdrTerminalId { get; set; }

    // set by ApprovalStore.Attach when a matching marker file exists
    public ApprovalRequest? PendingApproval { get; set; }

    // process alive (from discovery, never mtime)
    public bool IsLive { get; set; }

    // last user/assistant entry — housekeeping writes (away_summary etc.)
    // bump the file mtime but must not count as activity
    public DateTime? LastActivity { get; set; }

    // hybrid: busy = alive AND conversing; quiet-while-alive is idle, not done
    public bool IsBusy => IsLive && (DateTime.UtcNow - (LastActivity ?? LastModified)).TotalSeconds < 30;

    public bool AnyLive => IsLive || Children.Exists(c => c.IsLive);
    public bool AnyBusy => IsBusy || Children.Exists(c => c.IsBusy);

    public DateTime EffectiveLastModified =>
        Children.Count == 0 ? LastModified : Children.Select(c => c.LastModified).Prepend(LastModified).Max();

    public string Harness => HarnessOverride ?? Kind.ShortName();
}
