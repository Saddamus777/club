[CmdletBinding()]
param([Parameter(Mandatory)][string]$PayloadRoot, [string]$ManifestPath)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $PayloadRoot).Path
if (-not $ManifestPath) { $ManifestPath = Join-Path $root 'agent-payload.json' }
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($manifest.version -ne 1 -or -not $manifest.files -or $manifest.files.Count -gt 10000) { throw 'Unsupported or empty payload manifest.' }
$expected = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    $relative = [string]$entry.path
    if ($relative -notmatch '^(Agent|Visitor)/[^\\:*?"<>|]+$' -or
        @($relative.Split('/') | Where-Object { $_ -eq '.' -or $_ -eq '..' -or -not $_ -or $_.EndsWith('.') -or $_.EndsWith(' ') }).Count -ne 0 -or
        -not $expected.Add($relative) -or $entry.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $entry.size -lt 0) { throw 'Invalid or duplicate payload entry.' }
}
foreach ($component in @('Agent','Visitor')) {
    if (-not $expected.Contains($component + '/Club.' + $component + '.exe')) { throw 'Executable is missing from manifest.' }
    $folder = Get-Item -LiteralPath (Join-Path $root $component)
    if ($folder.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Payload links are forbidden.' }
    # Inspect directories before recursing so a junction is never followed.
    $pending = New-Object 'System.Collections.Generic.Queue[string]'
    $pending.Enqueue($folder.FullName)
    while ($pending.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Dequeue() -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Payload links are forbidden.' }
            if ($item.PSIsContainer) { $pending.Enqueue($item.FullName); continue }
            $relative = $item.FullName.Substring($root.TrimEnd('\').Length + 1).Replace('\','/')
            if (-not $expected.Contains($relative)) { throw ('Unexpected payload file: ' + $relative) }
        }
    }
}
foreach ($entry in $manifest.files) {
    $file = Get-Item -LiteralPath (Join-Path $root $entry.path)
    if ($file.PSIsContainer -or $file.Length -ne $entry.size -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $entry.sha256) {
        throw ('Payload integrity check failed: ' + $entry.path)
    }
}
Write-Host 'Agent/Visitor payload files match the manifest. This does not verify publisher identity or a digital signature.'
