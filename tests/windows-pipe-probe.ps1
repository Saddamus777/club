param([Parameter(Mandatory)][ValidateSet('Idle','Denied','Malformed','BadCatalog','Running','Recovery','Completed')][string]$Mode)
$ErrorActionPreference = 'Stop'
function Open-Pipe {
    $pipe = New-Object IO.Pipes.NamedPipeClientStream('.', 'Club.Visitor.v1',
        [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous,
        [Security.Principal.TokenImpersonationLevel]::Impersonation)
    try { $pipe.Connect(10000) } catch { $pipe.Dispose(); throw }
    return $pipe
}
function Read-Bytes($Pipe, [int]$Length) {
    $buffer = New-Object byte[] $Length
    $offset = 0
    while ($offset -lt $Length) {
        $read = $Pipe.ReadAsync($buffer, $offset, $Length - $offset)
        if (-not $read.Wait(10000)) { throw 'Pipe response timeout.' }
        $count = $read.GetAwaiter().GetResult()
        if ($count -eq 0) { throw 'Unexpected pipe EOF.' }
        $offset += $count
    }
    return ,$buffer
}
function Request($Pipe, [string]$Path, $Body = $null) {
    $json = $null
    if ($null -ne $Body) { $json = $Body | ConvertTo-Json -Compress }
    $bytes = [Text.Encoding]::UTF8.GetBytes((@{ path = $Path; json = $json } | ConvertTo-Json -Compress))
    $prefix = [BitConverter]::GetBytes([int]$bytes.Length)
    $Pipe.Write($prefix, 0, 4); $Pipe.Write($bytes, 0, $bytes.Length); $Pipe.Flush()
    $length = [BitConverter]::ToInt32((Read-Bytes $Pipe 4), 0)
    if ($length -lt 1 -or $length -gt 1048576) { throw 'Invalid response size.' }
    return ([Text.Encoding]::UTF8.GetString((Read-Bytes $Pipe $length)) | ConvertFrom-Json)
}
function Assert-PrivateFile([string]$Path) {
    $denied = $false
    try { [IO.File]::ReadAllText($Path) | Out-Null }
    catch [UnauthorizedAccessException] { $denied = $true }
    if (-not $denied) { throw 'Visitor could read private agent data.' }
}
try {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Probe must run as a standard user.' }
    Assert-PrivateFile (Join-Path $env:ProgramData 'Club\Agent\agent.json')
    Assert-PrivateFile (Join-Path $env:ProgramData 'Club\Agent\session.json')
    $denied = $false
    try { [IO.File]::WriteAllText((Join-Path $env:ProgramFiles 'Club\visitor-write-test.txt'), 'forbidden') }
    catch [UnauthorizedAccessException] { $denied = $true }
    if (-not $denied) { throw 'Visitor could write to installed program directory.' }
    if ($Mode -eq 'Denied') {
        $denied = $false
        try { $pipe = Open-Pipe; $pipe.Dispose() }
        catch [UnauthorizedAccessException] { $denied = $true }
        if (-not $denied) { throw 'Another Windows user was not rejected by the pipe ACL.' }
        Write-Output 'PASS: another standard user denied by pipe ACL and file ACL.'
        exit 0
    }
    if ($Mode -eq 'Malformed') {
        foreach ($length in @(-1, 0, 16385)) {
            $pipe = Open-Pipe
            try { $prefix = [BitConverter]::GetBytes([int]$length); $pipe.Write($prefix, 0, 4); $pipe.Flush() }
            finally { $pipe.Dispose() }
        }
        $pipe = Open-Pipe
        try {
            $prefix = [BitConverter]::GetBytes([int]1); $pipe.Write($prefix, 0, 4); $pipe.WriteByte(123); $pipe.Flush()
        } finally { $pipe.Dispose() }
    }
    $pipe = Open-Pipe
    try {
        $owner = $pipe.GetAccessControl().GetOwner([Security.Principal.SecurityIdentifier])
        if ($owner.Value -ne 'S-1-5-18') { throw 'Pipe owner is not SYSTEM.' }
        $response = Request $pipe '/local/status'
        if ($response.status -ne 200) { throw 'Local status failed.' }
        $status = $response.json | ConvertFrom-Json
        if ($Mode -eq 'Running') {
            # TCP failure detection depends on the in-flight HTTP timeout, not a fixed sleep.
            $deadline = [DateTime]::UtcNow.AddSeconds(15)
            while ($status.connected -and [DateTime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 250
                $status = (Request $pipe '/local/status').json | ConvertFrom-Json
            }
            $remaining = $status.grant.remainingMs
            Start-Sleep -Seconds 1
            $status = (Request $pipe '/local/status').json | ConvertFrom-Json
            if ($status.grant.remainingMs -ge $remaining) { throw 'Offline timer did not advance.' }
        }
        $expected = $Mode
        if ($Mode -in @('Malformed','BadCatalog')) { $expected = 'Idle' }
        if ($status.grant.state -ne $expected) { throw "Expected $expected, got $($status.grant.state)." }
        $catalog = Request $pipe '/local/games'
        if ($Mode -eq 'BadCatalog') {
            if ($catalog.status -ne 400 -or (Request $pipe '/local/status').status -ne 200) { throw 'Malformed catalog interrupted service.' }
        } else {
            $games = @($catalog.json | ConvertFrom-Json)
            if ($catalog.status -ne 200 -or $games.Count -ne 1 -or $games[0].id -ne 'sample') { throw 'Wrong protected catalog.' }
            $launch = Request $pipe '/local/games/launch' @{ id = 'sample'; executable = 'C:\ignored.exe' }
            if ($Mode -eq 'Running') {
                if ($launch.status -ne 200 -or $status.connected -or $status.grant.remainingMs -le 0) {
                    throw "Offline grant failed: launch=$($launch.status), connected=$($status.connected), remaining=$($status.grant.remainingMs)."
                }
                $approved = $launch.json | ConvertFrom-Json
                if ($approved.game.executable -ne (Join-Path $env:ProgramFiles 'Club\Visitor\Club.Visitor.exe')) { throw 'Visitor supplied executable was used.' }
            } elseif ($launch.status -ne 409) { throw 'Launch permitted without a running grant.' }
        }
        Write-Output "PASS: standard visitor $Mode, SYSTEM pipe owner, private files protected."
    } finally { $pipe.Dispose() }
    exit 0
} catch { Write-Output ('FAIL: ' + $_.Exception.Message); exit 1 }
