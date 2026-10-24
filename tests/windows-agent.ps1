#Requires -RunAsAdministrator
# Destructive only to this test's fresh fixture. Refuse an existing Club installation.
$ErrorActionPreference = 'Stop'
$programRoot = Join-Path $env:ProgramFiles 'Club'
$dataRoot = Join-Path $env:ProgramData 'Club'
if ((Test-Path $programRoot) -or (Test-Path $dataRoot) -or (Get-Service ClubAgent -ErrorAction SilentlyContinue) -or
    (Get-ScheduledTask -TaskName ClubVisitor -TaskPath '\' -ErrorAction SilentlyContinue)) { throw 'This test requires a clean disposable Windows machine.' }
$setup = (Resolve-Path 'artifacts/pilot/Setup').Path
$work = Join-Path $env:TEMP ('club-agent-' + [Guid]::NewGuid().ToString('N'))
$shared = Join-Path $env:PUBLIC ('ClubProbe-' + [Guid]::NewGuid().ToString('N'))
$node = $null; $cert = $null; $visitor = $null; $outsider = $null; $attemptedInstall = $false
$data = Join-Path $dataRoot 'Agent'
function Wait-Until([scriptblock]$Check, [string]$Failure, [int]$Seconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if (& $Check) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}
function Read-Json([string]$Path) {
    $reader = $null
    try {
        # Observing a checkpoint must not block the service's atomic file replacement.
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite,Delete')
        $reader = New-Object IO.StreamReader($stream)
        return ($reader.ReadToEnd() | ConvertFrom-Json)
    } catch { return $null }
    finally { if ($reader) { $reader.Dispose() } }
}
function Set-Grant([string]$State, [long]$Revision, [long]$Remaining) {
    $session = 'ci-session'
    if ($State -eq 'Idle') { $session = $null }
    @{ sessionId = $session; revision = $Revision; remainingMs = $Remaining; state = $State } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'grant.tmp') -Encoding UTF8
    Move-Item -LiteralPath (Join-Path $work 'grant.tmp') -Destination (Join-Path $work 'grant.json') -Force
}
function Start-FixtureServer {
    $portFile = Join-Path $work 'port.txt'
    if (Test-Path $portFile) { Remove-Item $portFile }
    $script:node = Start-Process -FilePath (Get-Command node.exe).Source -ArgumentList ('"' + (Join-Path $PSScriptRoot 'agent-test-server.cjs') + '" "' + $work + '"') `
        -WorkingDirectory $work -PassThru -RedirectStandardOutput (Join-Path $work 'server.out') -RedirectStandardError (Join-Path $work 'server.err')
    Wait-Until {
        if ($script:node.HasExited) { Get-Content (Join-Path $work 'server.err') | Write-Host; throw 'HTTPS fixture exited.' }
        Test-Path $portFile
    } 'HTTPS fixture failed to start.'
    return [int](Get-Content $portFile -Raw)
}
function Stop-FixtureServer {
    if ($script:node -and -not $script:node.HasExited) { Stop-Process -Id $script:node.Id -Force; $script:node.WaitForExit() }
    if ($script:node) { $script:node.Dispose(); $script:node = $null }
}
function Probe([Management.Automation.PSCredential]$Credential, [string]$Mode) {
    $output = Join-Path $shared ($Mode + '.out')
    $errorOutput = Join-Path $shared ($Mode + '.err')
    # The process has the standard user's real token; do not replace this with an admin-run probe.
    $process = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -Credential $Credential -LoadUserProfile `
        -WorkingDirectory $shared -ArgumentList ('-NoProfile -NonInteractive -File "' + (Join-Path $shared 'probe.ps1') + '" -Mode ' + $Mode) `
        -PassThru -RedirectStandardOutput $output -RedirectStandardError $errorOutput
    try {
        if (-not $process.WaitForExit(60000)) { $process.Kill(); throw "Probe $Mode timed out." }
        Get-Content -LiteralPath $output -ErrorAction SilentlyContinue | Write-Host
        if ($process.ExitCode -ne 0) {
            Get-Content -LiteralPath $errorOutput -ErrorAction SilentlyContinue | Write-Host
            throw "Probe $Mode failed: $($process.ExitCode)"
        }
    } finally { $process.Dispose() }
}
function Service-Pid { return (Get-CimInstance Win32_Service -Filter "Name='ClubAgent'").ProcessId }
try {
    New-Item -ItemType Directory -Path $work, $shared | Out-Null
    & icacls.exe $work /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not protect fixture secrets.' }
    Copy-Item (Join-Path $PSScriptRoot 'windows-pipe-probe.ps1') (Join-Path $shared 'probe.ps1')
    $password = ConvertTo-SecureString ([Guid]::NewGuid().ToString('N') + '!aA7') -AsPlainText -Force
    $visitor = New-LocalUser -Name ('ClubV' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) -Password $password
    $outsider = New-LocalUser -Name ('ClubX' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) -Password $password
    Add-LocalGroupMember -SID 'S-1-5-32-545' -Member $visitor, $outsider
    $visitorCredential = New-Object Management.Automation.PSCredential(($env:COMPUTERNAME + '\' + $visitor.Name), $password)
    $outsiderCredential = New-Object Management.Automation.PSCredential(($env:COMPUTERNAME + '\' + $outsider.Name), $password)
    $cert = New-SelfSignedCertificate -DnsName localhost -CertStoreLocation 'Cert:\LocalMachine\My' -NotAfter (Get-Date).AddDays(1)
    $cer = Join-Path $work 'server.cer'; $pfx = Join-Path $work 'server.pfx'
    $pfxPassword = [Guid]::NewGuid().ToString('N')
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $pfxPassword -AsPlainText -Force) -CryptoAlgorithmOption AES256_SHA256 | Out-Null
    $key = ([Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N'))
    @{ computerId = 'ci-pc'; token = $key; operationId = [Guid]::NewGuid().ToString() } |
        ConvertTo-Json | Set-Content (Join-Path $work 'test.binding.json') -Encoding UTF8
    $serverConfig = @{ pfx = $pfx; password = $pfxPassword; device = ('ci-pc:' + $key); port = 0 }
    $serverConfig | ConvertTo-Json | Set-Content (Join-Path $work 'server.json') -Encoding UTF8
    Set-Grant 'Idle' 0 0
    $port = Start-FixtureServer
    $serverConfig.port = $port
    $serverConfig | ConvertTo-Json | Set-Content (Join-Path $work 'server.json') -Encoding UTF8
    $attemptedInstall = $true
    & (Join-Path $setup 'Install-Agent.ps1') -BindingFile (Join-Path $work 'test.binding.json') `
        -ServerUrl ('https://localhost:' + $port) -VisitorUser $visitor.Name -ServerCertificate $cer -CertificateThumbprint $cert.Thumbprint
    # Credential probes must not race an automatically opened Visitor for the single pipe.
    Disable-ScheduledTask -TaskName ClubVisitor -TaskPath '\' | Out-Null
    Wait-Until { (Test-Path (Join-Path $work 'report.json')) -and (Test-Path (Join-Path $data 'session.json')) } 'No authenticated HTTPS heartbeat/checkpoint.'
    $service = Get-CimInstance Win32_Service -Filter "Name='ClubAgent'"
    if ($service.State -ne 'Running' -or $service.StartName -ne 'LocalSystem' -or $service.StartMode -ne 'Auto') { throw 'Wrong service identity or start mode.' }
    if (-not (Test-Path ('Cert:\LocalMachine\Root\' + $cert.Thumbprint))) { throw 'Server trust was not installed.' }
    $catalog = Join-Path $work 'games.json'
    ConvertTo-Json -InputObject @(@{ id = 'sample'; name = 'CI sample'; executable = (Join-Path $programRoot 'Visitor\Club.Visitor.exe'); arguments = @() }) -Depth 4 |
        Set-Content $catalog -Encoding UTF8
    & (Join-Path $setup 'Set-Games.ps1') -CatalogFile $catalog
    $beforeCatalog = (Get-FileHash (Join-Path $data 'games.json')).Hash
    'not json' | Set-Content (Join-Path $work 'bad-games.json') -Encoding UTF8
    $rejected = $false
    try { & (Join-Path $setup 'Set-Games.ps1') -CatalogFile (Join-Path $work 'bad-games.json') } catch { $rejected = $true }
    $global:LASTEXITCODE = 0
    if (-not $rejected -or (Get-FileHash (Join-Path $data 'games.json')).Hash -ne $beforeCatalog) { throw 'Invalid catalog replaced the valid one.' }
    Probe $visitorCredential 'Idle'
    Probe $outsiderCredential 'Denied'
    $pidBefore = Service-Pid
    Probe $visitorCredential 'Malformed'
    if ((Service-Pid) -ne $pidBefore) { throw 'Malformed pipe input restarted the service.' }
    [IO.File]::WriteAllText((Join-Path $data 'games.json'), '{')
    Probe $visitorCredential 'BadCatalog'
    & (Join-Path $setup 'Set-Games.ps1') -CatalogFile $catalog
    if ((Service-Pid) -ne $pidBefore) { throw 'Bad catalog restarted the service.' }
    Write-Host 'PASS: installed LocalSystem service, HTTPS device authentication, protected files/pipe, real catalog replacement and malformed-message resilience.'
    Set-Grant 'Running' 1 60000
    Wait-Until { (Read-Json (Join-Path $work 'report.json')).state -eq 'Running' } 'Running grant was not applied.'
    Stop-FixtureServer
    Start-Sleep -Seconds 3
    Probe $visitorCredential 'Running'
    $before = Read-Json (Join-Path $data 'session.json')
    Restart-Service ClubAgent
    Wait-Until { (Read-Json (Join-Path $data 'session.json')).state -eq 'Recovery' } 'Service did not recover conservatively.'
    Probe $visitorCredential 'Recovery'
    $recovered = Read-Json (Join-Path $data 'session.json')
    if ($recovered.remainingMs -le 0 -or $recovered.remainingMs -gt $before.remainingMs) { throw 'Recovery added or lost all paid time.' }
    Start-Sleep -Seconds 1
    if ((Read-Json (Join-Path $data 'session.json')).remainingMs -ne $recovered.remainingMs) { throw 'Recovery consumed time.' }
    Set-Grant 'Running' 2 10000
    Start-FixtureServer | Out-Null
    Wait-Until { $r = Read-Json (Join-Path $work 'report.json'); $r.state -eq 'Running' -and $r.revision -eq 2 } 'Explicit resume was not applied.'
    Stop-FixtureServer
    Wait-Until { (Read-Json (Join-Path $data 'session.json')).state -eq 'Completed' } 'Offline timer did not expire.' 20
    Probe $visitorCredential 'Completed'
    if ((Get-CimInstance Win32_Service -Filter "Name='ClubAgent'").State -ne 'Running') { throw 'Service stopped during offline expiry.' }
    Write-Host 'PASS: offline grant, restart into Recovery without added time, explicit resume and offline expiry; no visitor interactive Windows session was logged off.'
    & (Join-Path $setup 'Uninstall-Agent.ps1')
    Wait-Until { -not (Get-Service ClubAgent -ErrorAction SilentlyContinue) } 'Service was not removed.'
    if (Get-ScheduledTask -TaskName ClubVisitor -TaskPath '\' -ErrorAction SilentlyContinue) { throw 'Startup task remains after uninstall.' }
    if (Test-Path ('Cert:\LocalMachine\Root\' + $cert.Thumbprint)) { throw 'Owned certificate trust remains after uninstall.' }
    if (-not (Test-Path (Join-Path $data 'session.json')) -or -not (Test-Path (Join-Path $data 'agent.json'))) { throw 'Uninstall removed recovery data.' }
    Write-Host 'PASS: uninstall removed service, startup task and owned certificate trust; recovery data preserved.'
} finally {
    Stop-FixtureServer
    if ($attemptedInstall) { & (Join-Path $setup 'Uninstall-Agent.ps1') }
    foreach ($user in @($visitor, $outsider)) { if ($user) { Remove-LocalUser -SID $user.SID } }
    if ($cert) {
        foreach ($store in @('My','Root')) {
            $certPath = 'Cert:\LocalMachine\' + $store + '\' + $cert.Thumbprint
            if (Test-Path $certPath) { Remove-Item $certPath }
        }
    }
    foreach ($directory in @($programRoot, $dataRoot, $work, $shared)) {
        if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
    }
}
$global:LASTEXITCODE = 0
