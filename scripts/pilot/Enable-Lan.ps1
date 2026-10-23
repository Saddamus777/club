#Requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ServerAddress)
$ErrorActionPreference = 'Stop'
$ip = [Net.IPAddress]::Parse($ServerAddress)
if ([Net.IPAddress]::IsLoopback($ip) -or $ip.Equals([Net.IPAddress]::Any) -or $ip.Equals([Net.IPAddress]::IPv6Any)) { throw 'Use the fixed LAN IP of the admin PC.' }
$server = (Resolve-Path (Join-Path $PSScriptRoot '..\Server')).Path
$configPath = Join-Path $server 'appsettings.Production.json'
if (Test-Path $configPath) { throw 'LAN settings already exist; review the existing configuration instead of overwriting it.' }
$ownerSid = '*' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$tls = Join-Path $server '.data\tls'
New-Item -ItemType Directory -Force -Path $tls | Out-Null
& icacls.exe (Join-Path $server '.data') /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "${ownerSid}:(OI)(CI)F" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not protect server data. Run the server as the owning administrator.' }
$cert = New-SelfSignedCertificate -Subject 'CN=Club LAN pilot' -TextExtension @("2.5.29.17={text}IPAddress=$ServerAddress") -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy Exportable -CertStoreLocation 'Cert:\LocalMachine\My' -NotAfter (Get-Date).AddMonths(6)
$bytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes); $rng.Dispose()
$password = [Convert]::ToBase64String($bytes)
$pfx = Join-Path $tls 'server.pfx'
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $password -AsPlainText -Force) | Out-Null
$publicCert = Join-Path $PSScriptRoot 'club-server.cer'
Export-Certificate -Cert $cert -FilePath $publicCert | Out-Null
@{ AllowedHosts = "localhost;127.0.0.1;$ServerAddress"; Club = @{ Lan = @{ Enabled = $true; Address = $ServerAddress; Port = 5443; CertificatePath = $pfx; CertificatePassword = $password } } } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configPath -Encoding UTF8
& icacls.exe $configPath /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' "${ownerSid}:F" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not protect TLS settings.' }
New-NetFirewallRule -DisplayName 'Club pilot HTTPS LAN' -Name 'ClubPilotHttps' -Direction Inbound -Protocol TCP -LocalAddress $ServerAddress -LocalPort 5443 -RemoteAddress LocalSubnet -Profile Private -Action Allow | Out-Null
Write-Host "Restart the server. URL: https://${ServerAddress}:5443"
Write-Host "Public certificate: $publicCert"
Write-Host "Verify this thumbprint on the gaming PC: $($cert.Thumbprint)"
Write-Host 'Certificate is valid for six months. This pilot has no automatic certificate renewal.'
