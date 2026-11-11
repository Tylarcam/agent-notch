# AgentNotch Cursor permission hook (opt-in — see windows/README.md).
# Reads JSON from stdin (strips UTF-8 BOM). Writes ~/.config/agent-notch/pending/<id>.json
# and waits up to 120s for <id>.decision.json from the dock Approve/Deny buttons.

$raw = [Console]::In.ReadToEnd()
if ($raw.Length -gt 0 -and [int][char]$raw[0] -eq 0xFEFF) { $raw = $raw.Substring(1) }
if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }

try { $payload = $raw | ConvertFrom-Json } catch { exit 0 }

$event = [string]$payload.hook_event_name
$supported = @(
    'beforeShellExecution', 'beforeMCPExecution', 'beforeReadFile', 'preToolUse'
)
if ($supported -notcontains $event) { exit 0 }

$conversationId = [string]$payload.conversation_id
$markerId = $conversationId
if ([string]::IsNullOrWhiteSpace($markerId)) {
    $markerId = [guid]::NewGuid().ToString('n')
}

$message = ''
$tool = $null
$cwd = $null

switch ($event) {
    'beforeShellExecution' {
        $message = [string]$payload.command
        $tool = 'Shell'
        $cwd = [string]$payload.cwd
    }
    'beforeMCPExecution' {
        $tool = [string]$payload.tool_name
        $message = if ($tool) { "MCP: $tool" } else { 'MCP tool' }
        if ($payload.mcp_server_name) { $message += " ($($payload.mcp_server_name))" }
    }
    'beforeReadFile' {
        $message = [string]$payload.file_path
        $tool = 'Read'
    }
    'preToolUse' {
        $tool = [string]$payload.tool_name
        $input = $payload.tool_input
        if ($input -is [string]) {
            try { $input = $input | ConvertFrom-Json } catch { }
        }
        if ($tool -eq 'Shell' -and $input.command) { $message = [string]$input.command }
        elseif ($input -and $input.command) { $message = [string]$input.command }
        elseif ($input -and $input.path) { $message = [string]$input.path }
        elseif ($input -and $input.file_path) { $message = [string]$input.file_path }
        else { $message = $tool }
        $cwd = [string]$payload.cwd
    }
}

if ([string]::IsNullOrWhiteSpace($message)) { $message = "$event permission" }

$dir = Join-Path $env:USERPROFILE '.config\agent-notch\pending'
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$marker = @{
    sessionId = $markerId
    message   = $message
    tool      = $tool
    cwd       = $cwd
    timestamp = [DateTimeOffset]::UtcNow.ToString('o')
    agent     = 'cursor'
} | ConvertTo-Json -Compress

$pendingFile = Join-Path $dir "$markerId.json"
$decisionFile = Join-Path $dir "$markerId.decision.json"
$tmp = "$pendingFile.tmp"
Set-Content -Path $tmp -Value $marker -Encoding UTF8
Move-Item -Path $tmp -Destination $pendingFile -Force

$deadline = [DateTimeOffset]::UtcNow.AddSeconds(120)
while ([DateTimeOffset]::UtcNow -lt $deadline) {
    if (Test-Path -LiteralPath $decisionFile) {
        try {
            $dec = Get-Content -LiteralPath $decisionFile -Raw -Encoding UTF8 | ConvertFrom-Json
            $d = [string]$dec.decision
            Remove-Item -LiteralPath $decisionFile -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $pendingFile -Force -ErrorAction SilentlyContinue
            if ($d -eq 'allow') {
                @{ permission = 'allow' } | ConvertTo-Json -Compress | Write-Output
                exit 0
            }
            @{
                permission   = 'deny'
                user_message = 'Denied from Agent Notch'
            } | ConvertTo-Json -Compress | Write-Output
            exit 0
        } catch { }
    }
    Start-Sleep -Milliseconds 400
}

@{ permission = 'ask' } | ConvertTo-Json -Compress | Write-Output
exit 0
