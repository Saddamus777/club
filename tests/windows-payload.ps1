$ErrorActionPreference = 'Stop'
$verify = (Resolve-Path 'scripts/pilot/Verify-AgentPayload.ps1').Path
$work = Join-Path $env:TEMP ('club-payload-' + [Guid]::NewGuid().ToString('N'))
function Must-Reject {
    $rejected = $false
    try { & $verify -PayloadRoot $work } catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid payload accepted.' }
}
try {
    New-Item -ItemType Directory -Path (Join-Path $work 'Agent'), (Join-Path $work 'Visitor') | Out-Null
    'agent fixture' | Set-Content (Join-Path $work 'Agent\Club.Agent.exe')
    'visitor fixture' | Set-Content (Join-Path $work 'Visitor\Club.Visitor.exe')
    python scripts/write-agent-manifest.py $work
    if ($LASTEXITCODE -ne 0) { throw 'Manifest fixture failed.' }
    & $verify -PayloadRoot $work
    $exe = Join-Path $work 'Agent\Club.Agent.exe'
    $original = [IO.File]::ReadAllBytes($exe)
    $changed = [byte[]]$original.Clone(); $changed[0] = $changed[0] -bxor 1
    [IO.File]::WriteAllBytes($exe, $changed); Must-Reject
    [IO.File]::WriteAllBytes($exe, $original)
    'unexpected' | Set-Content (Join-Path $work 'Visitor\extra.dll'); Must-Reject
    Remove-Item (Join-Path $work 'Visitor\extra.dll')
    Remove-Item $exe; Must-Reject
    [IO.File]::WriteAllBytes($exe, $original)
    $manifest = Join-Path $work 'agent-payload.json'
    $saved = [IO.File]::ReadAllBytes($manifest)
    $bad = Get-Content $manifest -Raw | ConvertFrom-Json
    $bad.files[0].path = 'Agent/../outside.exe'
    $bad | ConvertTo-Json -Depth 5 | Set-Content $manifest -Encoding UTF8
    Must-Reject
    [IO.File]::WriteAllBytes($manifest, $saved)
    & $verify -PayloadRoot $work
    & $verify -PayloadRoot (Resolve-Path 'artifacts/pilot').Path
    Write-Host 'PASS: published payload verified; same-size corruption, missing/extra files and traversal rejected.'
} finally {
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
}
$global:LASTEXITCODE = 0
