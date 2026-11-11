# AgentNotch Docker storage prune + snapshot.
# Weekly scheduled task calls -Prune. ServiceProbe / manual runs use -Snapshot.
# Does NOT compact the Docker VHDX (no diskpart).
#
# Status file: ~/.config/agent-notch/storage.json

[CmdletBinding()]
param(
    [switch]$Prune,
    [switch]$Snapshot,
    [int]$IntervalDays = 7
)

$ErrorActionPreference = 'Continue'
$configDir = Join-Path $env:USERPROFILE '.config\agent-notch'
New-Item -ItemType Directory -Force -Path $configDir | Out-Null
$statusPath = Join-Path $configDir 'storage.json'
$logPath = Join-Path $configDir 'docker-prune.log'

function Write-Log([string]$msg) {
    $line = "$(Get-Date -Format o) $msg"
    Add-Content -Path $logPath -Value $line -ErrorAction SilentlyContinue
}

function Get-DriveStats {
    $d = Get-PSDrive C -ErrorAction SilentlyContinue
    if (-not $d) { return @{ FreeGb = 0; TotalGb = 0 } }
    $free = [math]::Round($d.Free / 1GB, 2)
    $total = [math]::Round(($d.Used + $d.Free) / 1GB, 2)
    return @{ FreeGb = $free; TotalGb = $total }
}

function Get-VhdxGb {
    $vhdx = Join-Path $env:LOCALAPPDATA 'Docker\wsl\disk\docker_data.vhdx'
    if (-not (Test-Path $vhdx)) { return $null }
    return [math]::Round((Get-Item $vhdx).Length / 1GB, 2)
}

function Get-DockerDf {
    $out = @{
        Ok            = $false
        ImagesGb      = $null
        ReclaimableGb = $null
        ContainersGb  = $null
        VolumesGb     = $null
        BuildCacheGb  = $null
        Raw           = $null
        Error         = $null
    }
    try {
        $text = & docker system df 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) {
            $out.Error = ($text -replace '\s+', ' ').Trim()
            return $out
        }
        $out.Ok = $true
        $out.Raw = ($text -replace '\s+', ' ').Trim()
        foreach ($line in ($text -split "`n")) {
            if ($line -match '^Images\s+\S+\s+\S+\s+([\d.]+[KMGT]?B)\s+([\d.]+[KMGT]?B)') {
                $out.ImagesGb = Convert-ToGb $Matches[1]
                $out.ReclaimableGb = Convert-ToGb $Matches[2]
            }
            elseif ($line -match '^Containers\s+\S+\s+\S+\s+([\d.]+[KMGT]?B)') {
                $out.ContainersGb = Convert-ToGb $Matches[1]
            }
            elseif ($line -match '^Local Volumes\s+\S+\s+\S+\s+([\d.]+[KMGT]?B)') {
                $out.VolumesGb = Convert-ToGb $Matches[1]
            }
            elseif ($line -match '^Build Cache\s+\S+\s+\S+\s+([\d.]+[KMGT]?B)') {
                $out.BuildCacheGb = Convert-ToGb $Matches[1]
            }
        }
    }
    catch {
        $out.Error = $_.Exception.Message
    }
    return $out
}

function Convert-ToGb([string]$size) {
    if ($size -match '^([\d.]+)([KMGT]?B)$') {
        $n = [double]$Matches[1]
        switch ($Matches[2]) {
            'B'  { return [math]::Round($n / 1GB, 3) }
            'KB' { return [math]::Round($n / 1MB, 3) }
            'MB' { return [math]::Round($n / 1024, 3) }
            'GB' { return [math]::Round($n, 3) }
            'TB' { return [math]::Round($n * 1024, 3) }
            default { return $n }
        }
    }
    return $null
}

function Write-Status([hashtable]$extra) {
    $drive = Get-DriveStats
    $df = Get-DockerDf
    $prev = $null
    if (Test-Path $statusPath) {
        try { $prev = Get-Content $statusPath -Raw | ConvertFrom-Json } catch { }
    }

    $obj = [ordered]@{
        updated_utc       = (Get-Date).ToUniversalTime().ToString('o')
        interval_days     = $IntervalDays
        c_free_gb         = $drive.FreeGb
        c_total_gb        = $drive.TotalGb
        vhdx_gb           = Get-VhdxGb
        docker_ok         = $df.Ok
        images_gb         = $df.ImagesGb
        reclaimable_gb    = $df.ReclaimableGb
        containers_gb     = $df.ContainersGb
        volumes_gb        = $df.VolumesGb
        build_cache_gb    = $df.BuildCacheGb
        docker_error      = $df.Error
        last_prune_utc    = if ($extra.ContainsKey('last_prune_utc')) { $extra.last_prune_utc } elseif ($prev) { $prev.last_prune_utc } else { $null }
        last_prune_ok     = if ($extra.ContainsKey('last_prune_ok')) { $extra.last_prune_ok } elseif ($prev) { $prev.last_prune_ok } else { $null }
        last_reclaimed    = if ($extra.ContainsKey('last_reclaimed')) { $extra.last_reclaimed } elseif ($prev) { $prev.last_reclaimed } else { $null }
        last_prune_detail = if ($extra.ContainsKey('last_prune_detail')) { $extra.last_prune_detail } elseif ($prev) { $prev.last_prune_detail } else { $null }
    }
    ($obj | ConvertTo-Json -Depth 4) | Set-Content -Path $statusPath -Encoding utf8
    return $obj
}

if ($Prune) {
    Write-Log 'prune start'
    $notes = [System.Collections.Generic.List[string]]::new()
    $ok = $true

    foreach ($cmd in @(
        @{ Name = 'builder'; Args = @('builder', 'prune', '-af') },
        @{ Name = 'containers'; Args = @('container', 'prune', '-f') },
        @{ Name = 'images'; Args = @('image', 'prune', '-af') },
        @{ Name = 'volumes'; Args = @('volume', 'prune', '-f') }
    )) {
        try {
            $text = & docker @($cmd.Args) 2>&1 | Out-String
            if ($LASTEXITCODE -ne 0) {
                $ok = $false
                $notes.Add("$($cmd.Name): FAIL $($text.Trim())")
                Write-Log "$($cmd.Name) fail: $($text.Trim())"
            }
            else {
                $reclaimed = if ($text -match 'Total reclaimed space:\s*(.+)') { $Matches[1].Trim() } else { 'ok' }
                $notes.Add("$($cmd.Name): $reclaimed")
                Write-Log "$($cmd.Name): $reclaimed"
            }
        }
        catch {
            $ok = $false
            $notes.Add("$($cmd.Name): $($_.Exception.Message)")
            Write-Log "$($cmd.Name) exception: $($_.Exception.Message)"
        }
    }

    $detail = ($notes -join ' | ')
    $status = Write-Status @{
        last_prune_utc    = (Get-Date).ToUniversalTime().ToString('o')
        last_prune_ok     = $ok
        last_reclaimed    = $detail
        last_prune_detail = $detail
    }
    Write-Log "prune done ok=$ok free=$($status.c_free_gb)G images=$($status.images_gb)G"
    $status | ConvertTo-Json -Depth 4
    exit $(if ($ok) { 0 } else { 1 })
}

# Default / -Snapshot: refresh status only
$status = Write-Status @{}
$status | ConvertTo-Json -Depth 4
exit 0
