using System.IO;

namespace AgentNotch.Config;

/// <summary>Reads the pet choice from ~/.config/agent-notch/pet (same path shape as
/// the Mac build, kept for parity — `echo dewey > %USERPROFILE%\.config\agent-notch\pet`).</summary>
internal static class PetConfig
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "agent-notch", "pet");

    public static string CurrentPetId { get; private set; } = "codex";

    public static readonly string[] PetIds =
        ["codex", "dewey", "fireball", "rocky", "seedy", "stacky", "bsod", "null-signal"];

    public static readonly HashSet<string> KnownPets =
        new(PetIds, StringComparer.OrdinalIgnoreCase);

    public static void Refresh()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var id = File.ReadAllText(ConfigPath).Trim();
            if (id.Length > 0 && KnownPets.Contains(id)) CurrentPetId = id;
        }
        catch
        {
            // config file mid-write or briefly locked — keep the last good value
        }
    }

    public static void SetCurrentPet(string id)
    {
        if (!KnownPets.Contains(id)) return;
        CurrentPetId = id;
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(ConfigPath, id);
        }
        catch
        {
            // directory permissions or a locked file — in-memory value still applies
        }
    }

    // Known agent-host process names, used both to acknowledge finished
    // agents on activation (mirrors the Mac build's bundle-identifier
    // allowlist: Ghostty, Terminal, iTerm2, kitty, Warp, Alacritty) and as
    // a hint set for the click-to-focus window walk. Covers plain
    // terminals, Termius, and the IDEs/desktop apps this user runs agents
    // in — process names confirmed via each app's install/AppxManifest.
    public static readonly HashSet<string> KnownHostProcessNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "WindowsTerminal", "cmd", "powershell", "pwsh", "ConEmu64", "ConEmu",
            "Hyper", "WezTerm-gui", "mintty", "alacritty", "Tabby", "Termius",
            "Cursor", "Claude", "ChatGPT", "MiniMax Code",
        };
}
