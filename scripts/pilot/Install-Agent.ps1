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
$serverUri = [Uri]$ServerUrl
if ($serverUri.Scheme -ne 'https' -or $serverUri.AbsolutePath -ne '/' -or $serverUri.UserInfo) { throw 'Use an HTTPS server URL without credentials or a path.' }
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
$data = Join-Path $env:ProgramData 'Club\Agent'
if (Test-Path (Join-Path $data 'agent.json')) { throw 'Saved agent data exists. Reuse the same binding or archive it explicitly before reinstalling.' }
New-Item -ItemType Directory -Force -Path $root, $data | Out-Null
# Remove inherited write access BEFORE copying the device key.
& icacls.exe $root /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not protect program directory.' }
& icacls.exe $data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not protect agent data.' }
Copy-Item -Path (Join-Path $PSScriptRoot '..\Agent') -Destination $root -Recurse -Force
Copy-Item -Path (Join-Path $PSScriptRoot '..\Visitor') -Destination $root -Recurse -Force
$config = @{ serverUrl = $ServerUrl; computerId = $binding.computerId; token = $binding.token; visitorSid = $visitor.SID.Value }
$config | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'agent.json') -Encoding UTF8
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
Start-Service ClubAgent
Write-Host 'Installed. Start Club.Visitor.exe as the specified standard user. No Windows shell/kiosk policy was changed.'
Write-Host 'Delete the transported binding file after checking the installation. Keep the admin copy secure.'
