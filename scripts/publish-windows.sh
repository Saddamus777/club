#!/usr/bin/env bash
set -euo pipefail
cd /workspace/club
bash scripts/dotnet.sh publish src/Club.Server -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:RestoreLockedMode=true -o artifacts/club-win-x64
cp docs/windows-test.md artifacts/club-win-x64/READ-ME.md
python3 - <<'PY'
from pathlib import Path
import hashlib, zipfile
root = Path('artifacts/club-win-x64')
# cmd.exe requires Windows line endings. ASCII avoids code-page dependent command corruption.
for source in Path('scripts/windows').glob('*.cmd'):
    content = source.read_text(encoding='ascii').replace('\r\n', '\n')
    (root / source.name).write_bytes(content.replace('\n', '\r\n').encode('ascii'))
for command in root.glob('*.cmd'):
    data = command.read_bytes()
    assert data.isascii() and data.endswith(b'\r\n') and b'\n' not in data.replace(b'\r\n', b''), command
(root / 'VERSION.txt').write_text('0.1.1 - browser owner setup and Windows launcher fix\n', encoding='ascii')
output = Path('artifacts/club-prototype-0.1-win-x64.zip')
with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED) as archive:
    for file in sorted(root.rglob('*')):
        if file.is_file() and '.data' not in file.relative_to(root).parts:
            archive.write(file, file.relative_to(root))
checksum = hashlib.sha256(output.read_bytes()).hexdigest()
output.with_suffix('.zip.sha256').write_text(f'{checksum}  {output.name}\n')
print(f'{output}: {output.stat().st_size} bytes, SHA256 {checksum}')
PY
