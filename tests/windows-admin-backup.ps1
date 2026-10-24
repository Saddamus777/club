#Requires -RunAsAdministrator
# A disposable real server on loopback, never an existing installation.
$ErrorActionPreference = 'Stop'
$serverExe = (Resolve-Path 'artifacts/pilot/Server/Club.Server.exe').Path
$adminExe = (Resolve-Path 'artifacts/pilot/Admin/Club.Admin.exe').Path
$work = Join-Path $env:TEMP ('club admin-' + [Guid]::NewGuid().ToString('N'))
$server = $null
$socket = New-Object Net.Sockets.TcpClient
try { $socket.Connect('127.0.0.1', 5080); throw 'Port 5080 is occupied. Refusing to test against an existing server.' }
catch [Net.Sockets.SocketException] { }
finally { $socket.Dispose() }
function Stop-TestServer {
    if ($script:server) {
        if (-not $script:server.HasExited) { Stop-Process -Id $script:server.Id -Force; $script:server.WaitForExit() }
        $script:server.Dispose(); $script:server = $null
    }
}
function Start-TestServer([string]$Directory) {
    $script:server = Start-Process -FilePath $serverExe -WorkingDirectory $work -ArgumentList ('--Club:DataDirectory "' + $Directory + '" --Club:DisableWorker true') `
        -PassThru -RedirectStandardOutput (Join-Path $work 'server.out') -RedirectStandardError (Join-Path $work 'server.err')
    $deadline = [DateTime]::UtcNow.AddSeconds(40)
    do {
        if ($script:server.HasExited) { Get-Content (Join-Path $work 'server.err') | Write-Host; throw 'Test server exited.' }
        try { return Invoke-RestMethod 'http://127.0.0.1:5080/api/security' -TimeoutSec 2 } catch { Start-Sleep -Milliseconds 250 }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Test server did not start.'
}
function Token($Session) {
    return (Invoke-RestMethod 'http://127.0.0.1:5080/api/security' -WebSession $Session -TimeoutSec 5).token
}
function Post([string]$Path, $Body, $Session) {
    $token = Token $Session
    return Invoke-RestMethod ('http://127.0.0.1:5080' + $Path) -Method Post -WebSession $Session -Headers @{ 'X-CSRF-TOKEN' = $token } `
        -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Depth 6) -TimeoutSec 15
}
function Native([string]$Exe, [string[]]$Arguments, [int]$Expected = 0) {
    $prefix = Join-Path $work ([Guid]::NewGuid().ToString('N'))
    $quoted = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $process = Start-Process -FilePath $Exe -ArgumentList $quoted -WorkingDirectory $work -PassThru `
        -RedirectStandardOutput ($prefix + '.out') -RedirectStandardError ($prefix + '.err')
    try {
        $handle = $process.Handle
        if (-not $process.WaitForExit(120000)) { $process.Kill(); throw 'Native backup operation timed out.' }
        $process.WaitForExit()
        if ($process.ExitCode -ne $Expected) {
            Get-Content ($prefix + '.err') | Write-Host
            throw "Native backup operation returned $($process.ExitCode), expected $Expected."
        }
    } finally { $process.Dispose() }
}
try {
    New-Item -ItemType Directory -Path $work | Out-Null
    & icacls.exe $work /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not protect fixture credentials.' }
    $initial = Start-TestServer (Join-Path $work 'source')
    if ($initial.ownerExists) { throw 'Test database is not fresh.' }
    $password = [Guid]::NewGuid().ToString('N') + '!aA7'
    $owner = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    Post '/api/setup/owner' @{ username = 'fixture-owner'; password = $password; confirmPassword = $password } $owner | Out-Null
    Post '/api/login' @{ username = 'fixture-owner'; password = $password } $owner | Out-Null
    Post '/api/admin/staff' @{ username = 'fixture-admin'; password = $password } $owner | Out-Null
    $visitor = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $customer = Post '/api/register' @{ username = 'fixture-visitor'; password = $password } $visitor
    Post '/api/admin/topups' @{ customerId = $customer.id; amountMinor = 12345; method = 'Cash'; operationId = [Guid]::NewGuid().ToString() } $owner | Out-Null
    $copy = Join-Path $work 'copy with spaces.db'
    $requestFile = Join-Path $work 'private-request.json'
    @{ username = 'fixture-admin'; password = $password; destination = $copy } | ConvertTo-Json | Set-Content -LiteralPath $requestFile -Encoding UTF8
    Native $adminExe @('--download-backup-smoke', $requestFile)
    $hash = (Get-FileHash -LiteralPath $copy).Hash
    Native $adminExe @('--download-backup-smoke', $requestFile) 1
    if ((Get-FileHash -LiteralPath $copy).Hash -ne $hash) { throw 'Existing backup changed.' }
    $deniedCopy = Join-Path $work 'denied.db'
    @{ username = 'fixture-visitor'; password = $password; destination = $deniedCopy } | ConvertTo-Json | Set-Content -LiteralPath $requestFile -Encoding UTF8
    Native $adminExe @('--download-backup-smoke', $requestFile) 1
    if (Test-Path -LiteralPath $deniedCopy) { throw 'Customer downloaded a backup.' }
    $corrupt = Join-Path $work 'corrupt.db'
    'corrupt' | Set-Content -LiteralPath $corrupt
    Native $adminExe @('--verify-backup-smoke', $corrupt) 1
    if (Get-ChildItem -LiteralPath $work -Filter '*.part' -Force) { throw 'Partial backup file remains.' }
    Stop-TestServer
    $restored = Join-Path $work 'restored'
    Native $serverExe @('--restore-backup', $copy, '--Club:DataDirectory', $restored)
    # Read-only verification must not require a working LAN certificate/configuration.
    Native $serverExe @('--verify-backup', (Join-Path $restored 'club.db'), '--Club:Lan:Enabled', 'true')
    Start-TestServer $restored | Out-Null
    $recoveredOwner = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    Post '/api/login' @{ username = 'fixture-owner'; password = $password } $recoveredOwner | Out-Null
    $state = Invoke-RestMethod 'http://127.0.0.1:5080/api/state' -WebSession $recoveredOwner -TimeoutSec 5
    if ($state.customers.Count -ne 1 -or $state.customers[0].balanceMinor -ne 12345 -or $state.employees.Count -ne 2) {
        throw 'Restored database lost the balance or staff accounts.'
    }
    Write-Host 'PASS: real Windows server, native Admin authenticated download and full verification, existing backup preserved, customer denied, corruption rejected, separate restored server has exact balance and staff accounts.'
} finally {
    Stop-TestServer
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
$global:LASTEXITCODE = 0
