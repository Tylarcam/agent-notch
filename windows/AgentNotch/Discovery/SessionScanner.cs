using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentNotch.Models;

namespace AgentNotch.Discovery;

public sealed partial class SessionScanner
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly TimeSpan RecentWindow = TimeSpan.FromHours(6);

    /// <summary>
    /// live = process discovery's resolved snapshots. Claude is matched two
    /// ways: directly by --resume=&lt;uuid&gt; when present (the desktop app's
    /// embedded launch, whose OS-level cwd doesn't point at the project), and
    /// otherwise by cwd (a plain terminal-launched `claude`, the same fallback
    /// Claude Code's Mac build itself relies on since it never keeps its
    /// transcript fd open). Codex transcripts embed their own cwd in the
    /// rollout's first line, so we match against that instead of an
    /// lsof-mapped open fd.
    /// </summary>
    public List<AgentSession> Scan(IReadOnlyList<ProcessDiscovery.Snapshot> live)
    {
        // pids grouped per cwd, newest process first — lines up with dated
        // transcripts (also newest-first) so idx-th transcript ~ idx-th pid.
        var claudeCwdPids = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var claudeResumePids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in live)
        {
            if (s.Kind != AgentKind.Claude) continue;
            if (s.ResumeSessionId is { } id) claudeResumePids[id] = s.Pid;
            else if (s.Cwd is { } cwd)
            {
                var key = ProcessDiscovery.EncodeProjectDir(cwd);
                (claudeCwdPids.TryGetValue(key, out var list) ? list : claudeCwdPids[key] = []).Add(s.Pid);
            }
        }
        foreach (var list in claudeCwdPids.Values) SortByCreationDesc(list, live);

        var codexCwdPids = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in live)
        {
            if (s.Kind == AgentKind.Codex && s.Cwd is { } cwd)
            {
                var key = NormalizeCwd(cwd);
                (codexCwdPids.TryGetValue(key, out var list) ? list : codexCwdPids[key] = []).Add(s.Pid);
            }
        }
        foreach (var list in codexCwdPids.Values) SortByCreationDesc(list, live);

        bool Recent(AgentSession s) => s.IsLive || DateTime.UtcNow - s.LastModified < RecentWindow;

        var sessions = ScanClaude(claudeCwdPids, claudeResumePids).Where(Recent)
            .Concat(GroupCodex(ScanCodex(codexCwdPids).Where(Recent)))
            .Concat(ScanCursor().Where(Recent))
            .ToList();
        AttachHerdr(sessions);
        ApprovalStore.Attach(sessions);
        sessions.Sort((a, b) =>
        {
            var pa = a.PendingApproval is not null ? 1 : 0;
            var pb = b.PendingApproval is not null ? 1 : 0;
            if (pa != pb) return pb - pa;
            var ba = a.AnyBusy ? 1 : 0;
            var bb = b.AnyBusy ? 1 : 0;
            if (ba != bb) return bb - ba;
            var la = a.AnyLive ? 1 : 0;
            var lb = b.AnyLive ? 1 : 0;
            if (la != lb) return lb - la;
            return b.EffectiveLastModified.CompareTo(a.EffectiveLastModified);
        });
        return sessions;
    }

    private static void SortByCreationDesc(List<int> pids, IReadOnlyList<ProcessDiscovery.Snapshot> live)
    {
        var created = live.ToDictionary(s => s.Pid, s => s.CreatedAt);
        pids.Sort((a, b) => created[b].CompareTo(created[a]));
    }

    private static string NormalizeCwd(string cwd) => cwd.TrimEnd('\\', '/');

    /// <summary>Fold Codex subagent rollouts under their root thread as children.</summary>
    private static List<AgentSession> GroupCodex(IEnumerable<AgentSession> nodes)
    {
        var list = nodes.ToList();
        var byId = list.Where(n => !string.IsNullOrEmpty(n.ThreadId))
            .GroupBy(n => n.ThreadId).ToDictionary(g => g.Key, g => g.First());

        string RootKey(AgentSession n)
        {
            var cur = n;
            var hops = 0;
            while (cur.ParentId is { } p && hops < 10)
            {
                if (!byId.TryGetValue(p, out var parent)) return p; // parent aged out: group under its id anyway
                cur = parent;
                hops++;
            }
            return cur.ThreadId;
        }

        var groups = new Dictionary<string, List<AgentSession>>();
        foreach (var n in list)
        {
            var key = RootKey(n);
            if (!groups.TryGetValue(key, out var g)) groups[key] = g = [];
            g.Add(n);
        }

        var outList = new List<AgentSession>();
        foreach (var (key, members) in groups)
        {
            var parent = byId.GetValueOrDefault(key) ?? members.OrderByDescending(m => m.LastModified).First();
            var kids = members.Where(m => m.ThreadId != parent.ThreadId)
                .OrderByDescending(m => m.LastModified).ToList();
            // One codex process serves the whole thread group but its resolved
            // cwd only ever matches its most-recently-opened rollout — so
            // liveness observed on any member means the shared process is
            // alive for all of them.
            if (parent.IsLive || kids.Any(k => k.IsLive))
            {
                parent.IsLive = true;
                foreach (var k in kids) k.IsLive = true;
            }
            parent.Children = kids;
            outList.Add(parent);
        }
        return outList;
    }

    /// <summary>Cursor IDE agent chats live under ~/.cursor/projects/*/agent-transcripts/&lt;id&gt;/&lt;id&gt;.jsonl.
    /// There is no per-session process — live means Cursor is open and this transcript was written recently.</summary>
    private static List<AgentSession> ScanCursor()
    {
        var outList = new List<AgentSession>();
        var root = Path.Combine(Home, ".cursor", "projects");
        if (!Directory.Exists(root)) return outList;
        var cursorOpen = Process.GetProcessesByName("Cursor").Length > 0;

        foreach (var projDir in Directory.EnumerateDirectories(root))
        {
            var transcripts = Path.Combine(projDir, "agent-transcripts");
            if (!Directory.Exists(transcripts)) continue;
            var slug = Path.GetFileName(projDir);
            var repo = RepoFromCursorSlug(slug);
            var workspace = CursorWorkspace(projDir, slug);

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(transcripts, "*.jsonl", SearchOption.AllDirectories); }
            catch { continue; }

            foreach (var file in files)
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}subagents{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                    continue;
                DateTime mtime;
                try { mtime = File.GetLastWriteTimeUtc(file); }
                catch { continue; }
                var age = DateTime.UtcNow - mtime;
                var isLive = cursorOpen && age < TimeSpan.FromMinutes(5);
                if (!isLive && age > RecentWindow) continue;

                var info = CursorInfo(file);
                var head = HeadInfo(file);
                outList.Add(new AgentSession
                {
                    Id = file,
                    ThreadId = Path.GetFileNameWithoutExtension(file),
                    Kind = AgentKind.Cursor,
                    Title = repo,
                    Snippet = info.Snippet,
                    Model = string.IsNullOrEmpty(info.Model) ? "" : info.Model,
                    LastModified = mtime,
                    Prompt = info.Prompt,
                    ChatTitle = string.IsNullOrEmpty(info.ChatTitle) ? head.ChatTitle : info.ChatTitle,
                    Goal = string.IsNullOrEmpty(info.Goal) ? head.Goal : info.Goal,
                    FileName = ShortFileName(file),
                    Cwd = head.Cwd ?? workspace,
                    IsLive = isLive,
                    LastActivity = mtime,
                });
            }
        }
        return outList;
    }

    private static string RepoFromCursorSlug(string slug)
    {
        var idx = slug.IndexOf("-code-", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0 && idx + 6 < slug.Length) return slug[(idx + 6)..];
        return slug.Split('-').LastOrDefault(s => s.Length > 0) ?? slug;
    }

    /// <summary>Resolve the workspace folder for a Cursor project dir: read
    /// workspace.json when present, else decode the project slug path.</summary>
    private static string? CursorWorkspace(string projDir, string slug)
    {
        var wsFile = Path.Combine(projDir, "workspace.json");
        if (File.Exists(wsFile))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(wsFile));
                var root = doc.RootElement;
                foreach (var key in new[] { "folder", "path", "workspace" })
                {
                    if (root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
                    {
                        var path = el.GetString();
                        if (!string.IsNullOrEmpty(path)) return path;
                    }
                }
            }
            catch { /* malformed */ }
        }
        return WorkspaceFromCursorSlug(slug);
    }

    /// <summary>Decode ~/.cursor/projects/&lt;slug&gt; back to a Windows path
    /// (e.g. c-Users-tylar-code-agent-notch → C:\Users\tylar\code\agent-notch).</summary>
    private static string? WorkspaceFromCursorSlug(string slug)
    {
        if (string.IsNullOrEmpty(slug) || char.IsDigit(slug[0])) return null;
        var parts = slug.Split('-');
        if (parts.Length < 2 || parts[0].Length != 1) return null;

        var path = char.ToUpperInvariant(parts[0][0]) + ":\\" + parts[1];
        var pending = "";
        for (var i = 2; i < parts.Length; i++)
        {
            var segment = pending.Length == 0 ? parts[i] : pending + "-" + parts[i];
            var candidate = path + "\\" + segment;
            if (Directory.Exists(candidate))
            {
                path = candidate;
                pending = "";
            }
            else
            {
                pending = segment;
            }
        }
        if (pending.Length > 0)
        {
            var final = path + "\\" + pending;
            if (Directory.Exists(final)) path = final;
        }
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>Overlay herdr's live agent list: mark matching transcripts live,
    /// and add a row for any herdr-managed agent that has no transcript yet
    /// (gemini, etc. started in a Termius pane).</summary>
    private static void AttachHerdr(List<AgentSession> sessions)
    {
        foreach (var agent in ListHerdrAgents())
        {
            var sessionId = agent.SessionId;
            var match = sessions.FirstOrDefault(s =>
                (!string.IsNullOrEmpty(sessionId) &&
                    (string.Equals(Path.GetFileNameWithoutExtension(s.Id), sessionId, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(s.ThreadId, sessionId, StringComparison.OrdinalIgnoreCase)))
                || string.Equals(s.Id, "herdr:" + agent.TerminalId, StringComparison.OrdinalIgnoreCase));

            var busy = agent.Busy;
            if (match is not null)
            {
                match.IsLive = true;
                match.HerdrTerminalId = agent.TerminalId;
                match.LastActivity = busy ? DateTime.UtcNow : DateTime.UtcNow - TimeSpan.FromMinutes(1);
                if (string.IsNullOrEmpty(match.ChatTitle) && !string.IsNullOrEmpty(agent.Title))
                    match.ChatTitle = agent.Title;
                continue;
            }

            var kind = agent.AgentName.ToLowerInvariant() switch
            {
                "claude" => AgentKind.Claude,
                "codex" => AgentKind.Codex,
                "cursor" => AgentKind.Cursor,
                _ => AgentKind.Other,
            };
            var harness = kind == AgentKind.Other
                ? (agent.AgentName.Length == 0 ? "Agent" : char.ToUpperInvariant(agent.AgentName[0]) + agent.AgentName[1..])
                : null;
            sessions.Add(new AgentSession
            {
                Id = "herdr:" + agent.TerminalId,
                Kind = kind,
                Title = LastPathComp(agent.Cwd) ?? harness ?? kind.ShortName(),
                ChatTitle = agent.Title,
                Goal = agent.Title,
                LastModified = DateTime.UtcNow,
                LastActivity = busy ? DateTime.UtcNow : DateTime.UtcNow - TimeSpan.FromMinutes(1),
                Cwd = agent.Cwd,
                IsLive = true,
                HerdrTerminalId = agent.TerminalId,
                ThreadId = sessionId ?? "",
                HarnessOverride = harness,
            });
        }
    }

    private readonly record struct HerdrAgent(string AgentName, string? SessionId, string TerminalId, string? Cwd, string Title, bool Busy);

    private static List<HerdrAgent> ListHerdrAgents()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "herdr",
                Arguments = "agent list",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return [];
            var json = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(1500))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return [];
            }
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("agents", out var agents))
                return [];

            var list = new List<HerdrAgent>();
            foreach (var a in agents.EnumerateArray())
            {
                var name = a.TryGetProperty("agent", out var n) ? n.GetString() ?? "" : "";
                string? sessionId = null;
                if (a.TryGetProperty("agent_session", out var sess) &&
                    sess.TryGetProperty("value", out var val))
                    sessionId = val.GetString();
                var termId = a.TryGetProperty("terminal_id", out var t) ? t.GetString() ?? "" : "";
                if (termId.Length == 0) continue;
                var cwd = a.TryGetProperty("cwd", out var c) ? c.GetString() : null;
                var title = a.TryGetProperty("terminal_title_stripped", out var tt) ? tt.GetString() ?? "" : "";
                var status = a.TryGetProperty("agent_status", out var st) ? st.GetString() ?? "" : "";
                var busy = status.Equals("busy", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("running", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("working", StringComparison.OrdinalIgnoreCase);
                list.Add(new HerdrAgent(name, sessionId, termId, cwd, title, busy));
            }
            return list;
        }
        catch
        {
            return [];
        }
    }

    private static List<AgentSession> ScanClaude(Dictionary<string, List<int>> cwdPids, Dictionary<string, int> resumePids)
    {
        var outList = new List<AgentSession>();
        var root = Path.Combine(Home, ".claude", "projects");
        if (!Directory.Exists(root)) return outList;

        foreach (var projDir in Directory.EnumerateDirectories(root))
        {
            var dated = Directory.EnumerateFiles(projDir, "*.jsonl")
                .Select(f => (File: f, Mtime: File.GetLastWriteTimeUtc(f)))
                .OrderByDescending(x => x.Mtime)
                .ToList();

            var projName = Path.GetFileName(projDir);
            var pidsByCwd = cwdPids.GetValueOrDefault(projName);

            for (var idx = 0; idx < dated.Count; idx++)
            {
                var (file, mtime) = dated[idx];
                var fallback = projName.Split('-').LastOrDefault(s => s.Length > 0) ?? projName;
                var info = TailInfo(file);
                var head = HeadInfo(file);
                var sessionId = Path.GetFileNameWithoutExtension(file);
                var resumePid = resumePids.GetValueOrDefault(sessionId);
                var cwdPid = pidsByCwd is not null && idx < pidsByCwd.Count ? pidsByCwd[idx] : (int?)null;
                var pid = resumePid != 0 ? resumePid : cwdPid;
                var isLive = pid is not null;
                var sess = new AgentSession
                {
                    Id = file,
                    Kind = AgentKind.Claude,
                    Title = LastPathComp(head.Cwd) ?? LastPathComp(info.Cwd) ?? fallback,
                    Snippet = info.Snippet,
                    Model = info.Model,
                    LastModified = mtime,
                    Prompt = info.Prompt,
                    ChatTitle = head.ChatTitle,
                    Goal = head.Goal,
                    FileName = ShortFileName(file),
                    LastActivity = info.Activity,
                    Cwd = head.Cwd ?? info.Cwd,
                    IsLive = isLive,
                    Pid = pid,
                };
                sess.Children = ClaudeSubagents(file, parentLive: isLive);
                outList.Add(sess);
            }
        }
        return outList;
    }

    private static List<AgentSession> ScanCodex(Dictionary<string, List<int>> cwdPids)
    {
        var outList = new List<AgentSession>();
        var root = Path.Combine(Home, ".codex", "sessions");
        if (!Directory.Exists(root)) return outList;

        // group candidates by normalized cwd so ties (N processes, N transcripts
        // in the same folder) mark the N newest live, same as Claude's fallback.
        var candidates = new List<(string File, DateTime Mtime, string Title, string Id, string? ParentId, string? Nickname, string? Cwd)>();
        foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
        {
            var mtime = File.GetLastWriteTimeUtc(file);
            if (DateTime.UtcNow - mtime > RecentWindow) continue; // skip old files early, avoid reading them
            var meta = CodexMeta(file);
            candidates.Add((file, mtime, meta.Title, meta.Id, meta.ParentId, meta.Nickname, meta.Cwd));
        }

        foreach (var group in candidates.GroupBy(c => c.Cwd is { } cwd ? NormalizeCwd(cwd) : null))
        {
            var pids = group.Key is { } key ? cwdPids.GetValueOrDefault(key) : null;
            var ordered = group.OrderByDescending(c => c.Mtime).ToList();
            for (var idx = 0; idx < ordered.Count; idx++)
            {
                var c = ordered[idx];
                var info = TailInfo(c.File);
                var head = HeadInfo(c.File);
                var pid = pids is not null && idx < pids.Count ? pids[idx] : (int?)null;
                outList.Add(new AgentSession
                {
                    Id = c.File,
                    Kind = AgentKind.Codex,
                    Title = LastPathComp(head.Cwd) ?? c.Title,
                    Snippet = info.Snippet,
                    Model = info.Model,
                    LastModified = c.Mtime,
                    Prompt = info.Prompt,
                    ChatTitle = head.ChatTitle,
                    Goal = head.Goal,
                    FileName = ShortFileName(c.File),
                    Cwd = head.Cwd ?? c.Cwd,
                    IsLive = pid is not null,
                    Pid = pid,
                    ThreadId = c.Id,
                    ParentId = c.ParentId,
                    Nickname = c.Nickname,
                });
            }
        }
        return outList;
    }

    /// <summary>Claude Code subagent transcripts live in &lt;proj&gt;/&lt;session-uuid&gt;/subagents/agent-*.jsonl</summary>
    private static List<AgentSession> ClaudeSubagents(string sessionFile, bool parentLive)
    {
        var dir = Path.Combine(Path.GetDirectoryName(sessionFile)!,
            Path.GetFileNameWithoutExtension(sessionFile), "subagents");
        if (!Directory.Exists(dir)) return [];

        var kids = new List<AgentSession>();
        foreach (var c in Directory.EnumerateFiles(dir, "*.jsonl"))
        {
            var mtime = File.GetLastWriteTimeUtc(c);
            if (DateTime.UtcNow - mtime >= RecentWindow) continue;
            var info = TailInfo(c);
            kids.Add(new AgentSession
            {
                Id = c,
                Kind = AgentKind.Claude,
                Title = "subagent",
                Snippet = info.Snippet,
                Model = info.Model,
                LastModified = mtime,
                // no nicknames here — label with the task it was given
                Nickname = string.IsNullOrEmpty(info.Prompt) ? "subagent" : Truncate(info.Prompt, 40),
                // subagents share the parent process — liveness inherits, busyness from writes
                IsLive = parentLive,
                LastActivity = info.Activity,
            });
        }
        kids.Sort((a, b) => b.LastModified.CompareTo(a.LastModified));
        return kids;
    }

    private static string Truncate(string s, int n) => s.Length > n ? s[..n] : s;

    private static (string Title, string Id, string? ParentId, string? Nickname, string? Cwd) CodexMeta(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[262_144];
            var read = fs.Read(buf, 0, buf.Length);
            var text = Encoding.UTF8.GetString(buf, 0, read);
            var line = text.Split('\n').FirstOrDefault();
            if (line is null) return ("Codex", file, null, null, null);
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("payload", out var payload)) return ("Codex", file, null, null, null);
            var cwd = payload.TryGetProperty("cwd", out var cwdEl) ? cwdEl.GetString() : null;
            var title = cwd is { Length: > 0 } ? Path.GetFileName(cwd.TrimEnd('\\', '/')) : "Codex";
            var id = payload.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? file : file;
            var parentId = payload.TryGetProperty("parent_thread_id", out var pEl) ? pEl.GetString() : null;
            string? nickname = null;
            if (payload.TryGetProperty("source", out var source) &&
                source.TryGetProperty("subagent", out var subagent) &&
                subagent.TryGetProperty("thread_spawn", out var spawn) &&
                spawn.TryGetProperty("agent_nickname", out var nick))
            {
                nickname = nick.GetString();
            }
            return (title, id, parentId, nickname, cwd);
        }
        catch
        {
            return ("Codex", file, null, null, null);
        }
    }

    [GeneratedRegex("\"model\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex ModelRegex();

    /// <summary>Read the tail of a jsonl transcript: last human-readable text + model name + timestamp of the last conversational entry.</summary>
    private static (string Snippet, string Model, string Prompt, DateTime? Activity, string? Cwd) TailInfo(string file)
    {
        string snippet = "", model = "", prompt = "", cwd = "";
        DateTime? activity = null;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var size = fs.Length;
            var readLen = Math.Min(size, 131_072);
            fs.Seek(size - readLen, SeekOrigin.Begin);
            var buf = new byte[readLen];
            fs.ReadExactly(buf);
            var text = Encoding.UTF8.GetString(buf);

            var lines = text.Split('\n');
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (model.Length == 0)
                {
                    var m = ModelRegex().Match(line);
                    if (m.Success) model = m.Groups[1].Value.Replace("claude-", "");
                }
                if (snippet.Length == 0 || prompt.Length == 0 || activity is null || cwd.Length == 0)
                {
                    JsonDocument? doc = null;
                    try { doc = JsonDocument.Parse(line); } catch { /* partial/invalid line, skip */ }
                    if (doc is not null)
                    {
                        using (doc)
                        {
                            var root = doc.RootElement;
                            if (snippet.Length == 0 && ExtractText(root) is { } s) snippet = s;
                            if (prompt.Length == 0 && ExtractUserPrompt(root) is { } p) prompt = p;
                            if (cwd.Length == 0 && root.TryGetProperty("cwd", out var cwdEl) &&
                                cwdEl.ValueKind == JsonValueKind.String)
                            {
                                cwd = cwdEl.GetString() ?? "";
                            }
                            // "system" entries (away_summary, compaction notes…) are housekeeping, not activity
                            if (activity is null && root.TryGetProperty("type", out var ty))
                            {
                                var tyStr = ty.GetString();
                                if ((tyStr == "user" || tyStr == "assistant") &&
                                    root.TryGetProperty("timestamp", out var tsEl) &&
                                    DateTimeOffset.TryParse(tsEl.GetString(), out var ts))
                                {
                                    activity = ts.UtcDateTime;
                                }
                            }
                        }
                    }
                }
                if (snippet.Length > 0 && model.Length > 0 && prompt.Length > 0 && activity is not null && cwd.Length > 0) break;
            }

            if (model.Length == 0 && size > readLen)
            {
                // model can appear only early in long transcripts — check the head too
                fs.Seek(0, SeekOrigin.Begin);
                var headBuf = new byte[Math.Min(size, 65_536)];
                var headRead = fs.Read(headBuf, 0, headBuf.Length);
                var headText = Encoding.UTF8.GetString(headBuf, 0, headRead);
                var hm = ModelRegex().Match(headText);
                if (hm.Success) model = hm.Groups[1].Value.Replace("claude-", "");
            }
        }
        catch
        {
            // transcript vanished or is mid-write — return whatever we got
        }
        return (snippet, model, prompt, activity, cwd.Length > 0 ? cwd : null);
    }

    private static (string ChatTitle, string Goal, string Prompt, string Snippet, string Model) CursorInfo(string file)
    {
        string first = "", last = "", snippet = "", model = "";
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var size = fs.Length;
            var readLen = (int)Math.Min(size, 196_608);
            var buf = new byte[readLen];
            if (size > readLen) fs.Seek(size - readLen, SeekOrigin.Begin);
            fs.ReadExactly(buf.AsSpan(0, readLen));
            foreach (var line in Encoding.UTF8.GetString(buf, 0, readLen).Split('\n'))
            {
                JsonDocument? doc = null;
                try { doc = JsonDocument.Parse(line); } catch { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    if (model.Length == 0 && root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                        model = m.GetString() ?? "";
                    if (!root.TryGetProperty("role", out var roleEl)) continue;
                    var role = roleEl.GetString();
                    if (role == "user" && CursorUserQuery(root) is { } q)
                    {
                        if (first.Length == 0) first = q;
                        last = q;
                    }
                    else if (role == "assistant" && CursorAssistantText(root) is { } t)
                        snippet = t;
                }
            }
            if (first.Length == 0 && size > readLen)
            {
                fs.Seek(0, SeekOrigin.Begin);
                var headBuf = new byte[Math.Min(size, 98_304)];
                var n = fs.Read(headBuf, 0, headBuf.Length);
                foreach (var line in Encoding.UTF8.GetString(headBuf, 0, n).Split('\n'))
                {
                    JsonDocument? doc = null;
                    try { doc = JsonDocument.Parse(line); } catch { continue; }
                    using (doc)
                    {
                        if (CursorUserQuery(doc.RootElement) is { } q)
                        {
                            first = q;
                            break;
                        }
                    }
                }
            }
        }
        catch { /* mid-write */ }
        var chat = first.Length > 48 ? first[..48] + "…" : first;
        return (chat, first, last, snippet, model);
    }

    private static string? CursorUserQuery(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg) ||
            !msg.TryGetProperty("content", out var content)) return null;
        string? text = null;
        if (content.ValueKind == JsonValueKind.String) text = content.GetString();
        else if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var ty) && ty.GetString() == "text" &&
                    part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    text = t.GetString();
                    break;
                }
            }
        }
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf("<user_query>", StringComparison.Ordinal);
        var end = text.IndexOf("</user_query>", StringComparison.Ordinal);
        if (start >= 0 && end > start) text = text[(start + 12)..end];
        text = text.Replace("\r", "").Trim();
        var nl = text.IndexOf('\n');
        if (nl > 0) text = text[..nl].Trim();
        return Clean(text, 160);
    }

    private static string? CursorAssistantText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg) ||
            !msg.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array) return null;
        foreach (var part in content.EnumerateArray())
        {
            if (part.TryGetProperty("type", out var ty) && ty.GetString() == "text" &&
                part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            {
                var s = t.GetString();
                if (!string.IsNullOrWhiteSpace(s) && !s.StartsWith('{')) return Clean(s, 90);
            }
        }
        return null;
    }

    /// <summary>First human prompt, Claude <c>ai-title</c>, and cwd from the start of the transcript.</summary>
    private static (string ChatTitle, string Goal, string? Cwd) HeadInfo(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[98_304];
            var read = fs.Read(buf, 0, buf.Length);
            var text = Encoding.UTF8.GetString(buf, 0, read);
            string firstPrompt = "", aiTitle = "", cwd = "";
            foreach (var line in text.Split('\n'))
            {
                JsonDocument? doc = null;
                try { doc = JsonDocument.Parse(line); } catch { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    if (cwd.Length == 0)
                    {
                        if (root.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String)
                            cwd = cwdEl.GetString() ?? "";
                        else if (root.TryGetProperty("payload", out var payload) &&
                                 payload.TryGetProperty("cwd", out var pCwd) && pCwd.ValueKind == JsonValueKind.String)
                            cwd = pCwd.GetString() ?? "";
                    }
                    if (aiTitle.Length == 0 &&
                        root.TryGetProperty("type", out var ty) && ty.GetString() == "ai-title" &&
                        root.TryGetProperty("aiTitle", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
                    {
                        aiTitle = (titleEl.GetString() ?? "").Trim();
                    }
                    if (firstPrompt.Length == 0 && ExtractUserPrompt(root, max: 160) is { } p)
                        firstPrompt = p;
                }
                if (firstPrompt.Length > 0 && aiTitle.Length > 0 && cwd.Length > 0) break;
            }
            var chatTitle = aiTitle.Length > 0
                ? aiTitle
                : firstPrompt.Length > 48 ? firstPrompt[..48] + "…" : firstPrompt;
            return (chatTitle, firstPrompt, cwd.Length > 0 ? cwd : null);
        }
        catch
        {
            return ("", "", null);
        }
    }

    private static string? LastPathComp(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static string ShortFileName(string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var ext = Path.GetExtension(file);
        return stem.Length > 12 ? stem[..8] + ext : Path.GetFileName(file);
    }

    /// <summary>The user's own message, if this line is one.</summary>
    private static string? ExtractUserPrompt(JsonElement obj, int max = 90)
    {
        // Codex: {"payload":{"type":"user_message","message":"..."}}
        if (obj.TryGetProperty("payload", out var payload) &&
            payload.TryGetProperty("type", out var pty) && pty.GetString() == "user_message" &&
            payload.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
        {
            return Clean(m.GetString()!, max);
        }
        // Claude: {"type":"user","message":{"content":"..." | [{"type":"text","text":...}]}}
        if (obj.TryGetProperty("type", out var ty) && ty.GetString() == "user" &&
            obj.TryGetProperty("message", out var msg))
        {
            if (msg.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.TryGetProperty("type", out var skip) && skip.GetString() == "tool_result")
                            return null;
                    }
                }
                if (content.ValueKind == JsonValueKind.String) return Clean(content.GetString()!, max);
                if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.TryGetProperty("type", out var pt) && pt.GetString() == "text" &&
                            part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                        {
                            return Clean(t.GetString()!, max);
                        }
                    }
                }
            }
        }
        return null;
    }

    private static string? ExtractText(JsonElement obj)
    {
        JsonElement? content = null;
        if (obj.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c)) content = c;
        if (content is null && obj.TryGetProperty("payload", out var payload))
        {
            if (payload.TryGetProperty("content", out var pc)) content = pc;
            else if (payload.TryGetProperty("message", out var pm) && pm.TryGetProperty("content", out var pmc)) content = pmc;
        }
        if (content is not { } el) return null;
        if (el.ValueKind == JsonValueKind.String) return Clean(el.GetString()!);
        if (el.ValueKind == JsonValueKind.Array)
        {
            var arr = el.EnumerateArray().ToList();
            for (var i = arr.Count - 1; i >= 0; i--)
            {
                if (arr[i].TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    return Clean(t.GetString()!);
            }
        }
        return null;
    }

    private static string? Clean(string s, int max = 90)
    {
        var t = s.Trim();
        if (t.Length == 0 || t.StartsWith('<') || t.StartsWith('{')) return null; // skip system-reminder / tool json
        t = t.Replace("\n", " ");
        if (t.Length > max) t = t[..max] + "…";
        return t;
    }
}
