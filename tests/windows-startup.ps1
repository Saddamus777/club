#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\scripts\pilot\VisitorStartup.ps1')
$account = 'ClubTest' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$visitorExe = (Resolve-Path 'artifacts/pilot/Visitor/Club.Visitor.exe').Path
$agentExe = (Resolve-Path 'artifacts/pilot/Agent/Club.Agent.exe').Path
$temp = Join-Path $env:TEMP ([Guid]::NewGuid().ToString('N') + '.json')
$user = $null
$registered = $false
function Resolve-AccountSid([string]$Value) {
    if ($Value -match '^S-1-') { return $Value }
    return (New-Object Security.Principal.NTAccount($Value)).Translate([Security.Principal.SecurityIdentifier]).Value
}
try {
    $password = ConvertTo-SecureString ([Guid]::NewGuid().ToString('N') + '!aA7') -AsPlainText -Force
    $user = New-LocalUser -Name $account -Password $password -Description 'Disposable Club CI test account'
    Register-ClubVisitorStartup -VisitorSid $user.SID.Value -Executable $visitorExe
    $registered = $true
    [xml]$xml = Export-ScheduledTask -TaskName 'ClubVisitor' -TaskPath '\'
    $principal = $xml.Task.Principals.Principal
    if ((Resolve-AccountSid $principal.UserId) -ne $user.SID.Value -or $principal.RunLevel -ne 'LeastPrivilege' -or $principal.LogonType -ne 'InteractiveToken') {
        throw "Incorrect startup principal: user=$($principal.UserId), level=$($principal.RunLevel), logon=$($principal.LogonType)"
    }
    if ((Resolve-AccountSid $xml.Task.Triggers.LogonTrigger.UserId) -ne $user.SID.Value -or $xml.Task.Actions.Exec.Command -ne $visitorExe) {
        throw 'Incorrect logon trigger or executable.'
    }
    if ($xml.Task.Settings.ExecutionTimeLimit -ne 'PT0S') { throw 'Visitor would be killed by a task time limit.' }
    $rejected = $false
    try { Register-ClubVisitorStartup -VisitorSid $user.SID.Value -Executable $visitorExe } catch { $rejected = $true }
    if (-not $rejected) { throw 'An existing task was overwritten.' }
    $rejected = $false
    try { Remove-ClubVisitorStartup -VisitorSid 'S-1-5-18' -Executable $visitorExe } catch { $rejected = $true }
    if (-not $rejected) { throw 'A task for a different user was removed.' }
    Remove-ClubVisitorStartup -VisitorSid $user.SID.Value -Executable $visitorExe
    $registered = $false
    if (Get-ScheduledTask -TaskName 'ClubVisitor' -TaskPath '\' -ErrorAction SilentlyContinue) { throw 'Task removal failed.' }
    # Exercise the published agent's real JSON reader and validation CLI, including PS 5.1 UTF-8 BOM.
    ConvertTo-Json -InputObject @(@{ id = 'sample'; name = 'Sample'; executable = $visitorExe; arguments = @('two words') }) -Depth 4 |
        Set-Content -LiteralPath $temp -Encoding UTF8
    & $agentExe --validate-games $temp
    if ($LASTEXITCODE -ne 0) { throw 'Valid catalog was rejected.' }
    '[]' | Set-Content -LiteralPath $temp -Encoding UTF8
    & $agentExe --validate-games $temp
    if ($LASTEXITCODE -ne 0) { throw 'Empty catalog was rejected.' }
    '[{"id":"bad","name":"Bad","executable":"relative.exe","arguments":[]}]' | Set-Content -LiteralPath $temp -Encoding UTF8
    & $agentExe --validate-games $temp 2>$null
    if ($LASTEXITCODE -eq 0) { throw 'Relative executable path was accepted.' }
    Write-Host 'PASS: limited visitor startup registration/removal/collision and catalog validation. No interactive logon or game execution was tested.'
} finally {
    if ($registered) { Remove-ClubVisitorStartup -VisitorSid $user.SID.Value -Executable $visitorExe }
    if ($user) { Remove-LocalUser -SID $user.SID }
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp }
}
