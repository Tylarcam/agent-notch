using System.IO;
using System.Windows;
using AgentNotch.Discovery;
using AgentNotch.Dock;
using AgentNotch.Models;

namespace AgentNotch;

internal static class Program
{
    // Debug: `AgentNotch.exe --scan` prints one discovery + scan cycle and exits.
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--scan"))
        {
            RunScan();
            return;
        }
        if (args.Contains("--dock-preview"))
        {
            Environment.Exit(DockPreviewRunner.Run());
            return;
        }
        if (args.Contains("--focus"))
        {
            RunFocus();
            return;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var controller = new AppController();
        app.Exit += (_, _) => controller.Shutdown();
        app.Startup += (_, _) => controller.Start();
        app.Run();
    }

    private static void RunScan()
    {
        var discovery = new ProcessDiscovery();
        var snaps = discovery.LiveAgentProcesses();
        Console.WriteLine("== process discovery ==");
        foreach (var s in snaps)
            Console.WriteLine($"{s.Kind}: pid={s.Pid} cwd={s.Cwd ?? "nil"} resume={s.ResumeSessionId ?? "nil"}");

        Console.WriteLine("== services ==");
        foreach (var svc in new ServiceProbe().Probe())
            Console.WriteLine($"{(svc.Up ? "up" : "down")} {svc.Name} [{svc.Summary}] {svc.Detail}");

        var sessions = new SessionScanner().Scan(snaps);
        Console.WriteLine("== sessions ==");
        foreach (var s in sessions)
        {
            var age = (DateTime.UtcNow - s.LastModified).TotalSeconds;
            Console.WriteLine($"{s.Kind.DisplayName()} [{s.Title}] chat={s.ChatTitle} file={s.FileName} goal={s.Goal} live={s.IsLive} busy={s.IsBusy} herdr={s.HerdrTerminalId ?? "nil"} mtime=-{age:F0}s kids={s.Children.Count}");
        }
    }

    private static void RunFocus()
    {
        var discovery = new ProcessDiscovery();
        var snaps = discovery.LiveAgentProcesses();
        var sessions = new SessionScanner().Scan(snaps);
        var kindArg = Environment.GetCommandLineArgs().SkipWhile(a => a != "--focus").Skip(1).FirstOrDefault();
        AgentSession? target = null;
        if (!string.IsNullOrEmpty(kindArg))
        {
            target = sessions.FirstOrDefault(s =>
                s.Kind.ToString().Equals(kindArg, StringComparison.OrdinalIgnoreCase) ||
                s.Kind.DisplayName().Contains(kindArg, StringComparison.OrdinalIgnoreCase) ||
                s.Kind.ShortName().Equals(kindArg, StringComparison.OrdinalIgnoreCase));
        }
        target ??= sessions.FirstOrDefault(s => s.AnyLive || !string.IsNullOrEmpty(s.HerdrTerminalId))
            ?? sessions.FirstOrDefault(s => !string.IsNullOrEmpty(s.Cwd));
        if (target is null)
        {
            Console.WriteLine("no session to focus");
            return;
        }
        Console.WriteLine($"== focus {target.Kind} [{target.Title}] live={target.IsLive} herdr={target.HerdrTerminalId ?? "nil"} ==");
        ProcessDiscovery.FocusSession(target);
        var log = Path.Combine(Path.GetTempPath(), "agentnotch-debug.log");
        if (File.Exists(log))
            Console.WriteLine(File.ReadAllText(log).Split('\n').TakeLast(12).Aggregate((a, b) => a + "\n" + b));
    }
}
