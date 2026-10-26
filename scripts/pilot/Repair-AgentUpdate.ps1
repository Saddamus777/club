#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:ProgramFiles 'Club'
$data = Join-Path $env:ProgramData 'Club\Agent'
$journal = Join-Path $data 'update-in-progress.json'
# Exclusive handle serializes updater and repair; OS releases it after process termination.
$maintenanceLock = [IO.File]::Open((Join-Path $data 'maintenance.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
$state = Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json
$work = [IO.Path]::GetFullPath([string]$state.workDirectory)
if ($state.version -ne 1 -or (Split-Path -Parent $work) -ne $root -or
    (Split-Path -Leaf $work) -notmatch '^\.update-[a-f0-9]{32}$') { throw 'Invalid update recovery location.' }
foreach ($path in @($root, $data, $work)) {
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Recovery links are forbidden.' }
}
$config = Get-Content -LiteralPath (Join-Path $data 'agent.json') -Raw | ConvertFrom-Json
if ((Get-LocalUser -SID $config.visitorSid).Enabled -or
    (Get-CimInstance Win32_UserProfile | Where-Object { $_.SID -eq $config.visitorSid -and $_.Loaded })) {
    throw 'Keep the dedicated visitor account disabled and logged off during recovery.'
}
$service = Get-CimInstance Win32_Service -Filter "Name='ClubAgent'"
if (-not $service -or $service.StartName -ne 'LocalSystem' -or
    $service.PathName -ne ('"' + (Join-Path $root 'Agent\Club.Agent.exe') + '"')) { throw 'Unexpected ClubAgent service configuration.' }
Set-Service ClubAgent -StartupType Disabled
Stop-Service ClubAgent -ErrorAction Stop
(Get-Service ClubAgent).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
$previous = Join-Path $work 'previous'
if ((Get-Item -LiteralPath $previous).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Recovery links are forbidden.' }
$repair = Join-Path $work ('repair-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $repair | Out-Null
# Inspect physical directories, not movedOld flags: power can fail between a move and journal write.
foreach ($component in @('Agent','Visitor','agent-payload.json')) {
    $source = Join-Path $previous $component
    if (-not (Test-Path -LiteralPath $source)) { $source = Join-Path $root $component }
    if ((Get-Item -LiteralPath $source).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Recovery links are forbidden.' }
    # Verify each directory tree before Copy-Item can follow a nested reparse point.
    if ((Get-Item -LiteralPath $source).PSIsContainer) {
        $pending = New-Object 'System.Collections.Generic.Queue[string]'
        $pending.Enqueue($source)
        while ($pending.Count -gt 0) {
            foreach ($item in Get-ChildItem -LiteralPath $pending.Dequeue() -Force) {
                if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Recovery links are forbidden.' }
                if ($item.PSIsContainer) { $pending.Enqueue($item.FullName) }
            }
        }
    }
    Copy-Item -LiteralPath $source -Destination $repair -Recurse
}
$verify = Join-Path $PSScriptRoot 'Verify-AgentPayload.ps1'
& $verify -PayloadRoot $repair
# Complete the immutable previous payload before touching the current installation.
# A second interruption during repair can then use the same complete source again.
foreach ($component in @('Agent','Visitor','agent-payload.json')) {
    $saved = Join-Path $previous $component
    if (-not (Test-Path -LiteralPath $saved)) {
        $pendingCopy = Join-Path $previous ('pending-' + [Guid]::NewGuid().ToString('N'))
        Copy-Item -LiteralPath (Join-Path $repair $component) -Destination $pendingCopy -Recurse
        Move-Item -LiteralPath $pendingCopy -Destination $saved
    }
}
& $verify -PayloadRoot $previous
# Keep every original source above until all replacement moves and health checks finish.
foreach ($component in @('Agent','Visitor','agent-payload.json')) {
    $destination = Join-Path $root $component
    if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
    Copy-Item -LiteralPath (Join-Path $repair $component) -Destination $root -Recurse
}
& $verify -PayloadRoot $root
try {
    Set-Service ClubAgent -StartupType Automatic
    $before = (Get-Item -LiteralPath (Join-Path $data 'session.json')).LastWriteTimeUtc
    Start-Service ClubAgent
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $healthy = $false
    do {
        Start-Sleep -Milliseconds 500
        if ((Get-Service ClubAgent).Status -eq 'Running' -and
            (Get-Item -LiteralPath (Join-Path $data 'session.json')).LastWriteTimeUtc -gt $before) { $healthy = $true; break }
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $healthy) { throw 'Recovered service did not produce a checkpoint.' }
    $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $repair 'recovered-journal.json') -Encoding UTF8
    Remove-Item -LiteralPath $journal
} catch {
    Set-Service ClubAgent -StartupType Disabled
    throw 'Recovered files did not start. Keep visitor disabled; recovery files and journal were retained.'
}
Write-Host 'Previous payload restored. Visitor remains disabled; verify server/session state before enabling it.'

} finally { $maintenanceLock.Dispose() }
