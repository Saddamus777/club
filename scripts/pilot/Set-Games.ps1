#Requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory)][string]$CatalogFile)
$ErrorActionPreference = 'Stop'
$data = Join-Path $env:ProgramData 'Club\Agent'
if (-not (Test-Path (Join-Path $data 'agent.json'))) { throw 'Install the agent first.' }
$exe = Join-Path $env:ProgramFiles 'Club\Agent\Club.Agent.exe'
# Stage and validate the exact bytes being installed inside the protected agent directory.
$temp = Join-Path $data ('games-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    Copy-Item -LiteralPath $CatalogFile -Destination $temp
    & $exe --validate-games $temp
    if ($LASTEXITCODE -ne 0) { throw 'Catalog validation failed. The previous catalog was preserved.' }
    Move-Item -LiteralPath $temp -Destination (Join-Path $data 'games.json') -Force
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp }
}
Write-Host 'Catalog installed. Click the games list button in Visitor. Service restart is not required.'
