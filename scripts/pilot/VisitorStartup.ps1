# Shared by installation, removal and the Windows integration test.
function Register-ClubVisitorStartup {
    param([Parameter(Mandatory)][string]$VisitorSid, [Parameter(Mandatory)][string]$Executable)
    if (Get-ScheduledTask -TaskName 'ClubVisitor' -TaskPath '\' -ErrorAction SilentlyContinue) {
        throw 'ClubVisitor startup task already exists; it will not be overwritten.'
    }
    if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw 'Visitor executable not found.' }
    $action = New-ScheduledTaskAction -Execute $Executable -WorkingDirectory (Split-Path -Parent $Executable)
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $VisitorSid
    $principal = New-ScheduledTaskPrincipal -UserId $VisitorSid -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName 'ClubVisitor' -TaskPath '\' -Action $action -Trigger $trigger `
        -Principal $principal -Settings $settings -Description 'Club visitor launcher (pilot)' -ErrorAction Stop | Out-Null
}

function Remove-ClubVisitorStartup {
    param([Parameter(Mandatory)][string]$VisitorSid, [Parameter(Mandatory)][string]$Executable)
    $task = Get-ScheduledTask -TaskName 'ClubVisitor' -TaskPath '\' -ErrorAction SilentlyContinue
    if (-not $task) { return }
    # Compare SID even when Task Scheduler returns a translated account name.
    $userId = $task.Principal.UserId
    if ($userId -notmatch '^S-1-') {
        $userId = (New-Object Security.Principal.NTAccount($userId)).Translate([Security.Principal.SecurityIdentifier]).Value
    }
    if ($userId -ne $VisitorSid -or @($task.Actions).Count -ne 1 -or $task.Actions[0].Execute -ne $Executable `
        -or $task.Actions[0].Arguments -or $task.Description -ne 'Club visitor launcher (pilot)') {
        throw 'ClubVisitor task was changed; inspect it before removing it manually.'
    }
    Unregister-ScheduledTask -TaskName 'ClubVisitor' -TaskPath '\' -Confirm:$false -ErrorAction Stop
}
