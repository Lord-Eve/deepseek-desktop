"""Package already-published Windows binaries on a non-Windows build host."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import zipfile

p = argparse.ArgumentParser()
p.add_argument('--publish-dir', required=True)
p.add_argument('--nuget-dir', required=True)
args = p.parse_args()
root = Path(__file__).resolve().parents[1]
publish = Path(args.publish_dir).resolve()
lock = json.loads((root / 'vendor/engine-lock.json').read_text())
digest = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
assert digest(root / 'engine/ds-free-api.exe') == lock['binarySha256']
assert digest(root / 'vendor' / lock['sourceArchive']) == lock['sourceSha256']
assert (publish / 'DeepSeekDesktop.exe').read_bytes()[:2] == b'MZ'
shutil.copytree(root / 'engine', publish / 'engine', dirs_exist_ok=True)
for name in ['README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md']:
    shutil.copy2(root / name, publish / name)
shutil.copy2(root / 'vendor/engine-lock.json', publish / 'engine-lock.json')
for path in publish.glob('*.pdb'):
    path.unlink()
source = publish / 'source'
for directory in ['src', 'tests', 'tools', '.github', 'vendor', 'docs']:
    for path in (root / directory).rglob('*'):
        if not path.is_file() or any(s in {'bin', 'obj', '__pycache__'} for s in path.parts) or path.suffix in {'.zip', '.pyc', '.pdb'}:
            continue
        target = source / path.relative_to(root)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)
for name in ['README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md', 'global.json', '.gitignore', '.gitattributes', '.editorconfig', 'AGENTS.md']:
    shutil.copy2(root / name, source / name)
notices = publish / 'runtime-notices'
notices.mkdir(exist_ok=True)
for runtime in ['microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64']:
    version = sorted((Path(args.nuget_dir) / runtime).iterdir(), key=lambda p: tuple(int(s) for s in p.name.split('.')))[-1]
    for name in ['LICENSE.TXT', 'LICENSE', 'THIRD-PARTY-NOTICES.TXT']:
        if (version / name).is_file():
            shutil.copy2(version / name, notices / (runtime + '-' + name))
archive = root / 'dist/DeepSeekDesktop-0.1.0-win-x64.zip'
with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for file in sorted(publish.rglob('*')):
        if file.is_file():
            z.write(file, Path(archive.stem) / file.relative_to(publish))
archive.with_suffix('.zip.sha256').write_text(digest(archive) + '  ' + archive.name + '\n')
print(f'Package: {archive} ({archive.stat().st_size:,} bytes)')
