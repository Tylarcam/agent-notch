using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentNotch.Models;
using AgentNotch.Relay;

namespace AgentNotch.Dock;

internal static class DockPreviewRunner
{
    private static string ResolveOutDir()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "..", "docs", "screens", "dock")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "docs", "screens", "dock")),
            Path.Combine(@"C:\Users\tylar\code\agent-notch\docs\screens\dock"),
        };
        foreach (var c in candidates)
        {
            try
            {
                Directory.CreateDirectory(c);
                return c;
            }
            catch { /* try next */ }
        }
        return candidates[^1];
    }

    [STAThread]
    public static int Run()
    {
        var outDir = ResolveOutDir();
        Directory.CreateDirectory(outDir);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var controller = new DockController(previewDemoRelay: true);
        var sessions = new List<AgentSession>
        {
            new()
            {
                Id = "claude-preview",
                Kind = AgentKind.Claude,
                Title = "feat/dock-drawer",
                LastModified = DateTime.UtcNow,
                IsLive = true,
                LastActivity = DateTime.UtcNow,
            },
            new()
            {
                Id = "cursor-preview",
                Kind = AgentKind.Cursor,
                Title = "Composer",
                LastModified = DateTime.UtcNow,
                IsLive = true,
            },
        };
        controller.UpdateSessions(sessions);
        controller.Start();

        // 01 collapsed
        controller.ForceStateForPreview(DockSurfaceState.Collapsed);
        Pump();
        SaveCapture(controller.Pill, Path.Combine(outDir, "01-collapsed.png"), (int)DockTokens.PillHitW, (int)DockTokens.CollapsedH);

        // 02 rail
        controller.ForceStateForPreview(DockSurfaceState.Rail, "odysseus");
        Pump();
        SaveCapture(controller.Rail, Path.Combine(outDir, "02-rail-open.png"), (int)DockTokens.RailW, (int)DockTokens.RailH);

        // 03 chat
        controller.ForceStateForPreview(DockSurfaceState.Chat, "odysseus");
        Pump();
        SaveCapture(controller.Chat, Path.Combine(outDir, "03-chat-open.png"), (int)DockTokens.ChatW, (int)DockTokens.ChatH);

        // 04 typing + short assistant line, then the typing indicator
        controller.ForceStateForPreview(DockSurfaceState.Chat, "odysseus");
        controller.Chat.InjectPreviewContent(["Reading the window behind the dock."], "What's going on here?");
        Pump();
        SaveCapture(controller.Chat, Path.Combine(outDir, "04-typing-streaming.png"), (int)DockTokens.ChatW, (int)DockTokens.ChatH);

        // 05 approval — use the seed helper so the Claude rail item and card stay in sync
        controller.SeedPreviewApproval();
        Pump();
        SaveCapture(controller.Chat, Path.Combine(outDir, "05-approval-pending.png"), (int)DockTokens.ChatW, (int)DockTokens.ChatH);

        controller.Shutdown();
        app.Shutdown();
        Console.WriteLine($"Dock preview PNGs written to {outDir}");
        return 0;
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            Application.Current?.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            Thread.Sleep(80);
        }
    }

    private static void SaveCapture(Window window, string path, int w, int h)
    {
        window.Dispatcher.Invoke(() =>
        {
            window.Width = w;
            window.Height = h;
            window.Measure(new Size(w, h));
            window.Arrange(new Rect(0, 0, w, h));
            window.UpdateLayout();
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(window);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            enc.Save(fs);
        });
    }
}
