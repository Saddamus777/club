#Requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
# Read-only workstation inspection. The only writes are to a new export directory.
# No CSP, GPO, registry, AppLocker, task, service or account setting is changed.
$data = Join-Path $env:ProgramData 'Club\Agent'
$manifest = Get-Content -LiteralPath (Join-Path $data 'installation.json') -Raw | ConvertFrom-Json
$sid = New-Object Security.Principal.SecurityIdentifier($manifest.visitorSid)
$visitor = Get-LocalUser -SID $sid
$admins = Get-LocalGroup -SID 'S-1-5-32-544'
if (Get-LocalGroupMember -Group $admins | Where-Object { $_.SID -eq $sid }) {
    throw 'A kiosk draft must never target an administrator. Correct the visitor account first.'
}
# Use the matching bundle CLI: an older installed agent may not recognize the export option.
$agentExe = Join-Path $PSScriptRoot '..\Agent\Club.Agent.exe'
$visitorExe = Join-Path $env:ProgramFiles 'Club\Visitor\Club.Visitor.exe'
$catalogFile = Join-Path $data 'games.json'
if (-not (Test-Path -LiteralPath $catalogFile -PathType Leaf)) { throw 'The installed catalog is missing.' }
& $agentExe --validate-games $catalogFile
if ($LASTEXITCODE -ne 0) { throw 'Repair the installed game catalog before exporting a draft.' }
$games = Get-Content -LiteralPath $catalogFile -Raw | ConvertFrom-Json
$os = Get-CimInstance Win32_OperatingSystem
$edition = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').EditionID
$uac = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System').EnableLUA
$service = Get-CimInstance Win32_Service -Filter "Name='ClubAgent'"
$startup = Get-ScheduledTask -TaskName ClubVisitor -TaskPath '\' -ErrorAction SilentlyContinue
$startupSid = $null
$triggerSid = $null
function Resolve-IdentitySid([string]$Identity) {
    if ($Identity -match '^S-1-') { return $Identity }
    return (New-Object Security.Principal.NTAccount($Identity)).Translate([Security.Principal.SecurityIdentifier]).Value
}
if ($startup) {
    $startupSid = Resolve-IdentitySid $startup.Principal.UserId
    if (@($startup.Triggers).Count -eq 1 -and $startup.Triggers[0].CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger' -and $startup.Triggers[0].UserId) {
        $triggerSid = Resolve-IdentitySid $startup.Triggers[0].UserId
    }
}
$checks = @(
    @{ id = 'windows11'; passed = ($os.ProductType -eq 1 -and [int]$os.BuildNumber -ge 22621); detail = ($os.Caption + ' / ' + $os.BuildNumber) },
    @{ id = 'proEdition'; passed = ($edition -in @('Professional', 'ProfessionalN', 'ProfessionalEducation', 'ProfessionalEducationN', 'ProfessionalWorkstation', 'ProfessionalWorkstationN')); detail = $edition },
    @{ id = 'uac'; passed = ($uac -eq 1); detail = 'EnableLUA must be 1' },
    @{ id = 'visitorEnabled'; passed = [bool]$visitor.Enabled; detail = $visitor.Name },
    @{ id = 'agentRunning'; passed = ($null -ne $service -and $service.State -eq 'Running' -and $service.StartMode -eq 'Auto' -and $service.StartName -eq 'LocalSystem'); detail = 'ClubAgent: running, automatic, LocalSystem' },
    @{ id = 'visitorStartup'; passed = ($null -ne $startup -and $startupSid -eq $sid.Value -and $triggerSid -eq $sid.Value -and $startup.Triggers[0].Enabled -and $startup.Settings.Enabled -and $startup.Principal.RunLevel -eq 'Limited' -and $startup.Principal.LogonType -eq 'Interactive' -and @($startup.Actions).Count -eq 1 -and $startup.Actions[0].Execute -eq $visitorExe -and -not $startup.Actions[0].Arguments); detail = 'ClubVisitor: exact SID, logon trigger and executable, limited rights, enabled' }
)
$pending = @(
    'Test Assigned Access and its removal on a separate, fully updated Windows 11 Pro workstation.',
    'Inspect existing Assigned Access, Shell Launcher, MDM/GPO and AppLocker policies as SYSTEM before any application; do not overwrite existing policies.',
    'Check effective visitor permissions on every EXE, DLL and parent folder, including game launchers and updater dependencies.',
    'Test a separate Windows administrator login and recovery path; some Assigned Access settings affect other users.',
    'Implement and test denial of unpaid access: a static allowlist does not bind game execution to a paid session.',
    'Test game/browser account cleanup, physical LAN failures, Windows logoff and game shutdown.'
)
$report = [ordered]@{
    schemaVersion = 1
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    visitorSid = $sid.Value
    localAccount = ($env:COMPUTERNAME + '\' + $visitor.Name)
    checks = $checks
    setupPrerequisitesPassed = (@($checks | Where-Object { -not $_.passed }).Count -eq 0)
    policyApplied = $false
    readyForOperation = $false
    pending = $pending
}
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new output directory; existing exports are never overwritten.' }
$output = New-Item -ItemType Directory -Path $OutputDirectory
$requestFile = Join-Path $output.FullName 'request.tmp.json'
try {
    # No agent.json, device key, password, game arguments or certificate is included in the export.
    $entries = @(foreach ($game in $games) { @{ id = $game.id; name = $game.name; executable = $game.executable; arguments = @() } })
    @{ localAccount = $report.localAccount; visitorExecutable = $visitorExe; games = $entries } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $requestFile -Encoding UTF8
    & $agentExe --export-kiosk-draft $requestFile (Join-Path $output.FullName 'assigned-access.draft.xml')
    if ($LASTEXITCODE -ne 0) { throw 'Draft export failed. No Windows policy was applied.' }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output.FullName 'readiness.json') -Encoding UTF8
} finally {
    if (Test-Path -LiteralPath $requestFile) { Remove-Item -LiteralPath $requestFile }
}
Write-Host ('Exported draft and readiness.json to ' + $output.FullName)
if (-not $report.setupPrerequisitesPassed) { Write-Warning 'Some setup prerequisites failed. Read readiness.json before the Windows 11 trial.' }
Write-Warning 'No policy was applied. The draft does not prevent unpaid use and is not an operational kiosk configuration.'
