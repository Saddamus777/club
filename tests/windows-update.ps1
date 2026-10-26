# Invoked by windows-agent.ps1 inside its private disposable installation, before visitor probes.
param([Parameter(Mandatory)][string]$PayloadRoot, [Parameter(Mandatory)][string]$VisitorUser)
$ErrorActionPreference = 'Stop'
$update = Join-Path $PayloadRoot 'Setup\Update-Agent.ps1'
$root = Join-Path $env:ProgramFiles 'Club'
$data = Join-Path $env:ProgramData 'Club\Agent'
$temporary = Join-Path $env:TEMP ('club-update-' + [Guid]::NewGuid().ToString('N'))
$files = @('agent.json','games.json','installation.json')
$hashes = @{}
foreach ($file in $files) { $hashes[$file] = (Get-FileHash (Join-Path $data $file)).Hash }
$originalExe = (Get-FileHash (Join-Path $root 'Agent\Club.Agent.exe')).Hash
try {
    $beforePid = (Get-CimInstance Win32_Service -Filter "Name='ClubAgent'").ProcessId
    $rejected = $false
    try { & $update -PayloadRoot $PayloadRoot } catch { $rejected = $true }
    if (-not $rejected -or (Get-CimInstance Win32_Service -Filter "Name='ClubAgent'").ProcessId -ne $beforePid) {
        throw 'Enabled visitor account was not rejected before interrupting service.'
    }
    Disable-LocalUser -Name $VisitorUser
    $held = [IO.File]::Open((Join-Path $data 'maintenance.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $rejected = $false
        try { & $update -PayloadRoot $PayloadRoot } catch { $rejected = $true }
        if (-not $rejected -or (Get-CimInstance Win32_Service -Filter "Name='ClubAgent'").ProcessId -ne $beforePid) {
            throw 'Concurrent maintenance was not refused before interrupting service.'
        }
    } finally { $held.Dispose() }
    $variant = Join-Path $temporary 'variant'
    New-Item -ItemType Directory -Path $variant | Out-Null
    foreach ($component in @('Agent','Visitor','agent-payload.json')) {
        Copy-Item -LiteralPath (Join-Path $PayloadRoot $component) -Destination $variant -Recurse
    }
    'compatible payload variant' | Set-Content (Join-Path $variant 'Visitor\update-fixture.txt')
    python scripts/write-agent-manifest.py $variant
    if ($LASTEXITCODE -ne 0) { throw 'Could not generate compatible update fixture.' }
    & $update -PayloadRoot $variant
    if (-not (Test-Path (Join-Path $root 'Visitor\update-fixture.txt'))) { throw 'New payload file was not installed.' }
    if ((Get-LocalUser -Name $VisitorUser).Enabled) { throw 'Updater enabled visitor without review.' }
    $archives = @(Get-ChildItem -LiteralPath $root -Directory -Filter '.update-*' | Where-Object {
        Test-Path -LiteralPath (Join-Path $_.FullName 'previous\agent-payload.json')
    })
    if ($archives.Count -ne 1) { throw 'Previous verified payload was not retained.' }
    & (Join-Path $PayloadRoot 'Setup\Verify-AgentPayload.ps1') -PayloadRoot (Join-Path $archives[0].FullName 'previous')
    & $update -PayloadRoot (Join-Path $archives[0].FullName 'previous')
    if (Test-Path (Join-Path $root 'Visitor\update-fixture.txt')) { throw 'Manual rollback retained a newer payload file.' }
    foreach ($component in @('Agent','Visitor','agent-payload.json')) {
        Copy-Item -LiteralPath (Join-Path $PayloadRoot $component) -Destination $temporary -Recurse
    }
    'broken exe' | Set-Content (Join-Path $temporary 'Agent\Club.Agent.exe')
    $beforePid = (Get-CimInstance Win32_Service -Filter "Name='ClubAgent'").ProcessId
    $rejected = $false
    try { & $update -PayloadRoot $temporary } catch { $rejected = $true }
    if (-not $rejected -or (Get-CimInstance Win32_Service -Filter "Name='ClubAgent'").ProcessId -ne $beforePid) {
        throw 'Corrupt payload was not rejected before interrupting service.'
    }
    # A self-consistent but non-executable bundle passes hashes and must roll back after startup failure.
    python scripts/write-agent-manifest.py $temporary
    if ($LASTEXITCODE -ne 0) { throw 'Could not generate rollback fixture manifest.' }
    $rejected = $false
    try { & $update -PayloadRoot $temporary } catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid executable update unexpectedly succeeded.' }
    if ((Get-Service ClubAgent).Status -ne 'Running' -or (Test-Path (Join-Path $data 'update-in-progress.json')) -or
        (Get-FileHash (Join-Path $root 'Agent\Club.Agent.exe')).Hash -ne $originalExe) { throw 'Rollback did not restore running original service.' }
    foreach ($file in $files) {
        if ((Get-FileHash (Join-Path $data $file)).Hash -ne $hashes[$file]) { throw 'Update changed protected runtime configuration.' }
    }
    # Simulate power loss after one directory move but before its journal flag was written.
    Set-Service ClubAgent -StartupType Disabled
    Stop-Service ClubAgent
    $interrupted = Join-Path $root ('.update-' + [Guid]::NewGuid().ToString('N'))
    $previous = Join-Path $interrupted 'previous'
    New-Item -ItemType Directory -Path $previous | Out-Null
    @{ version = 1; workDirectory = $interrupted; phase = 'replacing'; movedOld = @(); movedNew = @() } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'update-in-progress.json') -Encoding UTF8
    Move-Item -LiteralPath (Join-Path $root 'Agent') -Destination $previous
    Copy-Item -LiteralPath (Join-Path $temporary 'Agent') -Destination $root -Recurse
    & (Join-Path $PayloadRoot 'Setup\Repair-AgentUpdate.ps1')
    if ((Get-Service ClubAgent).Status -ne 'Running' -or (Test-Path (Join-Path $data 'update-in-progress.json')) -or
        (Get-FileHash (Join-Path $root 'Agent\Club.Agent.exe')).Hash -ne $originalExe) { throw 'Interrupted update recovery failed.' }
    & (Join-Path $PayloadRoot 'Setup\Verify-AgentPayload.ps1') -PayloadRoot $root
    Write-Host 'PASS: exclusive maintenance update, previous payload retained, compatible added file installed and removed by manual rollback, enabled visitor and corrupt bundle rejected without restart, invalid EXE rolled back, device configuration/catalog preserved; interrupted move recovered without relying on journal flags.'
} finally {
    Enable-LocalUser -Name $VisitorUser
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
$global:LASTEXITCODE = 0
