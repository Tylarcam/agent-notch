# Agent Notch (Windows)

A from-scratch C#/WPF port of the Mac menu-bar app. Same idea — a mascot walks
beside a small top-of-screen bar while Claude Code or Codex is working, click
it for a session panel — rebuilt on Windows APIs since AppKit, `ps`, and
`lsof` don't exist here.

## What's different from the Mac version

Windows laptops have no notch, so the indicator sits in a small always-on-top
bar centered at the top of the primary screen instead. Liveness detection is
also necessarily different:

- **Process discovery** uses WMI (`Win32_Process`) to find `claude.exe` /
  `codex.exe` instead of `ps`.
- **Mapping a process to its session** has no Windows equivalent of `lsof`
  (enumerating a process's open file handles requires
  `NtQuerySystemInformation` + handle duplication, which can hang on pipes —
  deliberately skipped). Instead:
  - Claude Code launched with `--resume=<uuid>` (true of the build embedded
    in the Claude *desktop app* — its own OS-level working directory doesn't
    point at your project) is matched directly by that session id.
  - A plain terminal-launched `claude` is matched by its working directory,
    read via a PEB memory read (the same signal the Mac build falls back to
    for Claude anyway, since Claude Code doesn't keep its transcript file
    handle open even there).
  - Codex rollout files embed their own `cwd` in the first line, matched
    against the process's working directory the same way.
- **Terminal-focus acknowledgement** (revisiting a terminal clears a
  finished agent's green indicator) watches for Windows Terminal, `cmd`,
  PowerShell/`pwsh`, ConEmu, Hyper, WezTerm, `mintty`, Alacritty, and Tabby
  becoming the foreground window.
- Click-through and the click-to-expand panel are implemented with a
  low-level mouse hook (`WH_MOUSE_LL`) plus `WS_EX_LAYERED | WS_EX_TRANSPARENT`,
  since routing real input to a borderless layered window is unreliable —
  same reasoning as the Mac build's global event monitor. **Esc** collapses
  the open panel or tucks the small hover tab via a matching keyboard hook
  (`WH_KEYBOARD_LL`) so windows behind it are clickable again.

## Build & run

Requires the [.NET SDK](https://dotnet.microsoft.com/download) (10.0+) and
Windows.

```powershell
cd windows/AgentNotch
dotnet run
```

Or from any directory, if you've installed the global shim
(`%USERPROFILE%\.local\bin\notch.cmd`, which is already on PATH when
`~/.local/bin` is):

```powershell
notch
```

That runs `dotnet run` in the **current** terminal (no new window). Pass
flags through the same way: `notch --scan`, `notch --focus`, etc.

```powershell
notch stop
```

Stops the running overlay (`AgentNotch.exe`) from any terminal.

Debug mode: `dotnet run -- --scan` (or `notch --scan`) prints one discovery
+ transcript-scan cycle and exits, without opening any windows.

### Right-edge dock (Phase 1)

The dock is additive: a collapsible pill on the middle of the right screen
edge, avatar rail, and chat panel styled from `docs/dock-design/`. The top
mascot bar and session panel are unchanged.

```powershell
cd windows/AgentNotch
dotnet run
```

- Click the edge pill to open the rail; click an avatar (or the pill again)
  to open chat. **Esc** or click outside collapses chat → rail → pill.
- Odysseus chat uses a relay when configured
  (`%USERPROFILE%\.config\agent-notch\relay.json` with `{ "baseUrl": "http://127.0.0.1:7000" }`
  or env `AGENT_NOTCH_RELAY_URL`). Otherwise messages stay local with a quiet
  “relay offline” label.
- Claude permission prompts still flow through `hooks/notify.ps1` → approval
  cards in the agent’s chat thread (panel quick-approve remains as fallback).

**Preview / screenshots** (no live sessions required):

```powershell
dotnet run -- --dock-preview
# or: notch --dock-preview
```

Writes PNGs under `docs/screens/dock/` (`01-collapsed` … `05-approval-pending`)
via off-screen render, then exits.

## Publish a standalone build

```powershell
cd windows/AgentNotch
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
```

Produces a single `publish/AgentNotch.exe` (plus a handful of native WPF
DLLs it needs alongside it) that runs on a machine without the .NET SDK
installed. Pet spritesheets are compiled in as WPF resources, so nothing
else needs to ship alongside it.

## Start at login

Press `Win+R`, run `shell:startup`, and drop a shortcut to `AgentNotch.exe`
(or `publish\AgentNotch.exe`) in the folder that opens.

## Codex pet

Same mechanism as the Mac build:

```powershell
"dewey" | Out-File -Encoding ascii "$env:USERPROFILE\.config\agent-notch\pet"
```

Options: `codex`, `dewey`, `fireball`, `rocky`, `seedy`, `stacky`, `bsod`,
`null-signal`. Takes effect within a few seconds, no restart needed.

## Docker storage (Windows)

Weekly unused-image / volume / build-cache prune (no VHDX compact), plus live
Storage + Docker prune rows in the Systems panel.

```powershell
cd windows/AgentNotch
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-docker-storage-task.ps1
```

That registers `AgentNotchDockerPrune` (Tue 12:30 weekly) and writes
`%USERPROFILE%\.config\agent-notch\storage.json`. Open the notch panel →
Systems to see C: free space, Docker image/reclaim/cache/VHDX sizes, last prune,
and next run. Manual prune: `.\scripts\docker-storage.ps1 -Prune`.

## Known limitations

- Codex matching is implemented analogously to Claude's but hasn't been
  validated against a live Codex CLI session (none was running on the
  machine this was built on) — worth a sanity check with `--scan` the first
  time you run Codex from a terminal.
- Liveness detection is coarser than the Mac build's exact lsof-based
  mapping when several Claude processes share the same working directory
  without a `--resume` id (falls back to "N processes, N newest
  transcripts," same as Claude Code's own Mac fallback).
- Multi-monitor setups: the bar always anchors to the primary monitor.
