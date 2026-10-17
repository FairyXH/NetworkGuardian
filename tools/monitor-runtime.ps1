param(
    [string]$Url = 'http://127.0.0.1:18765/mcp',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/runtime-monitor-2026-10-17')
)

$ErrorActionPreference = 'Stop'
$endpoint = [uri]$Url
if ($endpoint.Scheme -ne 'http' -or $endpoint.Host -ne '127.0.0.1' -or
    $endpoint.AbsolutePath -ne '/mcp' -or $endpoint.UserInfo -or $endpoint.Query -or $endpoint.Fragment) {
    throw 'Only the local NetworkGuardian MCP endpoint is allowed.'
}
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$sample = [ordered]@{
    startedAt = [DateTimeOffset]::Now.ToString('o')
    endpoint = $Url
    processes = @()
    calls = [ordered]@{}
    errors = @()
}
try {
    $sample.processes = @(Get-Process -Name NetworkGuardian -ErrorAction SilentlyContinue | ForEach-Object {
        [ordered]@{
            id = $_.Id
            path = $_.Path
            startedAt = $_.StartTime.ToString('o')
            responding = $_.Responding
            cpuSeconds = $_.CPU
            workingSetBytes = $_.WorkingSet64
            privateBytes = $_.PrivateMemorySize64
            handles = $_.HandleCount
            threadCount = $_.Threads.Count
        }
    })
} catch { $sample.errors += "process: $($_.Exception.Message)" }

$script:monitorSequence = 0
function Invoke-MonitorRpc([string]$Method, [hashtable]$Parameters) {
    $script:monitorSequence++
    $body = @{ jsonrpc = '2.0'; id = $script:monitorSequence; method = $Method; params = $Parameters } |
        ConvertTo-Json -Depth 20 -Compress
    $reply = Invoke-RestMethod -Uri $Url -Method Post -NoProxy -TimeoutSec 15 -ContentType 'application/json; charset=utf-8' -Headers @{
        Accept = 'application/json, text/event-stream'
        'MCP-Protocol-Version' = '2025-11-25'
    } -Body ([Text.Encoding]::UTF8.GetBytes($body))
    if ($reply.id -ne $script:monitorSequence) { throw 'MCP response ID mismatch.' }
    if ($reply.error) { throw ($reply.error | ConvertTo-Json -Depth 10 -Compress) }
    return $reply.result
}

try {
    $sample.server = Invoke-MonitorRpc 'initialize' @{
        protocolVersion = '2025-11-25'; capabilities = @{}
        clientInfo = @{ name = 'networkguardian-runtime-monitor'; version = '1.0' }
    }
} catch { $sample.errors += "initialize: $($_.Exception.Message)" }

$queries = [ordered]@{
    guardian_status = @{}
    training_status = @{}
    quality_report = @{}
    debug_model = @{}
    debug_decision = @{}
    debug_logs = @{ minimumLevel = 'warning'; limit = 100 }
    config_general = @{ section = 'general' }
    config_quality = @{ section = 'quality' }
    config_disabled = @{ section = 'disabledDeviceInstanceIds' }
}
foreach ($entry in $queries.GetEnumerator()) {
    $tool = if ($entry.Key.StartsWith('config_')) { 'config_get' } else { $entry.Key }
    $call = [ordered]@{ startedAt = [DateTimeOffset]::Now.ToString('o') }
    try {
        $result = Invoke-MonitorRpc 'tools/call' @{ name = $tool; arguments = $entry.Value }
        $data = $result.structuredContent
        if ($null -eq $data) {
            $contentText = ($result.content | Where-Object type -eq 'text' | ForEach-Object text) -join "`n"
            $data = $contentText | ConvertFrom-Json -Depth 100
        }
        if ($result.isError -or $data.success -eq $false) {
            throw ($data | ConvertTo-Json -Depth 30 -Compress)
        }
        $call.data = $data
        $call.success = $true
    } catch {
        $call.success = $false
        $call.error = $_.Exception.Message
        $sample.errors += "$($entry.Key): $($_.Exception.Message)"
    }
    $call.finishedAt = [DateTimeOffset]::Now.ToString('o')
    $sample.calls[$entry.Key] = $call
}
$sample.finishedAt = [DateTimeOffset]::Now.ToString('o')
$path = Join-Path $OutputDirectory ('sample-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.json')
$sample | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $path -Encoding utf8
Write-Output $path
if ($sample.errors.Count -gt 0) { Write-Warning ($sample.errors -join '; ') }
