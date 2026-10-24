$ErrorActionPreference = 'Stop'
$agentExe = (Resolve-Path 'artifacts/pilot/Agent/Club.Agent.exe').Path
$visitorExe = (Resolve-Path 'artifacts/pilot/Visitor/Club.Visitor.exe').Path
$work = Join-Path $env:TEMP ('club-kiosk-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $work | Out-Null
    $game = Join-Path $work 'A&B.exe'
    Copy-Item -LiteralPath $visitorExe -Destination $game
    $request = @{ localAccount = 'CLUB-PC\Visitor'; visitorExecutable = $visitorExe; games = @(
        @{ id = 'game'; name = 'Game'; executable = $game; arguments = @('--secret-argument') },
        @{ id = 'alias'; name = 'Alias'; executable = $game.ToUpperInvariant(); arguments = @() }
    ) }
    $inputFile = Join-Path $work 'request.json'
    $outputFile = Join-Path $work 'draft.xml'
    & $agentExe --export-kiosk-draft $inputFile
    if ($LASTEXITCODE -ne 1) { throw 'Incomplete maintenance command was not rejected before service startup.' }
    $request | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $inputFile -Encoding UTF8
    & $agentExe --export-kiosk-draft $inputFile $outputFile
    if ($LASTEXITCODE -ne 0) { throw 'Draft CLI failed with a valid request (PS 5.1 BOM).' }
    [xml]$draft = Get-Content -LiteralPath $outputFile -Raw
    if ($draft.AssignedAccessConfiguration.Configs.Config.Account -ne $request.localAccount -or
        @($draft.AssignedAccessConfiguration.Profiles.Profile.AllAppsList.AllowedApps.App).Count -ne 2 -or
        $draft.OuterXml.Contains('--secret-argument')) { throw 'Wrong draft scope, duplicated EXE or leaked arguments.' }
    $original = (Get-FileHash -LiteralPath $outputFile).Hash
    & $agentExe --export-kiosk-draft $inputFile $outputFile
    if ($LASTEXITCODE -ne 1 -or (Get-FileHash -LiteralPath $outputFile).Hash -ne $original) { throw 'Existing draft was overwritten.' }
    $missingOutput = Join-Path $work 'must-not-exist.xml'
    Remove-Item -LiteralPath $game
    & $agentExe --export-kiosk-draft $inputFile $missingOutput
    if ($LASTEXITCODE -ne 1 -or (Test-Path -LiteralPath $missingOutput)) { throw 'Missing EXE was accepted or left a draft.' }
    $request.games = @()
    $request.localAccount = 'CLUB-PC\*'
    $request | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $inputFile -Encoding UTF8
    & $agentExe --export-kiosk-draft $inputFile $missingOutput
    if ($LASTEXITCODE -ne 1 -or (Test-Path -LiteralPath $missingOutput)) { throw 'Wildcard account was accepted.' }
    $global:LASTEXITCODE = 0
    Write-Host 'PASS: real Windows draft CLI, exact account, escaped and deduplicated EXEs, no arguments, existing output preserved, missing EXE and wildcard account rejected. No policy applied.'
} finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
