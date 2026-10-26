#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p work artifacts
root=artifacts/club-pilot-0.2-win-x64
# Build only into a new staging directory so a developer's data/config cannot enter the ZIP.
staging=$(mktemp -d "$PWD/work/publish.XXXXXX")
trap 'rm -rf "$staging"' EXIT
for project in Server Admin Visitor Agent; do
  bash scripts/dotnet.sh publish "src/Club.$project" -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -o "$staging/$project"
done
mkdir -p "$staging/Setup"
cp scripts/pilot/*.ps1 "$staging/Setup/"
cp docs/windows-pilot.md "$staging/READ-ME.md"
cp docs/validation.md "$staging/validation.md"
cp docs/windows-kiosk.md "$staging/windows-kiosk.md"
printf '0.2.0-pilot - Windows runtime validation required\n' > "$staging/VERSION.txt"
python3 scripts/write-agent-manifest.py "$staging"
python3 - "$staging" <<'PY'
from pathlib import Path
import hashlib, sys, zipfile
root = Path(sys.argv[1])
archive = Path('artifacts/club-pilot-0.2-win-x64.zip')
with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as z:
    for path in sorted(root.rglob('*')):
        if path.is_file():
            rel = path.relative_to(root)
            assert '.data' not in rel.parts and path.suffix not in ('.pfx', '.db')
            z.write(path, rel)
archive.with_suffix('.zip.sha256').write_text(f'{hashlib.sha256(archive.read_bytes()).hexdigest()}  {archive.name}\n')
print(f'{archive}: {archive.stat().st_size} bytes')
PY
# Keep the unpacked binaries for local inspection; do not overwrite an existing data directory.
if [[ -d "$root/.data" || -d "$root/Server/.data" ]]; then
  echo 'Refusing to replace an unpacked bundle containing data.' >&2
  exit 1
fi
rm -rf "$root"
mv "$staging" "$root"
