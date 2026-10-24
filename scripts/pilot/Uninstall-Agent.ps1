#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'VisitorStartup.ps1')
$data = Join-Path $env:ProgramData 'Club\Agent'
$manifest = Join-Path $data 'installation.json'
if (Test-Path $manifest) {
    $installed = Get-Content $manifest -Raw | ConvertFrom-Json
    if ($installed.startupTask -eq 'ClubVisitor') {
        Remove-ClubVisitorStartup -VisitorSid $installed.visitorSid -Executable (Join-Path $env:ProgramFiles 'Club\Visitor\Club.Visitor.exe')
    }
}
$service = Get-Service ClubAgent -ErrorAction SilentlyContinue
if ($service) {
    Stop-Service ClubAgent -Force
    & sc.exe delete ClubAgent | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not remove ClubAgent.' }
    $service.Dispose()
}
$data = Join-Path $env:ProgramData 'Club\Agent'
$manifest = Join-Path $data 'installation.json'
if (Test-Path $manifest) {
    $installed = Get-Content $manifest -Raw | ConvertFrom-Json
    if ($installed.addedTrust -and $installed.certificateThumbprint -match '^[A-Fa-f0-9]{40}$') {
        $cert = 'Cert:\LocalMachine\Root\' + $installed.certificateThumbprint
        if (Test-Path $cert) { Remove-Item $cert }
    }
}
Write-Host 'Service and owned startup task removed. Program files, catalog, configuration and checkpoint were preserved for diagnosis/recovery.'
Write-Host 'Do not resume operation without a running service. Finish pending sessions on the server before rebinding.'
