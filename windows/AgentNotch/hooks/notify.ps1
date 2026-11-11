# AgentNotch approval-detection hook (opt-in — see windows/README.md).
#
# Wire this to Claude Code's Notification event in ~/.claude/settings.json:
#   "Notification": [{ "matcher": "", "hooks": [{ "type": "command",
#     "command": "powershell -NoProfile -ExecutionPolicy Bypass -File \"<this file>\"" }] }]
#
# Claude Code fires Notification both for permission prompts and for the
# separate "idle 60s" nudge — only the former has an approve/deny action, so
# only messages mentioning "permission" get a marker written.
#
# Marker files live at ~/.config/agent-notch/pending/<session-id>.json and are
# polled + cleared by AgentNotch itself (Discovery/ApprovalStore.cs) — this
# script only ever adds one.

$inputText = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($inputText)) { exit 0 }

try { $payload = $inputText | ConvertFrom-Json } catch { exit 0 }

$sessionId = $payload.session_id
$message = [string]$payload.message
if ([string]::IsNullOrWhiteSpace($sessionId) -or [string]::IsNullOrWhiteSpace($message)) { exit 0 }

if ($message -notmatch "permission") { exit 0 }

$dir = Join-Path $env:USERPROFILE ".config\agent-notch\pending"
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$tool = $null
if ($message -match "use (\w+)") { $tool = $Matches[1] }

$marker = @{
    sessionId = $sessionId
    message   = $message
    tool      = $tool
    cwd       = $payload.cwd
    timestamp = [DateTimeOffset]::UtcNow.ToString("o")
} | ConvertTo-Json -Compress

$file = Join-Path $dir "$sessionId.json"
$tmp = "$file.tmp"
Set-Content -Path $tmp -Value $marker -Encoding UTF8
Move-Item -Path $tmp -Destination $file -Force
