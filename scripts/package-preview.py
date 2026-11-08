"""Package the tested Windows helper and cloud handler; no user settings/secrets."""
import hashlib
from pathlib import Path
import zipfile

root = Path(__file__).resolve().parents[1]
output = root / 'publish' / 'bundle'
output.mkdir(parents=True, exist_ok=True)
cloud = output / 'WakeMini-Alice-Cloud-1.5.1.zip'
with zipfile.ZipFile(cloud, 'w', zipfile.ZIP_DEFLATED) as archive:
    for name in ['main.py', 'README.md', 'environment.example.json']:
        archive.write(root / 'alice-cloud' / name, name)
bundle = output / 'WakeMini-1.5.1-preview.zip'
with zipfile.ZipFile(bundle, 'w', zipfile.ZIP_DEFLATED) as archive:
    for source, target in [
        (root / 'publish' / 'package' / 'WakeMiniAgent.exe', 'WakeMiniAgent.exe'),
        (root / 'pc-agent' / 'THIRD-PARTY-NOTICES.txt', 'THIRD-PARTY-NOTICES.txt'),
        (root / 'docs' / 'UPDATE-1.5.1.md', 'README.md'),
        (cloud, cloud.name),
    ]:
        archive.write(source, target)
    for source in sorted((root / 'chrome-voice-extension').glob('*')):
        if source.is_file(): archive.write(source, 'chrome-voice-extension/' + source.name)
with zipfile.ZipFile(bundle) as archive:
    assert archive.testzip() is None
(output / 'SHA256SUMS.txt').write_text(
    hashlib.sha256(bundle.read_bytes()).hexdigest() + '  ' + bundle.name + '\n')
print(bundle)
