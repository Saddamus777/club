#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BindingFile,
    [Parameter(Mandatory)][string]$ServerUrl,
    [Parameter(Mandatory)][string]$VisitorUser,
    [Parameter(Mandatory)][string]$ServerCertificate,
    [Parameter(Mandatory)][string]$CertificateThumbprint
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'VisitorStartup.ps1')
if (Get-ScheduledTask -TaskName 'ClubVisitor' -TaskPath '\' -ErrorAction SilentlyContinue) { throw 'ClubVisitor startup task already exists. Inspect it before installing.' }
$serverUri = [Uri]$ServerUrl
if (-not $serverUri.IsAbsoluteUri -or $serverUri.Scheme -ne 'https' -or $serverUri.AbsolutePath -ne '/' -or $serverUri.UserInfo -or $serverUri.Query -or $serverUri.Fragment) { throw 'Use an HTTPS server URL without credentials, path, query or fragment.' }
$visitor = Get-LocalUser -Name $VisitorUser
if ($visitor.SID -eq [Security.Principal.WindowsIdentity]::GetCurrent().User) { throw 'Use a separate standard Windows visitor account.' }
$admins = Get-LocalGroup -SID 'S-1-5-32-544'
if (Get-LocalGroupMember -Group $admins | Where-Object { $_.SID -eq $visitor.SID }) { throw 'The visitor must not be an administrator.' }
$binding = Get-Content -LiteralPath $BindingFile -Raw | ConvertFrom-Json
if ($binding.token -notmatch '^[0-9a-fA-F]{64}$' -or -not $binding.computerId) { throw 'Invalid device binding.' }
$cert = New-Object Security.Cryptography.X509Certificates.X509Certificate2((Resolve-Path $ServerCertificate).Path)
if (($cert.Thumbprint -replace ' ', '') -ne ($CertificateThumbprint -replace ' ', '')) { throw 'Certificate thumbprint mismatch. Verify it on the admin PC.' }
if ($cert.NotAfter -le (Get-Date)) { throw 'Certificate has expired.' }
if (Get-Service ClubAgent -ErrorAction SilentlyContinue) { throw 'ClubAgent already exists. Stop pilot sessions and uninstall it before replacing binaries.' }
$root = Join-Path $env:ProgramFiles 'Club'
$dataRoot = Join-Path $env:ProgramData 'Club'
$data = Join-Path $env:ProgramData 'Club\Agent'
if ((Test-Path -LiteralPath $root) -or (Test-Path -LiteralPath $dataRoot)) {
    throw 'Club program/data directory already exists. Archive the previous installation explicitly; existing directories will not be adopted or overwritten.'
}
function New-ProtectedDirectory([string]$Path, [bool]$AllowRead) {
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            (New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    if ($AllowRead) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            (New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')), 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    # Create with the final ACL, including the parent of the secret directory.
    if ($PSVersionTable.PSEdition -eq 'Core') { [IO.FileSystemAclExtensions]::CreateDirectory($acl, $Path) | Out-Null }
    else { [IO.Directory]::CreateDirectory($Path, $acl) | Out-Null }
    $actual = Get-Acl -LiteralPath $Path
    $expectedRules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | ForEach-Object { $_.IdentityReference.Value + ':' + $_.FileSystemRights + ':' + $_.InheritanceFlags + ':' + $_.PropagationFlags + ':' + $_.AccessControlType + ':' + $_.IsInherited } | Sort-Object)
    $actualRules = @($actual.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | ForEach-Object { $_.IdentityReference.Value + ':' + $_.FileSystemRights + ':' + $_.InheritanceFlags + ':' + $_.PropagationFlags + ':' + $_.AccessControlType + ':' + $_.IsInherited } | Sort-Object)
    if (((Get-Item -LiteralPath $Path).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        -not $actual.AreAccessRulesProtected -or $actual.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-32-544' -or
        @(Compare-Object $expectedRules $actualRules).Count -ne 0) {
        throw 'Directory ownership or permissions changed during installation. No device key was written.'
    }
}
New-ProtectedDirectory $root $true
New-ProtectedDirectory $dataRoot $false
New-ProtectedDirectory $data $false
Copy-Item -Path (Join-Path $PSScriptRoot '..\Agent') -Destination $root -Recurse -Force
Copy-Item -Path (Join-Path $PSScriptRoot '..\Visitor') -Destination $root -Recurse -Force
$config = @{ serverUrl = $ServerUrl; computerId = $binding.computerId; token = $binding.token; visitorSid = $visitor.SID.Value }
$config | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'agent.json') -Encoding UTF8
if (-not (Test-Path (Join-Path $data 'games.json'))) {
    '[]' | Set-Content -LiteralPath (Join-Path $data 'games.json') -Encoding UTF8
}
$alreadyTrusted = Test-Path ('Cert:\LocalMachine\Root\' + $cert.Thumbprint)
if (-not $alreadyTrusted) { Import-Certificate -FilePath $ServerCertificate -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null }
@{ certificateThumbprint = $cert.Thumbprint; addedTrust = (-not $alreadyTrusted); visitorSid = $visitor.SID.Value } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'installation.json') -Encoding UTF8
$exe = Join-Path $root 'Agent\Club.Agent.exe'
New-Service -Name ClubAgent -BinaryPathName ('"' + $exe + '"') -DisplayName 'Club gaming PC agent (pilot)' -StartupType Automatic | Out-Null
& sc.exe failure ClubAgent reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not configure service recovery.' }
& sc.exe failureflag ClubAgent 1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not configure failure recovery.' }
$visitorExe = Join-Path $root 'Visitor\Club.Visitor.exe'
Register-ClubVisitorStartup -VisitorSid $visitor.SID.Value -Executable $visitorExe
@{ certificateThumbprint = $cert.Thumbprint; addedTrust = (-not $alreadyTrusted); visitorSid = $visitor.SID.Value; startupTask = 'ClubVisitor' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'installation.json') -Encoding UTF8
Start-Service ClubAgent
$installedService = Get-Service ClubAgent
try { $installedService.WaitForStatus('Running', [TimeSpan]::FromSeconds(30)) }
finally { $installedService.Dispose() }
Write-Host 'Installed. Visitor starts at the next logon of the specified standard user, with limited rights.'
Write-Host 'Configure installed games with Set-Games.ps1. No Windows shell/kiosk/autologon policy was changed.'
Write-Host 'Delete the transported binding file after checking the installation. Keep the admin copy secure.'
