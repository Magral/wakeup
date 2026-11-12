"""Build the cloud-only 1.7.2 release without settings or credentials."""
import ast
import hashlib
from pathlib import Path
import zipfile

root = Path(__file__).resolve().parents[1]
output = root / 'publish' / 'cloud'
output.mkdir(parents=True, exist_ok=True)
archive = output / 'WakeMini-Alice-Cloud-1.7.2.zip'
sources = [(root / 'alice-cloud' / name, name) for name in
           ('main.py', 'steam_cloud.py', 'requirements.txt', 'environment.example.json')]
sources += [(root / 'docs' / 'UPDATE-CLOUD-1.7.2.md', 'README.md'),
            (root / 'docs' / 'STEAM-VOICE-SEARCH.md', 'STEAM-VOICE-SEARCH.md')]
with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as package:
    for source, name in sources:
        data = source.read_bytes()
        if name.endswith('.py'):
            ast.parse(data.decode('utf-8'))
        entry = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
        entry.compress_type = zipfile.ZIP_DEFLATED
        entry.external_attr = 0o100644 << 16
        package.writestr(entry, data)
with zipfile.ZipFile(archive) as package:
    assert package.testzip() is None
(output / 'SHA256SUMS.txt').write_text(
    hashlib.sha256(archive.read_bytes()).hexdigest() + '  ' + archive.name + '\n',
    encoding='utf-8')
print(archive)
