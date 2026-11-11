# Phase 2: agent-notch side dock (Windows)

You are implementing Phase 2 in the existing Windows C#/WPF port at `windows/AgentNotch/`. This is an upgrade of the current dock on `feat/dock-phase2` (branched from main after PR #1 merged). Do not rewrite the app.

## Repo / safety (must follow)

- Working tree should be clean except for this brief and files you create. If you see uncommitted `main.swift`, `openspec/`, `docs/dev-wiki.html`, or `docs/screens/windows-*`, leave them completely alone (do not add, commit, stash, restore, or edit).
- Do NOT commit, push, merge, tag, or amend. The operator will commit after review.
- Do NOT restart Odysseus, Docker containers, Aether (`server.js` on port 3000), or the already-running AgentNotch overlay (PID may be 19912, `AgentNotch.exe`). Do not run `notch stop` or `taskkill` on AgentNotch / node / docker.
- Do NOT read, print, copy, or commit secrets, API keys, tokens, or `.env` files (including `C:\Users\tylar\code\odysseus\.env`, `C:\Users\tylar\code\Aether\.env`, `~/.chums/grokbot.env`). Never write `ODYSSEUS_API_TOKEN` or any token into source, docs, logs, `relay.json`, screenshots, or commit messages.
- Do NOT install global tools, system packages, Python packages, or SDKs. Adding a NuGet package to `AgentNotch.csproj` (e.g. NAudio) is OK. `dotnet restore` / `dotnet build` is OK.
- Target `net10.0-windows`. Do not compile or run macOS `main.swift`.
- Follow `docs/dock-design/SPEC.md`, `docs/dock-design/tokens.json`, and `docs/dock-design/tokens.css`. Keep the Odysseus look (no Storybox pink).

## What already exists (Phase 1)

- Edge pill, 42×285 rail, 428×601 chat panel, approval cards, pulsing notch.
- Relay client at `windows/AgentNotch/Relay/`: `RelayConfig` reads `AGENT_NOTCH_RELAY_URL` or `%USERPROFILE%\.config\agent-notch\relay.json` (`baseUrl` only). `OdysseusRelayClient` probes `GET /api/health` and posts JSON `{message, stream:true}` to `POST /api/chat_stream`. This client is incomplete (see below). `StubOdysseusRelay` is used when no URL is configured, and in `--dock-preview`.
- Claude Code approvals: `hooks/notify.ps1` writes `~/.config/agent-notch/pending/<session-id>.json`; `ApprovalStore` polls it; dock Approve/Deny sends keystrokes. Cursor is a stub (`Discovery/CursorApprovalStub.cs`).
- Mic and `+` buttons are chrome-only placeholders.
- Preview: `dotnet run --project windows/AgentNotch/AgentNotch.csproj -- --dock-preview` writes PNGs to `docs/screens/dock/`.

## Live services already running on this PC (probe read-only; do not restart)

### Odysseus
- Docker container `odysseus-odysseus-1`, published at `http://127.0.0.1:7000` (also `http://localhost:7000`). Public URL is `https://odysseus.tail42ce2d.ts.net/` — prefer the local URL in `relay.json`.
- Process env already has `ODYSSEUS_URL=http://localhost:7000/` and `ODYSSEUS_API_TOKEN` (use the env var; never print it).
- `GET http://127.0.0.1:7000/api/health` → 200 `{"status":"healthy",...}` (no auth required).
- `GET /api/chat_stream` without auth → 401. Chat requires `Authorization: Bearer <ODYSSEUS_API_TOKEN>`.
- Auth helper: Bearer token is the env operator token (`ody_...`). Send it on every authenticated request. Do not put the token in `relay.json`. Read it only from `ODYSSEUS_API_TOKEN` (and optionally `AN_ODYSSEUS_API_TOKEN` if you add one). If the token is missing, stay in a clear "relay needs token" / disconnected state — do not crash.
- Write `%USERPROFILE%\.config\agent-notch\relay.json` as `{ "baseUrl": "http://127.0.0.1:7000" }` only (create the directory if needed). You may also add a non-secret `aetherBaseUrl` of `http://127.0.0.1:3000` in that same file or a sibling config. No tokens.

### Real chat protocol (Phase 1 client is wrong — fix it)
The Phase 1 client does not send a Bearer token, does not create/send a session id, and treats SSE `data:` lines as raw text. Odysseus actually emits JSON events.

1. **Create or reuse a dock session** (session id is required):
   - `GET /api/default-chat` with Bearer → `{ "endpoint_id", "endpoint_url", "model" }`.
   - `POST /api/session` as `multipart/form-data` with `name` (e.g. `Agent Notch`), `endpoint_id`, and `model`. Response includes `id`.
   - Persist the session id in `%USERPROFILE%\.config\agent-notch\relay.json` as `sessionId` (not a secret) so later launches reuse it. If a stored session 404s, create a new one.
2. **Send a message**: `POST /api/chat_stream` with Bearer and JSON body:
   `{ "message": "<text>", "session": "<sessionId>", "mode": "chat" }`
   Default to `mode: "chat"` (not agent) so a dock ping does not launch tools. The server may still auto-escalate some intents; keep test messages innocuous.
   Note: the Python handler may also try `await request.form()` after reading JSON. Prefer JSON. If that 400s because the body was consumed, switch to multipart form fields `message`, `session`, `mode=chat`.
3. **Parse SSE correctly**:
   - Lines look like `data: {"delta":"hello"}` or `data: {"type":"model_info",...}` or `data: [DONE]`.
   - Append only `delta` text to the streamed assistant reply.
   - Ignore other event types (`model_info`, `metrics`, `tool_start`, `attachments`, `rag_sources`, heartbeats starting with `:`).
   - Stop on `data: [DONE]`.
   - Fix the CA2024 warning (`reader.EndOfStream` in async) while you are here.
4. **Verify a real round-trip yourself**: after the client is wired, send a single innocuous message such as `Reply with exactly the word pong and nothing else.` to the live local relay. Confirm you get a streamed assistant reply. Log only status codes, whether a reply arrived, and a short sanitized excerpt (first 80 chars). Never log headers, tokens, or full prompt/response dumps. If the round-trip fails, leave the UI honest ("relay error (code)") and document the exact failure (status, missing session, parse error) in `windows/README.md`.

### Aether (mic / speech-to-text)
- Local server already running: `C:\Users\tylar\code\Aether\server.js` listening on `http://127.0.0.1:3000`.
- `GET /health` → 200 `{"status":"ok","auto_compress":true,"max_upload_mb":200,"groq_max_mb":24}`.
- Press-to-talk: while the mic button is held, record the default microphone to a wav/webm/mp3 temp file; on release, `POST` multipart field `file` to:
  1. `http://127.0.0.1:3000/api/transcribe-groq` (fast; may 500 if Groq is unconfigured — do not print that server's env), then
  2. fall back to `http://127.0.0.1:3000/api/transcribe-whisper` (local whisper, form field `model` optional, default `tiny`).
- Response is `{ "text": "..." }`. Fill the composer input with that text (do not auto-send). Show a brief recording state on the mic (teal / pulsing) matching the dock tokens. If Aether is down, show a quiet error in the composer ("Aether offline") and do not crash.
- Configurable Aether base URL (default `http://127.0.0.1:3000`) from `relay.json` / env `AGENT_NOTCH_AETHER_URL`.
- Do not implement the voice orb. Do not install ffmpeg or Python whisper packages; if a transcribe call fails, surface the server's error string (trimmed) in the UI.

### Screenshot attach (`+` button)
- On `+` click, capture a PNG screenshot of the current foreground top-level window, excluding Agent Notch / dock windows (pill, rail, chat). If the foreground is a dock window, use the last non-dock foreground, or the primary screen as a last resort.
- Show a small thumbnail chip in the composer. Sending the message should upload then attach:
  - `POST /api/upload` with Bearer, multipart files. Response: `{ "files": [ { "id", "name", "mime", "size", ... } ] }`.
  - Include `attachments: ["<id>", ...]` on the subsequent `/api/chat_stream` request (JSON list, or form JSON string as the server accepts).
- Attachment-only sends are allowed (empty message + attachments). If the relay is offline, keep the screenshot locally in the transcript and say the relay is offline.

### Cursor Approve/Deny (replace the stub)
Cursor now has documented hooks: https://cursor.com/docs/hooks

User-level file would be `%USERPROFILE%\.cursor\hooks.json`. **Do NOT write or overwrite Tylar's user `~/.cursor/hooks.json` or any other user Cursor settings.** That is an opt-in install. Ship the hook in-repo and document the snippet.

Implement a real hook script, modeled on `windows/AgentNotch/hooks/notify.ps1`:

- Add `windows/AgentNotch/hooks/cursor-permission.ps1`.
- Cursor events to support: `beforeShellExecution`, `beforeMCPExecution`, `beforeReadFile` / `preToolUse` if practical (file-edit / tool prompts). Read JSON from stdin (strip a leading UTF-8 BOM if present — a known Windows Cursor issue).
- Write a pending marker to `%USERPROFILE%\.config\agent-notch\pending\<id>.json` in the same shape ApprovalStore already understands (`sessionId`, `message`, `tool`, `cwd`, `timestamp`). Put the command, file path, or MCP tool name in `message`/`tool` so the approval card payload row can show the right thing (path vs command vs tool).
- Then **wait** (poll, timeout ~120s) for a sibling decision file such as `pending/<id>.decision.json` written by the dock when the user clicks Approve or Deny. Return hook JSON on stdout: `{ "permission": "allow" }` or `{ "permission": "deny", "user_message": "Denied from Agent Notch" }` and exit 0. On timeout, return `{ "permission": "ask" }` so Cursor's own prompt still appears, and leave the pending card visible.
- Wire dock Approve/Deny for Cursor sessions to write that decision file (and keep Claude's existing keystroke approve path working).
- Replace `CursorApprovalStub.cs` with a short comment + the hook path, or delete the stub if the new hook + store path fully supersedes it.
- Document in `windows/README.md` the exact `~/.cursor/hooks.json` snippet to opt in (PowerShell `-File` pointing at the hook, `version: 1`, the events above). Do not auto-install it.
- Extend `--dock-preview` so a Cursor approval card still appears in demo mode.

If some Cursor prompt types cannot be hooked (for example built-in MCP allowlist prompts that ignore hook `allow`), document that limitation precisely in the README and keep those cards as "pending / needs hook install" rather than pretending they work.

## UI / design notes

- Keep Phase 1 layout, typography (embedded Fira Code), motion, and tokens.
- Mic: press-to-talk, not a click-to-toggle orb.
- `+` should feel like the clip's attach control; thumbnail chip is enough (no live desktop-preview blob).
- Chat should show "Typing..." then stream tokens when the relay is connected; keep the honest "relay offline" state when it is not.
- Update `windows/README.md` with relay.json, token env, Aether, screenshot attach, Cursor hook opt-in, and preview instructions.

## Build / preview

- `dotnet build windows/AgentNotch/AgentNotch.csproj -c Release` must succeed (warnings are OK if you fixed CA2024; no errors).
- Optionally run `--dock-preview` to refresh `docs/screens/dock/*.png`. Do not kill the already-running normal overlay to do this. Preview is a separate process.
- You may run a small console test (or `dotnet run` of a short test) for the relay round-trip, but do not start a second always-on overlay.

## Done when

- Chat talks to the real local Odysseus relay with auth + session + correct SSE parsing.
- A real message round-trip was attempted and the result is documented (success or exact failure).
- Mic press-to-talk fills the input via Aether.
- `+` captures the current (non-dock) window and can attach it on send.
- Cursor approvals have a real hook + decision file path; user hooks.json is not silently installed.
- Build passes. README updated. No secrets in the tree.
