using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Text.Json;

namespace AgentNotch.Discovery;

/// <summary>One local service the notch panel can show. Up/down plus a short
/// summary and an optional detail line.</summary>
public sealed record ServiceStatus(string Id, string Name, bool Up, string Summary, string? Detail);

/// <summary>Checks the fixed set of local services this machine actually runs.
/// HTTP probes share an 800ms timeout. The scheduled-task, Tailscale, and
/// Docker storage checks are cached so the 3s panel rescan does not shell
/// out every tick.</summary>
public sealed class ServiceProbe
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(800) };
    private const int StorageWarnFreeGb = 15;
    private const int StorageCriticalFreeGb = 5;

    private DateTime _taskCheckedAt = DateTime.MinValue;
    private ServiceStatus? _taskCached;

    private DateTime _tailscaleCheckedAt = DateTime.MinValue;
    private ServiceStatus? _tailscaleCached;

    private DateTime _storageCheckedAt = DateTime.MinValue;
    private ServiceStatus? _storageCached;
    private ServiceStatus? _dockerPruneCached;

    public IReadOnlyList<ServiceStatus> Probe()
    {
        var procs = ReadProcesses();
        var aether = Get("http://127.0.0.1:3000/health");
        var clicky = Get("http://127.0.0.1:40002/health");
        var odysseus = Get("http://127.0.0.1:7000/api/health");
        var ollama = Get("http://127.0.0.1:11434/api/version");
        var screenpipe = Get("http://127.0.0.1:3030/health");
        var (storage, dockerPrune) = StoragePair();

        return
        [
            storage,
            dockerPrune,
            Aether(aether, procs),
            AetherWatchdog(),
            Clicky(clicky, procs),
            HandoffRelay(procs),
            Odysseus(odysseus),
            Ollama(ollama, procs),
            Screenpipe(screenpipe, procs),
            Tailscale(),
        ];
    }

    private static ServiceStatus Aether((bool Ok, string Body) health, List<Proc> procs)
    {
        var node = FindAetherNode(procs);
        var pid = node is null ? null : $"pid {node.Pid}";
        if (health.Ok)
        {
            var detail = Join(pid, Squash(health.Body));
            return new ServiceStatus("aether", "Aether", true, "ok", detail);
        }
        var down = node is null ? health.Body : Join(pid, "process alive, health failed", health.Body);
        return new ServiceStatus("aether", "Aether", false, "down", down);
    }

    private ServiceStatus AetherWatchdog()
    {
        if (_taskCached is not null && DateTime.UtcNow - _taskCheckedAt < TimeSpan.FromSeconds(60))
            return _taskCached;

        _taskCheckedAt = DateTime.UtcNow;
        _taskCached = QueryAetherTask();
        return _taskCached;
    }

    private static ServiceStatus QueryAetherTask()
    {
        try
        {
            var text = Run("schtasks.exe", "/Query /TN AetherServer /FO LIST", 2500);
            if (string.IsNullOrWhiteSpace(text) || text.Contains("ERROR:", StringComparison.OrdinalIgnoreCase))
                return new ServiceStatus("aether-watchdog", "Aether watchdog", false, "missing", Squash(text));

            var status = Field(text, "Status");
            var next = Field(text, "Next Run Time");
            var last = Field(text, "Last Result");
            var armed = status.Equals("Ready", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Running", StringComparison.OrdinalIgnoreCase);
            var summary = status.Equals("Running", StringComparison.OrdinalIgnoreCase) ? "running" : armed ? "armed" : "off";
            var lastText = int.TryParse(last, out var code) && code != 0 ? $"last 0x{code:X}" : "last 0";
            return new ServiceStatus("aether-watchdog", "Aether watchdog", armed, summary, Join(string.IsNullOrEmpty(next) ? null : "next " + next, lastText));
        }
        catch (Exception ex)
        {
            return new ServiceStatus("aether-watchdog", "Aether watchdog", false, "error", ex.Message);
        }
    }

    private static ServiceStatus Clicky((bool Ok, string Body) health, List<Proc> procs)
    {
        var app = procs.FirstOrDefault(p =>
            p.Name.Equals("ClickyWindows.exe", StringComparison.OrdinalIgnoreCase));
        var worker = procs.FirstOrDefault(p =>
            (p.Name.Equals("python.exe", StringComparison.OrdinalIgnoreCase)
             || p.Name.Equals("pythonw.exe", StringComparison.OrdinalIgnoreCase))
            && p.Cmd.Contains("clicky_worker_api", StringComparison.OrdinalIgnoreCase));
        var pid = app is null ? null : $"pid {app.Pid}";
        var workerPid = worker is null ? null : $"worker pid {worker.Pid}";

        string? ModeOrStatus()
        {
            try
            {
                using var doc = JsonDocument.Parse(health.Body);
                if (doc.RootElement.TryGetProperty("chat_mode", out var mode)
                    && mode.GetString() is { Length: > 0 } m)
                    return m;
                if (doc.RootElement.TryGetProperty("status", out var st)
                    && st.GetString() is { Length: > 0 } s)
                    return s;
            }
            catch { /* fall through */ }
            return null;
        }

        if (app is not null)
        {
            if (health.Ok)
            {
                var summary = ModeOrStatus() ?? ":40002";
                return new ServiceStatus("clicky", "Clicky", true, summary,
                    Join(pid, workerPid, Squash(health.Body)));
            }
            return new ServiceStatus("clicky", "Clicky", true, "overlay",
                Join(pid, "worker :40002 closed", health.Body));
        }

        if (health.Ok)
            return new ServiceStatus("clicky", "Clicky", false, "no overlay",
                Join("worker up, overlay not running", workerPid, ModeOrStatus(), Squash(health.Body)));

        return new ServiceStatus("clicky", "Clicky", false, "down",
            Join(worker is null ? null : "worker alive, health failed", workerPid, health.Body));
    }

    private static ServiceStatus HandoffRelay(List<Proc> procs)
    {
        var relay = procs.FirstOrDefault(p =>
            (p.Name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
             || p.Name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase))
            && p.Cmd.Contains("handoff-relay-watcher.ps1", StringComparison.OrdinalIgnoreCase));
        if (relay is null)
            return new ServiceStatus("handoff-relay", "Handoff relay", false, "stopped", "watcher process is not running");
        return new ServiceStatus("handoff-relay", "Handoff relay", true, "running", $"pid {relay.Pid} · every 60s");
    }

    private static ServiceStatus Odysseus((bool Ok, string Body) health)
    {
        if (health.Ok)
            return new ServiceStatus("odysseus", "Odysseus", true, ":7000", Squash(health.Body));
        return new ServiceStatus("odysseus", "Odysseus", false, "down", health.Body);
    }

    private static ServiceStatus Ollama((bool Ok, string Body) health, List<Proc> procs)
    {
        var running = procs.Any(p => p.Name.Equals("ollama.exe", StringComparison.OrdinalIgnoreCase));
        if (health.Ok)
        {
            var version = "ok";
            try
            {
                using var doc = JsonDocument.Parse(health.Body);
                if (doc.RootElement.TryGetProperty("version", out var v) && v.GetString() is { Length: > 0 } s)
                    version = s;
            }
            catch { /* body is still shown as detail */ }
            return new ServiceStatus("ollama", "Ollama", true, version, Squash(health.Body));
        }
        var detail = running ? "process alive, port 11434 closed" : health.Body;
        return new ServiceStatus("ollama", "Ollama", false, "down", detail);
    }

    private static ServiceStatus Screenpipe((bool Ok, string Body) health, List<Proc> procs)
    {
        var proc = procs.FirstOrDefault(p => p.Name.Equals("screenpipe.exe", StringComparison.OrdinalIgnoreCase));
        var pid = proc is null ? null : $"pid {proc.Pid}";
        if (health.Ok)
        {
            var summary = ":3030";
            try
            {
                using var doc = JsonDocument.Parse(health.Body);
                if (doc.RootElement.TryGetProperty("status", out var st) && st.GetString() is { Length: > 0 } s)
                    summary = s;
            }
            catch { /* detail still shows the body */ }
            return new ServiceStatus("screenpipe", "Screenpipe", true, summary, Join(pid, Squash(health.Body)));
        }
        var down = proc is null ? health.Body : Join(pid, "process alive, port 3030 closed", health.Body);
        return new ServiceStatus("screenpipe", "Screenpipe", false, "down", down);
    }

    private (ServiceStatus Storage, ServiceStatus DockerPrune) StoragePair()
    {
        if (_storageCached is not null
            && _dockerPruneCached is not null
            && DateTime.UtcNow - _storageCheckedAt < TimeSpan.FromSeconds(30))
            return (_storageCached, _dockerPruneCached);

        _storageCheckedAt = DateTime.UtcNow;
        var snap = ReadStorageSnapshot();
        var task = QueryDockerPruneTask();
        _storageCached = BuildStorageStatus(snap, task);
        _dockerPruneCached = BuildDockerPruneStatus(snap, task);
        return (_storageCached, _dockerPruneCached);
    }

    private static ServiceStatus BuildStorageStatus(StorageSnapshot snap, DockerPruneTaskInfo task)
    {
        var free = snap.CFreeGb;
        var total = snap.CTotalGb;
        var pctFree = total > 0 ? free / total * 100.0 : 0;
        var up = free >= StorageCriticalFreeGb && (snap.DockerOk || snap.DockerError is null);
        var summary = free < StorageCriticalFreeGb ? $"{FmtGb(free)} crit"
            : free < StorageWarnFreeGb ? $"{FmtGb(free)} low"
            : $"{FmtGb(free)} free";

        var detail = Join(
            total > 0 ? $"C {FmtGb(free)}/{FmtGb(total)} ({pctFree:0}%)" : null,
            snap.DockerOk
                ? Join(
                    snap.ImagesGb is double img ? $"img {FmtGb(img)}" : null,
                    snap.ReclaimableGb is double rec && rec > 0.05 ? $"reclaim {FmtGb(rec)}" : "reclaim 0",
                    snap.BuildCacheGb is double bc && bc > 0.05 ? $"cache {FmtGb(bc)}" : null,
                    snap.VolumesGb is double vol && vol > 0.05 ? $"vol {FmtGb(vol)}" : null)
                : (snap.DockerError is { Length: > 0 } err ? "docker " + Squash(err) : "docker down"),
            snap.VhdxGb is double vhdx ? $"vhdx {FmtGb(vhdx)}" : null,
            AgeLabel("pruned", snap.LastPruneUtc),
            task.NextRun is { Length: > 0 } next ? "next " + next : null);

        return new ServiceStatus("storage", "Storage", up, summary, detail);
    }

    private static ServiceStatus BuildDockerPruneStatus(StorageSnapshot snap, DockerPruneTaskInfo task)
    {
        if (!task.Exists)
            return new ServiceStatus("docker-prune", "Docker prune", false, "missing",
                "run scripts/install-docker-storage-task.ps1 · weekly unused image/volume/cache prune");

        var armed = task.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase)
            || task.Status.Equals("Running", StringComparison.OrdinalIgnoreCase);
        var summary = task.Status.Equals("Running", StringComparison.OrdinalIgnoreCase) ? "running"
            : armed ? "armed" : "off";
        var lastOk = snap.LastPruneOk;
        var lastText = lastOk is null ? null
            : lastOk == true ? "last ok"
            : "last failed";
        var detail = Join(
            task.NextRun is { Length: > 0 } n ? "next " + n : null,
            AgeLabel("last", snap.LastPruneUtc),
            lastText,
            snap.LastReclaimed is { Length: > 0 } r ? Squash(r) : null,
            "every 7d · no vhdx compact");
        return new ServiceStatus("docker-prune", "Docker prune", armed && lastOk != false, summary, detail);
    }

    private static StorageSnapshot ReadStorageSnapshot()
    {
        var snap = new StorageSnapshot();
        try
        {
            var drive = new DriveInfo("C");
            if (drive.IsReady)
            {
                snap.CFreeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                snap.CTotalGb = drive.TotalSize / (1024.0 * 1024 * 1024);
            }
        }
        catch { /* leave zeros */ }

        var vhdx = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Docker", "wsl", "disk", "docker_data.vhdx");
        try
        {
            if (File.Exists(vhdx))
                snap.VhdxGb = new FileInfo(vhdx).Length / (1024.0 * 1024 * 1024);
        }
        catch { /* optional */ }

        try
        {
            var text = Run("docker", "system df", 8000);
            snap.DockerOk = true;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.StartsWith("Images", StringComparison.Ordinal))
                {
                    var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 5)
                    {
                        snap.ImagesGb = ParseDockerSizeGb(parts[3]);
                        snap.ReclaimableGb = ParseDockerSizeGb(parts[4]);
                    }
                }
                else if (line.StartsWith("Containers", StringComparison.Ordinal))
                {
                    var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4) snap.ContainersGb = ParseDockerSizeGb(parts[3]);
                }
                else if (line.StartsWith("Local Volumes", StringComparison.Ordinal))
                {
                    var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    // Local Volumes TOTAL ACTIVE SIZE RECLAIMABLE
                    if (parts.Length >= 5) snap.VolumesGb = ParseDockerSizeGb(parts[4]);
                }
                else if (line.StartsWith("Build Cache", StringComparison.Ordinal))
                {
                    var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    // Build Cache TOTAL ACTIVE SIZE RECLAIMABLE
                    if (parts.Length >= 5) snap.BuildCacheGb = ParseDockerSizeGb(parts[4]);
                }
            }
        }
        catch (Exception ex)
        {
            snap.DockerOk = false;
            snap.DockerError = ex.Message;
        }

        var statusPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "agent-notch", "storage.json");
        try
        {
            if (File.Exists(statusPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(statusPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("last_prune_utc", out var lp) && lp.GetString() is { Length: > 0 } lps
                    && DateTime.TryParse(lps, null, System.Globalization.DateTimeStyles.RoundtripKind, out var lpd))
                    snap.LastPruneUtc = lpd.ToUniversalTime();
                if (root.TryGetProperty("last_prune_ok", out var okEl)
                    && (okEl.ValueKind == JsonValueKind.True || okEl.ValueKind == JsonValueKind.False))
                    snap.LastPruneOk = okEl.GetBoolean();
                if (root.TryGetProperty("last_reclaimed", out var rec) && rec.GetString() is { Length: > 0 } rs)
                    snap.LastReclaimed = rs;
            }
        }
        catch { /* status file optional */ }

        return snap;
    }

    private static DockerPruneTaskInfo QueryDockerPruneTask()
    {
        try
        {
            var text = Run("schtasks.exe", "/Query /TN AgentNotchDockerPrune /FO LIST /V", 2500);
            if (string.IsNullOrWhiteSpace(text) || text.Contains("ERROR:", StringComparison.OrdinalIgnoreCase))
                return new DockerPruneTaskInfo(false, "", "", "");
            return new DockerPruneTaskInfo(
                true,
                Field(text, "Status"),
                Field(text, "Next Run Time"),
                Field(text, "Last Run Time"));
        }
        catch
        {
            return new DockerPruneTaskInfo(false, "", "", "");
        }
    }

    private static double? ParseDockerSizeGb(string token)
    {
        // docker prints reclaimable as "12.71GB" or "12.71GB (30%)" — take leading size.
        var t = token.Trim();
        var cut = t.IndexOf('(');
        if (cut > 0) t = t[..cut].Trim();
        if (t.Length < 2) return null;
        var unitStart = t.Length - 1;
        while (unitStart > 0 && !char.IsDigit(t[unitStart - 1]) && t[unitStart - 1] != '.')
            unitStart--;
        if (unitStart <= 0) return null;
        if (!double.TryParse(t[..unitStart], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
            return null;
        var unit = t[unitStart..].ToUpperInvariant();
        return unit switch
        {
            "B" => n / (1024.0 * 1024 * 1024),
            "KB" or "KIB" => n / (1024.0 * 1024),
            "MB" or "MIB" => n / 1024.0,
            "GB" or "GIB" => n,
            "TB" or "TIB" => n * 1024.0,
            _ => null,
        };
    }

    private static string FmtGb(double gb) =>
        gb >= 100 ? $"{gb:0}G" : gb >= 10 ? $"{gb:0.0}G" : $"{gb:0.00}G";

    private static string? AgeLabel(string prefix, DateTime? utc)
    {
        if (utc is null) return null;
        var age = DateTime.UtcNow - utc.Value;
        if (age.TotalMinutes < 90) return $"{prefix} {(int)age.TotalMinutes}m ago";
        if (age.TotalHours < 48) return $"{prefix} {(int)age.TotalHours}h ago";
        return $"{prefix} {(int)age.TotalDays}d ago";
    }

    private ServiceStatus Tailscale()
    {
        if (_tailscaleCached is not null && DateTime.UtcNow - _tailscaleCheckedAt < TimeSpan.FromSeconds(20))
            return _tailscaleCached;

        _tailscaleCheckedAt = DateTime.UtcNow;
        _tailscaleCached = QueryTailscale();
        return _tailscaleCached;
    }

    private static ServiceStatus QueryTailscale()
    {
        var exe = TailscaleExe();
        if (exe is null)
            return new ServiceStatus("tailscale", "Tailscale", false, "missing", "tailscale.exe not found");
        try
        {
            var json = Run(exe, "status --json", 2500);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var state = root.TryGetProperty("BackendState", out var st) ? st.GetString() ?? "" : "";
            var dns = "";
            if (root.TryGetProperty("Self", out var self) && self.TryGetProperty("DNSName", out var name))
                dns = (name.GetString() ?? "").TrimEnd('.');
            var suffix = root.TryGetProperty("MagicDNSSuffix", out var mag) ? mag.GetString() ?? "" : "";
            var up = state.Equals("Running", StringComparison.OrdinalIgnoreCase);
            var summary = up && dns.Length > 0 ? "magicdns" : up ? "up" : (state.Length > 0 ? state.ToLowerInvariant() : "down");
            var detail = Join(dns.Length > 0 ? dns : null, suffix.Length > 0 ? "suffix " + suffix : null, state.Length > 0 ? state : null);
            return new ServiceStatus("tailscale", "Tailscale", up, summary, detail);
        }
        catch (Exception ex)
        {
            return new ServiceStatus("tailscale", "Tailscale", false, "down", ex.Message);
        }
    }

    private static Proc? FindAetherNode(List<Proc> procs)
    {
        var parents = procs
            .Where(p => p.Name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
                && p.Cmd.Contains("aether-server", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Pid)
            .ToHashSet();
        return procs.FirstOrDefault(p =>
            p.Name.Equals("node.exe", StringComparison.OrdinalIgnoreCase)
            && !p.Cmd.Contains("cursor", StringComparison.OrdinalIgnoreCase)
            && (p.Cmd.Contains("Aether", StringComparison.OrdinalIgnoreCase)
                || (parents.Contains(p.Parent) && p.Cmd.Contains("server.js", StringComparison.OrdinalIgnoreCase))));
    }

    private static List<Proc> ReadProcesses()
    {
        var list = new List<Proc>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, Name, CommandLine FROM Win32_Process " +
                "WHERE Name='node.exe' OR Name='powershell.exe' OR Name='pwsh.exe' OR Name='cmd.exe' OR Name='ollama.exe' OR Name='screenpipe.exe' OR Name='ClickyWindows.exe' OR Name='python.exe' OR Name='pythonw.exe'");
            foreach (ManagementObject mo in searcher.Get())
            {
                list.Add(new Proc(
                    Convert.ToInt32(mo["ProcessId"]),
                    Convert.ToInt32(mo["ParentProcessId"] ?? 0),
                    (mo["Name"] as string) ?? "",
                    (mo["CommandLine"] as string) ?? ""));
            }
        }
        catch
        {
            // WMI can fail; HTTP checks still report.
        }
        return list;
    }

    private static (bool Ok, string Body) Get(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = Http.Send(req);
            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return (resp.IsSuccessStatusCode, string.IsNullOrWhiteSpace(body) ? ((int)resp.StatusCode).ToString() : Squash(body));
        }
        catch (Exception ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            if (ex is TaskCanceledException || msg.Contains("canceled", StringComparison.OrdinalIgnoreCase))
                return (false, "no response");
            return (false, msg);
        }
    }

    private static string Run(string file, string args, int timeoutMs)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        proc.Start();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException(file + " timed out");
        }
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
    }

    private static string? TailscaleExe()
    {
        var candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string Field(string text, string name)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var prefix = name + ":";
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return line[prefix.Length..].Trim();
        }
        return "";
    }

    private static string Squash(string text)
    {
        var one = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return one.Length <= 90 ? one : one[..90];
    }

    private static string? Join(params string?[] parts)
    {
        var kept = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        return kept.Length == 0 ? null : string.Join(" · ", kept);
    }

    private sealed record Proc(int Pid, int Parent, string Name, string Cmd);

    private sealed class StorageSnapshot
    {
        public double CFreeGb;
        public double CTotalGb;
        public double? VhdxGb;
        public bool DockerOk;
        public string? DockerError;
        public double? ImagesGb;
        public double? ReclaimableGb;
        public double? ContainersGb;
        public double? VolumesGb;
        public double? BuildCacheGb;
        public DateTime? LastPruneUtc;
        public bool? LastPruneOk;
        public string? LastReclaimed;
    }

    private sealed record DockerPruneTaskInfo(bool Exists, string Status, string NextRun, string LastRun);
}
