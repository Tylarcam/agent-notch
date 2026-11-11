# Phase 1: agent-notch side dock (Windows)

You are implementing this in the existing Windows C#/WPF port at `windows/AgentNotch/`. This is an upgrade of the current agent-notch build, not a rewrite and not a from-scratch app.

## Repo state (important)

- Branch: `feat/dock-drawer` (already created, based on `main` at `443c0f5`).
- Working tree was already dirty when this branch was created. The untracked `windows/` tree IS the current Windows agent-notch build — build on it.
- Do NOT stash, discard, revert, or overwrite unrelated uncommitted work: `main.swift`, `openspec/`, `docs/dev-wiki.html`, `docs/screens/`. You may update `README.md` and `.gitignore` only as needed for the dock.
- Do NOT commit, push, merge, tag, or amend. The operator will commit after reviewing.
- Do NOT restart Odysseus or any other long-running service.
- Do NOT read, print, copy, or commit secrets, API keys, or `.env` files (including `~/.chums/grokbot.env` and any Odysseus env).
- Do NOT install global tools or SDKs. The .NET 10 SDK is already present. `dotnet --version` is 10.0.102.

## Design sources (follow exactly)

Read these first, in order:

1. `docs/dock-design/SPEC.md` — dimensions, color, type, motion, chat structure, approval card, rail status.
2. `docs/dock-design/tokens.json` and `docs/dock-design/tokens.css` — source of truth for values.
3. `docs/dock-design/FEATURES.md` — feature list.
4. Annotated mockups in `docs/dock-design/annotated/` (collapsed, rail-open, chat-open, typing-streaming, rich-reply, approval, overview) and HTML sources in `docs/dock-design/html/`.
5. Font: `docs/dock-design/fonts/FiraCode-Variable.ttf` — embed and use this. No substitute UI font.

Where the clip and Odysseus disagree, use Odysseus tokens. Do not ship Storybox pink, traffic lights, the live desktop-preview blob, or solid `#101010` panels.

## Decided defaults for SPEC.md open questions

1. Keep the collapsed pill visual at ~10×196 px, but give it an invisible hit area of at least 24 px wide (44 px tall padding is fine; the visible pill stays ~10 px wide). Mid-right edge, flush, always-on-top over any window.
2. Chat panel opens with a 280 ms slide (`cubic-bezier(0.22, 1, 0.36, 1)`). Rail expands in 100 ms with the same easing.
3. Drop the clip's live desktop-preview blob. Header is: active bot avatar, name, gear, Share.
4. Rail lists every connected agent (Claude Code, Codex, Cursor, plus any others the existing scanner already finds). Odysseus is pinned at the top of the rail even if it has no live session. Active avatar gets a 2 px Odysseus teal ring (`#8bd9da`).
5. Approval payload row is context-appropriate: file path for edits, shell command for shell, MCP/tool name for tool/MCP prompts. Parse what you can from the existing `ApprovalRequest` / hook message; fall back to the raw message text.

## Phase 1 scope (build these)

1. **Edge pill + avatar rail + chat panel**
   - Collapsed: tiny vertical pill on the middle of the right screen edge.
   - Click/hover-open: 42×285 avatar rail slides out from the right (radius 21 on the left only, flush to the right).
   - Clicking an avatar (or a second click on the pill once the rail is open) opens a 428×601 chat panel 19 px to the left of the rail as a **separate surface** (desktop shows through the gap). Not one combined window.
   - Chat header, message list, composer (`+` on the left, `Message <bot>` placeholder, mic on the right; mic becomes a teal send arrow when there is text).
   - Assistant messages: full width, 2 px teal left rule, no boxed card.
   - User messages: right-aligned dark pill `#162022`, radius `18 18 6 18`, max-width 78%.
   - Send pop 180 ms spring; brief "Typing..." then streamed reply when a response arrives.
   - Esc or click-outside collapses chat, then rail, back to the pill. Existing Esc / click-through behavior must keep windows behind the overlay clickable when collapsed.
   - Use glass fill, blur, border, inset highlight, and shadows from the tokens. Respect reduced-motion (opacity-only / skip springs).

2. **Odysseus relay as message transport**
   - Implement a small client interface (e.g. `OdysseusRelay`) used by the chat panel to send user messages and receive assistant replies (including streamed tokens if the protocol supports it).
   - Look for an already-documented local/public Odysseus chat relay in this repo or obviously documented sibling trees under `C:\Users\tylar\code\odysseus*` / `agent-infrastructure` — **do not open .env files or print secrets**. Prefer a configurable base URL (env or `%USERPROFILE%\.config\agent-notch\relay.json` with no secrets in source).
   - If no usable endpoint is available, stub the client cleanly: in-memory / local transcript, a visible but quiet “relay offline” / disconnected state in the header or composer, and a fake streamed reply only in `--dock-preview` demo mode. Keep the interface ready to point at a real relay later.
   - Do not wire Aether voice in this phase. The mic and `+` buttons may be present in the composer chrome; they can be no-ops or “coming soon” besides the send-arrow swap. Do not implement the voice orb.

3. **Approve/Deny cards + pulsing notch**
   - When Claude Code or Cursor is waiting on a permission prompt, show an approval card in that agent’s chat thread (and pulse the collapsed pill).
   - Reuse and extend the existing hook/store: `windows/AgentNotch/hooks/notify.ps1`, `Discovery/ApprovalStore.cs`, `Models/AgentSession.PendingApproval`, and `AppController` approve/deny key sending.
   - Card states: pending (yellow `#D8CB03` edge, eyebrow “Pending · needs you”, title naming tool + action, payload row, Deny outline `#c45b5b`, Approve teal fill), approved (teal edge, receipt), denied (deny tint, receipt). 220 ms resolve transition.
   - Pending: collapsed notch pulses yellow on a 1200 ms period; that agent’s rail avatar gets a yellow status dot + soft 2 px ring.
   - Rail status dots (8 px, bottom-right, 1.5 px panel-colored halo): `idle` hidden; `busy` teal + soft glow; `needs_you` yellow; `waiting_approval` yellow + ring.
   - Extend detection to Cursor permission prompts if there is a practical hook or file signal; if not, keep Claude’s existing Notification hook and make Cursor cards work in preview/demo, with a clear stub comment for a future Cursor hook.
   - Keep the existing session panel’s approve/deny as a fallback if that is low-risk; do not break it. The dock card is the new primary approval UI.

4. **Keep existing session detection working**
   - Do not regress `ProcessDiscovery`, `SessionScanner`, the 3 s poll, busy/idle/done afterglow, mascots, or the existing top indicator / session panel.
   - The right-edge dock is additive in this phase (do not delete the top mascot bar). If input hooks conflict, integrate carefully so both overlays still work and click-through still works when the dock is collapsed.
   - Live rail status must be driven by the existing session list (plus Odysseus pinned on top).

Out of scope for this phase (do not build except as inert chrome): Aether voice orb, attaching a screenshot of the current window, rich Odysseus spend/Now/usage cards, Claude/Cursor start/stop hooks. The rich-reply mockup is design reference only — you do not need to generate SEO dashboards. A simple text reply (and the approval card) is enough.

## Implementation notes

- Target the Windows WPF app (`net10.0-windows`). Do not try to compile or run the macOS `main.swift` on this PC.
- Sizes in SPEC.md are CSS/logical px; use WPF device-independent pixels. Anchor to the working area of the primary screen (right edge, vertically centered).
- Copy or link the Fira Code font into the app resources so it ships with the build.
- Encode tokens as a small C# static class or load `docs/dock-design/tokens.json` at runtime — either is fine, but values must match.
- Prefer new files under `windows/AgentNotch/` (e.g. `Dock/`, `Relay/`) rather than stuffing everything into `AppController.cs`.
- Update `windows/README.md` with how to run the app and the preview flag.

## Preview / proof

Add a `dotnet run -- --dock-preview` mode (also via the existing `notch` shim if easy) that:

- Does not require a live Claude/Cursor session or a live Odysseus relay.
- Opens the dock, walks (or simultaneously renders) these states, and writes PNGs to `docs/screens/dock/`:
  - `01-collapsed.png`
  - `02-rail-open.png`
  - `03-chat-open.png`
  - `04-typing-streaming.png` (or a chat with typing + a short streamed line)
  - `05-approval-pending.png`
- Then exits cleanly (do not leave a persistent overlay running after preview).
- If a fully automated screenshot pass is blocked, still make the UI runnable with `dotnet run` and document a manual way to capture those states; but try hard to emit the PNGs automatically (e.g. `RenderTargetBitmap` of the dock visuals, no need to capture the whole desktop).

Also run a normal `dotnet build` of `windows/AgentNotch` and fix any errors. Optionally run existing `--scan` to confirm discovery still works.

## Done when

- `dotnet build` of the Windows project succeeds.
- The dock states exist in the running app and match SPEC/tokens (pill, rail, chat, approval, pulse, status dots).
- Session detection still compiles and `--scan` still runs.
- Relay is either connected behind a clean client, or stubbed with a clear offline path.
- Preview PNGs exist under `docs/screens/dock/` if technically possible.
- `windows/README.md` explains how to run the preview.
- No commit was made by you.
