#Requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PayloadRoot)
$ErrorActionPreference = 'Stop'
$verify = Join-Path $PSScriptRoot 'Verify-AgentPayload.ps1'
$root = Join-Path $env:ProgramFiles 'Club'
$data = Join-Path $env:ProgramData 'Club\Agent'
$journal = Join-Path $data 'update-in-progress.json'
if (Test-Path -LiteralPath $journal) { throw 'An interrupted update exists. Recover it using windows-pilot.md before retrying.' }
& $verify -PayloadRoot $PayloadRoot
& $verify -PayloadRoot $root
$config = Get-Content -LiteralPath (Join-Path $data 'agent.json') -Raw | ConvertFrom-Json
$user = Get-LocalUser -SID $config.visitorSid
if ($user.Enabled) { throw 'Disable the dedicated visitor Windows account and log it off before updating.' }
if (Get-CimInstance Win32_UserProfile | Where-Object { $_.SID -eq $config.visitorSid -and $_.Loaded }) {
    throw 'The visitor profile is still loaded. Log off the visitor before updating.'
}
$service = Get-CimInstance Win32_Service -Filter "Name='ClubAgent'"
$expectedExe = '"' + (Join-Path $root 'Agent\Club.Agent.exe') + '"'
if (-not $service -or $service.PathName -ne $expectedExe -or $service.StartName -ne 'LocalSystem' -or
    $service.StartMode -ne 'Auto' -or $service.State -ne 'Running') { throw 'Expected the installed automatic LocalSystem ClubAgent service to be running.' }
function Assert-MaintenanceCheckpoint {
    $stream = [IO.File]::Open((Join-Path $data 'session.json'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite,Delete')
    $reader = New-Object IO.StreamReader($stream)
    try { $checkpoint = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($checkpoint.state -notin @('Idle','Paused','Recovery','Ended','Completed') -or
        $null -eq $checkpoint.remainingMs -or $checkpoint.remainingMs -lt 0 -or
        $null -eq $checkpoint.revision -or $checkpoint.revision -lt 0) {
        throw 'The checkpoint is active or invalid. Pause/finish the session and retry.'
    }
}
Assert-MaintenanceCheckpoint
# Reject links before copying or moving anything in the installed tree.
foreach ($path in @($root, $data, (Join-Path $root 'agent-payload.json'))) {
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Installation links are forbidden.' }
}
$work = Join-Path $root ('.update-' + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $work 'staged'
$previous = Join-Path $work 'previous'
New-Item -ItemType Directory -Path $stage, $previous | Out-Null
foreach ($component in @('Agent','Visitor','agent-payload.json')) {
    Copy-Item -LiteralPath (Join-Path $PayloadRoot $component) -Destination $stage -Recurse
}
& $verify -PayloadRoot $stage
# A disabled service cannot auto-start a mixed set of binaries after a power interruption.
$state = @{ version = 1; workDirectory = $work; phase = 'prepared'; movedOld = @(); movedNew = @() }
function Save-UpdateState {
    $temporary = $journal + '.tmp'
    $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $journal -Force
}
Save-UpdateState
try {
    Set-Service ClubAgent -StartupType Disabled
    Stop-Service ClubAgent -ErrorAction Stop
    (Get-Service ClubAgent).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    Assert-MaintenanceCheckpoint
    $state.phase = 'replacing'; Save-UpdateState
    foreach ($component in @('Agent','Visitor','agent-payload.json')) {
        Move-Item -LiteralPath (Join-Path $root $component) -Destination $previous
        $state.movedOld += $component; Save-UpdateState
        Move-Item -LiteralPath (Join-Path $stage $component) -Destination $root
        $state.movedNew += $component; Save-UpdateState
    }
    & $verify -PayloadRoot $root
    $state.phase = 'starting'; Save-UpdateState
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
    if (-not $healthy) { throw 'Updated service did not produce a checkpoint.' }
    $state.phase = 'complete'; Save-UpdateState
    Copy-Item -LiteralPath $journal -Destination (Join-Path $work 'result.json')
    Remove-Item -LiteralPath $journal
    Write-Host ('Update completed; previous payload retained in: ' + $previous)
    Write-Host 'Visitor account remains disabled. Check the service and server before enabling it; resume paid time explicitly.'
} catch {
    $failure = $_
    try {
        Set-Service ClubAgent -StartupType Disabled
        Stop-Service ClubAgent -ErrorAction Stop
        (Get-Service ClubAgent).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        # Only remove replacements whose previous copy was actually moved in this process.
        foreach ($component in @('Agent','Visitor','agent-payload.json')) {
            if ($state.movedOld -contains $component) {
                $current = Join-Path $root $component
                if (Test-Path -LiteralPath $current) { Remove-Item -LiteralPath $current -Recurse -Force }
                Move-Item -LiteralPath (Join-Path $previous $component) -Destination $root
            }
        }
        & $verify -PayloadRoot $root
        Set-Service ClubAgent -StartupType Automatic
        Start-Service ClubAgent
        (Get-Service ClubAgent).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
        $state.phase = 'rolledBack'; Save-UpdateState
        Copy-Item -LiteralPath $journal -Destination (Join-Path $work 'result.json')
        Remove-Item -LiteralPath $journal
    } catch {
        Set-Service ClubAgent -StartupType Disabled
        throw 'Update and rollback could not finish. ClubAgent is disabled; keep visitor disabled and recover using update-in-progress.json and windows-pilot.md.'
    }
    throw $failure
}
