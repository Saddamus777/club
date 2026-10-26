#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$server = (Resolve-Path (Join-Path $PSScriptRoot '..\Server')).Path
$configPath = Join-Path $server 'appsettings.Production.json'
if (Test-Path $configPath) {
    $config = Get-Content $configPath -Raw | ConvertFrom-Json
    $config.Club.Lan.Enabled = $false
    $config | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configPath -Encoding UTF8
}
Remove-NetFirewallRule -Name ClubPilotHttps -ErrorAction SilentlyContinue
Write-Host 'Restart the server to disable its LAN listener. Certificates and data were preserved.'
