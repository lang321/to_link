#!/usr/bin/env python3
"""REQ-REMOTE-20261105-05: reproducible offline bundle, with checksum-verified official Node binaries."""
from pathlib import Path
import gzip
import hashlib
import json
import shutil
import tarfile

ROOT = Path(__file__).resolve().parents[1]
cache = ROOT / 'dist/server-runtime'
records = json.loads((cache / 'runtime-lock.json').read_text())
stage = ROOT / 'dist/server-bundle'
stage.mkdir(exist_ok=True)
bundle = stage / 'tolink-server'
bundle.mkdir(exist_ok=True)
for name in ['install.sh', 'manage.sh']:
    shutil.copy2(ROOT / 'deploy' / name, bundle / name)
    (bundle / name).chmod(0o755)
shutil.copytree(ROOT / 'deploy/tools', bundle / 'tools', dirs_exist_ok=True)
app = bundle / 'app'; app.mkdir(exist_ok=True)
for name in ['server.js', 'auth.js', 'package.json', 'package-lock.json']:
    shutil.copy2(ROOT / name, app / name)
for name in ['public', 'node_modules']:
    shutil.copytree(ROOT / name, app / name, dirs_exist_ok=True)
runtime = bundle / 'runtime'; runtime.mkdir(exist_ok=True)
for item in records:
    archive = cache / item['file']
    assert hashlib.sha256(archive.read_bytes()).hexdigest() == item['sha256']
    arch = 'arm64' if '-arm64.' in item['file'] else 'x64'
    dest = runtime / ('linux-' + arch); dest.mkdir(exist_ok=True)
    prefix = item['file'].removesuffix('.tar.xz')
    with tarfile.open(archive, 'r:xz') as tar:
        for source, target in [('bin/node', dest / 'node'), ('LICENSE', runtime / 'NODE-LICENSE.txt')]:
            with tar.extractfile(prefix + '/' + source) as inp, target.open('wb') as out:
                shutil.copyfileobj(inp, out)
    (dest / 'node').chmod(0o755)
(runtime / 'SOURCE.json').write_text(json.dumps(records, indent=2))
shutil.copy2(ROOT / 'deploy/README.txt', bundle / 'README.txt')
# BUILD_ID depends on every shipped file except the generated identifier/checksum manifest.
files = sorted(p for p in bundle.rglob('*') if p.is_file() and p.name not in ['BUILD_ID', 'SHA256SUMS'])
digests = [(hashlib.sha256(p.read_bytes()).hexdigest(), str(p.relative_to(bundle))) for p in files]
build_id = hashlib.sha256('\n'.join(h + '  ' + n for h, n in digests).encode()).hexdigest()[:16]
(bundle / 'BUILD_ID').write_text(build_id + '\n')
digests.append((hashlib.sha256((bundle / 'BUILD_ID').read_bytes()).hexdigest(), 'BUILD_ID'))
(bundle / 'SHA256SUMS').write_text(''.join(h + '  ' + n + '\n' for h, n in sorted(digests, key=lambda v: v[1])))
for p in bundle.rglob('*'):
    p.chmod(0o755 if p.is_dir() or p.name in ['node', 'install.sh', 'manage.sh'] else 0o644)
destination = ROOT.parents[1] / 'Server'
destination.mkdir(exist_ok=True)
output = destination / 'ToLink-Server-linux.tar.gz'
with output.open('wb') as raw, gzip.GzipFile(filename='', mode='wb', fileobj=raw, compresslevel=6, mtime=0) as gz:
    with tarfile.open(fileobj=gz, mode='w') as tar:
        def normalized(info):
            info.uid = info.gid = 0; info.uname = info.gname = 'root'; info.mtime = 0
            return info
        tar.add(bundle, arcname='tolink-server', filter=normalized)
digest = hashlib.sha256(output.read_bytes()).hexdigest()
output.with_suffix(output.suffix + '.sha256').write_text(digest + '  ' + output.name + '\n')
print(json.dumps({'archive': str(output), 'bytes': output.stat().st_size, 'buildId': build_id, 'node': records[0]['version']}, ensure_ascii=False))
