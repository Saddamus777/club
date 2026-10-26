"""Generate an integrity manifest for the two installed gaming-PC payloads."""
import hashlib
import json
import pathlib
import sys

root = pathlib.Path(sys.argv[1]).resolve()
entries = []
for component in ('Agent', 'Visitor'):
    folder = root / component
    if not (folder / f'Club.{component}.exe').is_file():
        raise SystemExit(f'Missing {component} executable')
    for file in sorted(folder.rglob('*')):
        if file.is_symlink():
            raise SystemExit('Symlinks are not allowed in a payload')
        if not file.is_file():
            continue
        if file.suffix.lower() in ('.db', '.pfx', '.key') or file.name.lower() in ('agent.json', 'games.json', 'session.json', 'installation.json', 'appsettings.production.json'):
            raise SystemExit('Runtime data must not be shipped in the payload')
        entries.append(dict(path=file.relative_to(root).as_posix(), size=file.stat().st_size, sha256=hashlib.sha256(file.read_bytes()).hexdigest()))
(root / 'agent-payload.json').write_text(json.dumps(dict(version=1, files=entries), indent=2) + '\n', encoding='utf-8')
