using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentNotch.Models;

namespace AgentNotch.Discovery;

/// <summary>
/// Windows has no lsof: mapping a process to the file it holds open needs
/// NtQuerySystemInformation(SystemHandleInformation) + handle duplication +
/// NtQueryObject, which can hang on pipes and is fragile across Windows builds.
/// Deliberately skipped (per the simplified-heuristic choice) in favor of two
/// matching strategies, cheapest/most-precise first:
///   1. Claude Code launched with --resume=&lt;session-uuid&gt; (true of the
///      build embedded in the Claude *desktop* app, which is this user's main
///      way of running it — its OS-level cwd resolves to ~/.claude, not the
///      project dir, so cwd matching alone misses it entirely) names its
///      transcript file directly.
///   2. Otherwise, a PEB read (a plain cross-process memory read, not handle
///      enumeration) recovers the process's cwd — the same signal
///      open-vibe-island's Mac build falls back to for Claude anyway, since
///      Claude Code doesn't keep its transcript fd open even there. This is
///      what a plain terminal-launched `claude` resolves through.
/// </summary>
public sealed partial class ProcessDiscovery
{
    public sealed record Snapshot(AgentKind Kind, int Pid, string? Cwd, string? ResumeSessionId, DateTime CreatedAt);

    public List<Snapshot> LiveAgentProcesses()
    {
        var result = new List<Snapshot>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, Name, ExecutablePath, CommandLine, CreationDate FROM Win32_Process " +
            "WHERE Name='claude.exe' OR Name='codex.exe'");
        foreach (ManagementObject mo in searcher.Get())
        {
            var pid = Convert.ToInt32(mo["ProcessId"]);
            var name = (mo["Name"] as string) ?? "";
            var exePath = (mo["ExecutablePath"] as string) ?? "";
            var cmd = (mo["CommandLine"] as string) ?? "";
            var createdRaw = mo["CreationDate"] as string;
            var created = createdRaw is not null
                ? ManagementDateTimeConverter.ToDateTime(createdRaw)
                : DateTime.MinValue;

            AgentKind kind;
            if (name.Equals("claude.exe", StringComparison.OrdinalIgnoreCase) && IsClaudeAgent(exePath, cmd))
                kind = AgentKind.Claude;
            else if (name.Equals("codex.exe", StringComparison.OrdinalIgnoreCase) && IsCodexAgent(exePath, cmd))
                kind = AgentKind.Codex;
            else
                continue;

            var resumeMatch = ResumeIdRegex().Match(cmd);
            var resumeId = resumeMatch.Success ? resumeMatch.Groups[1].Value : null;
            result.Add(new Snapshot(kind, pid, GetProcessCurrentDirectory(pid), resumeId, created));
        }
        return result;
    }

    /// <summary>Brings the session's host app to the foreground.
    /// Cursor → existing Cursor window (never shells <c>cursor.cmd</c>, which
    /// flashes a console). Claude → herdr pane select (best-effort) then the
    /// Termius window this user hosts Claude in. Other live agents → herdr,
    /// then the pid window walk.</summary>
    public static void FocusSession(AgentSession session)
    {
        DebugLog($"FocusSession: title={session.Title} kind={session.Kind} id={session.Id} threadId={session.ThreadId} pid={session.Pid} isLive={session.IsLive} herdr={session.HerdrTerminalId ?? "nil"}");
        if (session.Kind == AgentKind.Cursor)
        {
            FocusCursor(session.Cwd);
            return;
        }
        if (session.Kind == AgentKind.Claude)
        {
            // herdr only selects the pane inside its own state — it does not
            // raise the OS host window. Always bring Termius forward after.
            _ = TryHerdrFocus(session);
            if (FocusProcessWindow("Termius", session.Title)) return;
            if (session.Pid is int claudePid) FocusConsole(claudePid);
            return;
        }
        if (TryHerdrFocus(session)) return;
        // herdr-managed sessions have no reachable host window via the agent
        // pid — FocusConsole would attach to a hidden ConPTY and flash wt/cmd.
        if (!string.IsNullOrEmpty(session.HerdrTerminalId) ||
            session.Id.StartsWith("herdr:", StringComparison.Ordinal))
        {
            DebugLog("FocusSession: herdr-managed but focus failed — skipping FocusConsole fallback");
            return;
        }
        if (session.Pid is int pid) FocusConsole(pid);
    }

    /// <summary>Raise an already-open Cursor window. Prefer a title that
    /// mentions the workspace folder; never invoke <c>cursor.cmd</c> while
    /// Cursor is running (that batch file opens a real console window).</summary>
    private static void FocusCursor(string? workspace = null)
    {
        var hint = string.IsNullOrEmpty(workspace) ? null : Path.GetFileName(workspace.TrimEnd('\\', '/'));
        if (FocusProcessWindow("Cursor", hint)) return;

        // Cursor isn't open — launch the GUI exe directly (not the .cmd shim).
        var exe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "cursor", "Cursor.exe");
        if (!File.Exists(exe))
        {
            DebugLog("FocusCursor: Cursor.exe not found and no running window");
            return;
        }
        try
        {
            DebugLog($"FocusCursor: starting Cursor.exe workspace={workspace ?? "nil"}");
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = string.IsNullOrEmpty(workspace) ? "" : $"\"{workspace}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            DebugLog($"FocusCursor: launch failed ({ex.Message})");
        }
    }

    /// <summary>Find a visible top-level window owned by a process named
    /// <paramref name="processName"/> and bring it to the foreground.
    /// When <paramref name="titleHint"/> is set, prefer a window whose title
    /// contains that substring (case-insensitive).</summary>
    private static bool FocusProcessWindow(string processName, string? titleHint = null)
    {
        nint preferred = 0, fallback = 0;
        foreach (var proc in Process.GetProcessesByName(processName))
        {
            try
            {
                var hwnd = FindVisibleWindow(proc.Id);
                if (hwnd == 0) continue;
                if (fallback == 0) fallback = hwnd;
                if (string.IsNullOrEmpty(titleHint)) { preferred = hwnd; break; }
                var title = proc.MainWindowTitle;
                if (string.IsNullOrEmpty(title))
                {
                    var buf = new StringBuilder(512);
                    _ = GetWindowText(hwnd, buf, buf.Capacity);
                    title = buf.ToString();
                }
                if (title.Contains(titleHint, StringComparison.OrdinalIgnoreCase))
                {
                    preferred = hwnd;
                    break;
                }
            }
            catch { /* process exited */ }
        }
        var target = preferred != 0 ? preferred : fallback;
        if (target == 0)
        {
            DebugLog($"FocusProcessWindow: no visible window for {processName}");
            return false;
        }
        DebugLog($"FocusProcessWindow: raising {processName} hwnd={target} hint={titleHint ?? "nil"}");
        ShowWindow(target, SW_RESTORE);
        NativeMethods.SetForegroundWindow(target);
        return true;
    }

    // TEMPORARY diagnostic logging while debugging the herdr-focus path — remove once confirmed working.
    private static readonly string DebugLogPath = Path.Combine(Path.GetTempPath(), "agentnotch-debug.log");
    private static void DebugLog(string msg)
    {
        try { File.AppendAllText(DebugLogPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); } catch { }
    }

    /// <summary>Looks up <paramref name="session"/> in `herdr agent list`
    /// (matching on the session id the existing herdr-agent-state.ps1 hook
    /// already reports) and, on a hit, calls `herdr agent focus &lt;terminal_id&gt;`
    /// — that only selects the pane inside herdr; the OS host window (Termius)
    /// is raised separately by <see cref="FocusProcessWindow"/>.</summary>
    private static bool TryHerdrFocus(AgentSession session)
    {
        try
        {
            var herdr = ResolveHerdrExe();
            if (herdr is null) { DebugLog("TryHerdrFocus: herdr executable not found"); return false; }

            if (!string.IsNullOrEmpty(session.HerdrTerminalId))
            {
                DebugLog($"TryHerdrFocus: direct terminal_id={session.HerdrTerminalId}");
                return RunHerdrFocus(herdr, session.HerdrTerminalId);
            }

            var herdrTerm = session.Id.StartsWith("herdr:", StringComparison.Ordinal)
                ? session.Id["herdr:".Length..]
                : null;
            var sessionId = HerdrSessionLookupId(session);
            DebugLog($"TryHerdrFocus: sessionId={sessionId} herdrTerm={herdrTerm}");
            if (string.IsNullOrEmpty(sessionId) && string.IsNullOrEmpty(herdrTerm)) return false;

            using var list = Process.Start(new ProcessStartInfo
            {
                FileName = herdr,
                Arguments = "agent list",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (list is null) { DebugLog("TryHerdrFocus: Process.Start returned null"); return false; }
            var json = list.StandardOutput.ReadToEnd();
            if (!list.WaitForExit(2000)) { DebugLog("TryHerdrFocus: herdr agent list timed out"); return false; }
            DebugLog($"TryHerdrFocus: herdr agent list exitCode={list.ExitCode} json={json}");

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("agents", out var agents))
            {
                DebugLog("TryHerdrFocus: no result.agents in JSON");
                return false;
            }

            foreach (var agent in agents.EnumerateArray())
            {
                if (!agent.TryGetProperty("terminal_id", out var termId)) continue;
                var term = termId.GetString();
                var value = agent.TryGetProperty("agent_session", out var sess) &&
                            sess.TryGetProperty("value", out var val)
                    ? val.GetString()
                    : null;
                var hit = (!string.IsNullOrEmpty(sessionId) && value == sessionId)
                    || (!string.IsNullOrEmpty(herdrTerm) && term == herdrTerm);
                if (!hit) continue;

                DebugLog($"TryHerdrFocus: MATCH terminal_id={term}, focusing");
                return RunHerdrFocus(herdr, term!);
            }
            DebugLog("TryHerdrFocus: no matching agent_session.value found");
            return false;
        }
        catch (Exception ex)
        {
            DebugLog($"TryHerdrFocus: EXCEPTION {ex}");
            return false;
        }
    }

    /// <summary>Session id herdr stores in agent_session.value — Claude uses
    /// the transcript uuid; Codex uses the rollout thread id, not the long
    /// rollout-*.jsonl filename stem.</summary>
    private static string? HerdrSessionLookupId(AgentSession session)
    {
        if (session.Id.StartsWith("herdr:", StringComparison.Ordinal)) return null;
        return session.Kind switch
        {
            AgentKind.Claude => Path.GetFileNameWithoutExtension(session.Id),
            AgentKind.Codex when !string.IsNullOrEmpty(session.ThreadId) => session.ThreadId,
            _ when session.Id.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                => Path.GetFileNameWithoutExtension(session.Id),
            _ => string.IsNullOrEmpty(session.ThreadId) ? null : session.ThreadId,
        };
    }

    private static bool RunHerdrFocus(string herdr, string terminalId)
    {
        using var focus = Process.Start(new ProcessStartInfo
        {
            FileName = herdr,
            Arguments = $"agent focus {terminalId}",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (focus is null) { DebugLog("RunHerdrFocus: Process.Start returned null"); return false; }
        if (!focus.WaitForExit(2000)) { DebugLog("RunHerdrFocus: timed out"); return false; }
        DebugLog($"RunHerdrFocus: exitCode={focus.ExitCode}");
        return focus.ExitCode == 0;
    }

    private static string? ResolveHerdrExe()
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Herdr", "bin", "herdr.exe");
        if (File.Exists(local)) return local;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(dir, "herdr.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Brings the terminal/IDE hosting <paramref name="pid"/> to the
    /// foreground. Tries the process's own ancestry first — walking up past
    /// pty helpers (winpty-agent, conhost, ...) to the first ancestor that
    /// owns a real top-level window — because hosts like Termius run their
    /// own pty (winpty) inside an Electron app with no window of its own on
    /// the console object AttachConsole would find; a plain conhost/ConPTY
    /// host (Windows Terminal, cmd) is caught by the same walk since
    /// conhost's own window IS the visible terminal there. Falls back to
    /// AttachConsole+GetConsoleWindow for any host the walk doesn't find.
    /// Session managers like herdr that sit between the shell and its host
    /// as their own orphaned-parent process break this walk entirely — see
    /// <see cref="FocusSession"/>, which tries herdr's own API first.</summary>
    public static void FocusConsole(int pid)
    {
        if (FocusOwningWindow(pid)) return;
        try
        {
            if (!AttachConsole((uint)pid)) return;
            try
            {
                var hwnd = GetConsoleWindow();
                if (hwnd != 0)
                {
                    ShowWindow(hwnd, SW_RESTORE);
                    NativeMethods.SetForegroundWindow(hwnd);
                }
            }
            finally { FreeConsole(); }
        }
        catch
        {
            // best effort — no console attached, or the process just exited
        }
    }

    private static bool FocusOwningWindow(int pid)
    {
        var walk = pid;
        for (var hop = 0; hop < 20 && walk != 0; hop++)
        {
            var hwnd = FindVisibleWindow(walk);
            if (hwnd != 0)
            {
                ShowWindow(hwnd, SW_RESTORE);
                NativeMethods.SetForegroundWindow(hwnd);
                return true;
            }
            var parent = GetParentPid(walk);
            walk = parent == walk ? 0 : parent;
        }
        return false;
    }

    /// <summary>First visible, titled top-level window owned by <paramref name="pid"/>
    /// — pty/helper processes (winpty-agent, herdr, conhost hidden by winpty)
    /// don't have one, so this naturally skips past them during the walk.</summary>
    private static nint FindVisibleWindow(int pid)
    {
        nint found = 0;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var wpid);
            if (wpid == pid && NativeMethods.IsWindowVisible(hwnd) && NativeMethods.GetWindowTextLength(hwnd) > 0)
            {
                found = hwnd;
                return false;
            }
            return true;
        }, 0);
        return found;
    }

    private static int GetParentPid(int pid)
    {
        const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        nint hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == 0) return 0;
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            int status = NtQueryInformationProcess(hProcess, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
            return status == 0 ? (int)pbi.InheritedFromUniqueProcessId : 0;
        }
        catch
        {
            return 0;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    /// <summary>Injects the numeric choice (and Enter) for a pending
    /// permission prompt directly into the session's console input buffer —
    /// the same effect as the user typing it themselves, works regardless of
    /// which terminal hosts the process.</summary>
    public static void SendApprovalKey(int pid, char digit)
    {
        try
        {
            if (!AttachConsole((uint)pid)) return;
            try
            {
                var hIn = GetStdHandle(STD_INPUT_HANDLE);
                if (hIn == 0 || hIn == -1) return;
                var records = new[]
                {
                    KeyRecord(true, digit), KeyRecord(false, digit),
                    KeyRecord(true, '\r'), KeyRecord(false, '\r'),
                };
                WriteConsoleInput(hIn, records, (uint)records.Length, out _);
            }
            finally { FreeConsole(); }
        }
        catch
        {
            // best effort — no console attached, or the process just exited
        }
    }

    private static INPUT_RECORD KeyRecord(bool down, char ch) => new()
    {
        EventType = KEY_EVENT,
        KeyEvent = new KEY_EVENT_RECORD
        {
            bKeyDown = down,
            wRepeatCount = 1,
            wVirtualKeyCode = (ushort)char.ToUpperInvariant(ch),
            wVirtualScanCode = 0,
            UnicodeChar = ch,
            dwControlKeyState = 0,
        },
    };

    /// <summary>For finished sessions: prefer raising the known host app
    /// (Cursor / Termius) when it is already open. Only fall back to spawning
    /// a new Windows Terminal resume when that host is not running.</summary>
    public static void LaunchResume(AgentSession session)
    {
        DebugLog($"LaunchResume: title={session.Title} kind={session.Kind} cwd={session.Cwd ?? "nil"}");
        if (session.Kind == AgentKind.Cursor)
        {
            FocusCursor(session.Cwd);
            return;
        }
        if (session.Kind == AgentKind.Claude)
        {
            if (FocusProcessWindow("Termius", session.Title)) return;
            // Termius not open — resume in a new terminal below.
        }
        if (string.IsNullOrEmpty(session.Cwd)) return;
        if (session.Kind == AgentKind.Other) return;
        var cmd = session.Kind == AgentKind.Claude
            ? $"claude --resume={Path.GetFileNameWithoutExtension(session.Id)}"
            : $"codex resume {session.ThreadId}";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "wt.exe",
                Arguments = $"-d \"{session.Cwd}\" cmd /k {cmd}",
                UseShellExecute = true,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k {cmd}",
                WorkingDirectory = session.Cwd,
                UseShellExecute = true,
            });
        }
    }

    private const int SW_RESTORE = 9;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    private const int STD_INPUT_HANDLE = -10;
    private const ushort KEY_EVENT = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteConsoleInput(nint hConsoleInput, INPUT_RECORD[] lpBuffer,
        uint nLength, out uint lpNumberOfEventsWritten);

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEY_EVENT_RECORD
    {
        [MarshalAs(UnmanagedType.Bool)] public bool bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char UnicodeChar;
        public uint dwControlKeyState;
    }

    [GeneratedRegex("--resume[= ]([0-9a-fA-F-]{8,})")]
    private static partial Regex ResumeIdRegex();

    // Electron helper processes of the Claude *desktop app* shell (renderer,
    // gpu-process, crashpad-handler, utility, ...) are children of Claude.exe
    // and always carry a --type= flag; the shell's own top-level process
    // lives under WindowsApps\Claude_... with no flags at all. Neither is an
    // agent session — the real embedded CLI runs from
    // AppData\Roaming\Claude\claude-code\<version>\claude.exe.
    private static bool IsClaudeAgent(string exePath, string cmd)
    {
        if (cmd.Contains("--type=", StringComparison.OrdinalIgnoreCase)) return false;
        if (exePath.Contains(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    // Cursor's ChatGPT extension bundles its own codex.exe as a headless
    // "app-server" language-server process under .cursor\extensions — not an
    // interactive session to surface.
    private static bool IsCodexAgent(string exePath, string cmd)
    {
        if (cmd.Contains("app-server", StringComparison.OrdinalIgnoreCase)) return false;
        if (exePath.Contains(@"\.cursor\extensions\", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>Encode a Windows path the way Claude Code names its project dir under ~/.claude/projects.</summary>
    public static string EncodeProjectDir(string cwd) =>
        cwd.Replace(':', '-').Replace('\\', '-').Replace('/', '-');

    private static string? GetProcessCurrentDirectory(int pid)
    {
        const int PROCESS_QUERY_INFORMATION = 0x0400;
        const int PROCESS_VM_READ = 0x0010;
        nint hProcess = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (hProcess == 0) return null;
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            int status = NtQueryInformationProcess(hProcess, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
            if (status != 0 || pbi.PebBaseAddress == 0) return null;

            // PEB -> ProcessParameters pointer sits at offset 0x20 on 64-bit Windows.
            nint processParams = ReadPointer(hProcess, pbi.PebBaseAddress + 0x20);
            if (processParams == 0) return null;

            // RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath (UNICODE_STRING) sits at offset 0x38.
            nint curDirStruct = processParams + 0x38;
            if (!ReadBytes(hProcess, curDirStruct, 16, out byte[] unicodeString)) return null;
            ushort length = BitConverter.ToUInt16(unicodeString, 0);
            nint bufferPtr = (nint)BitConverter.ToInt64(unicodeString, 8);
            if (length == 0 || bufferPtr == 0) return null;
            if (!ReadBytes(hProcess, bufferPtr, length, out byte[] pathBytes)) return null;
            var path = Encoding.Unicode.GetString(pathBytes).TrimEnd('\\', '\0');
            return path.Length > 0 ? path : null;
        }
        catch
        {
            // 32-bit/64-bit bitness mismatch, access denied, or a process that
            // exited mid-read — degrade to "unknown cwd" rather than crash.
            return null;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    private static nint ReadPointer(nint hProcess, nint address)
    {
        return ReadBytes(hProcess, address, 8, out byte[] bytes) ? (nint)BitConverter.ToInt64(bytes, 0) : 0;
    }

    private static bool ReadBytes(nint hProcess, nint address, int count, out byte[] bytes)
    {
        bytes = new byte[count];
        return ReadProcessMemory(hProcess, address, bytes, count, out nint read) && (int)read == count;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint processHandle, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint hProcess, nint lpBaseAddress, byte[] lpBuffer, int dwSize, out nint lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);
}
