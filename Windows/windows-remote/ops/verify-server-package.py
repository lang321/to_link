#!/usr/bin/env python3
"""REQ-REMOTE-20261105-05: integration checks against an isolated systemd instance."""
import json
import shlex
import subprocess
from remote import ROOT, VM, vm, vm_file

PACKAGE = '/tmp/tolink-package-check/tolink-server'
def run(command, timeout=120):
    return subprocess.run(['prlctl', 'exec', VM, '/bin/bash', '-lc', shlex.quote(command)], text=True, capture_output=True, timeout=timeout)
def execute(command):
    result = run(command)
    if result.returncode:
        raise RuntimeError('Guest check failed: ' + result.stderr + result.stdout)
    return result.stdout.strip()
def install(args='', package=PACKAGE, log='upgrade'):
    return run('bash ' + package + '/install.sh --instance tolink-packtest ' + args + ' > /tmp/tolink-package-check/' + log + '.log 2>&1')
def health():
    execute('/opt/tolink-packtest/current/runtime/node ' + PACKAGE + '/tools/health.js /etc/tolink-packtest/relay.json')

# Copy only the changed metadata; all application/runtime checksums are validated by install.sh.
bundle = ROOT / 'dist/server-bundle/tolink-server'
for name in ['install.sh', 'README.txt', 'BUILD_ID', 'SHA256SUMS']:
    vm_file(bundle / name, PACKAGE + '/' + name)
report = {}
production = execute('sha256sum /etc/systemd/system/tolink-relay.service /etc/tolink/relay.json')
probe = r'''
const fs=require('fs'),crypto=require('crypto'),assert=require('assert');
(async()=>{const c=JSON.parse(fs.readFileSync('/etc/tolink-packtest/relay.json'));const password=crypto.randomBytes(16).toString('hex');
const base='http://127.0.0.1:'+c.port;let r=await fetch(base+'/api/register',{method:'POST',headers:{authorization:'Bearer '+c.enrollmentToken,'content-type':'application/json'},body:JSON.stringify({name:'installer-test',password})});assert.equal(r.status,201);const device=await r.json();
r=await fetch(base+'/api/login',{method:'POST',headers:{origin:c.origins[0],'content-type':'application/json'},body:JSON.stringify({deviceId:device.deviceId,password})});assert.equal(r.status,200);
const cookie=r.headers.get('set-cookie').split(';')[0];r=await fetch(base+'/api/status',{headers:{cookie}});assert.equal(r.status,200);assert.equal((await r.json()).devices[0].id,device.deviceId);
const raw=fs.readFileSync(c.stateFile);assert(!raw.toString().includes(password));const hash=b=>crypto.createHash('sha256').update(b).digest('hex');
fs.writeFileSync('/tmp/tolink-package-check/snapshot.json',JSON.stringify({config:hash(fs.readFileSync('/etc/tolink-packtest/relay.json')),state:hash(raw),secret:hash(c.enrollmentToken)}),{mode:0o600});console.log('registration-login-persistence passed');})().catch(e=>{console.error(e.message);process.exit(1)});
'''
probe_file = ROOT / 'dist/server-registration-probe.js'; probe_file.write_text(probe)
vm_file(probe_file, '/tmp/tolink-package-check/probe.js')
print(execute('/opt/tolink-packtest/current/runtime/node /tmp/tolink-package-check/probe.js'), flush=True)
report['registrationLogin'] = True

verify = r'''const fs=require('fs'),crypto=require('crypto'),assert=require('assert');const c=JSON.parse(fs.readFileSync('/etc/tolink-packtest/relay.json'));const saved=JSON.parse(fs.readFileSync('/tmp/tolink-package-check/snapshot.json'));const hash=b=>crypto.createHash('sha256').update(b).digest('hex');assert.equal(hash(fs.readFileSync('/etc/tolink-packtest/relay.json')),saved.config);assert.equal(hash(fs.readFileSync(c.stateFile)),saved.state);assert.equal(hash(c.enrollmentToken),saved.secret);'''
verify_file = ROOT / 'dist/server-preservation-probe.js'; verify_file.write_text(verify)
vm_file(verify_file, '/tmp/tolink-package-check/preserve.js')
def preserved(): execute('/opt/tolink-packtest/current/runtime/node /tmp/tolink-package-check/preserve.js')

r = install(); assert r.returncode == 0, 'Upgrade failed; inspect private upgrade.log'
health(); preserved(); report['upgradePreservesIdentity'] = True; print('upgrade preserved configuration, registration key and device state', flush=True)
r = install('--port 19000', log='port-conflict'); assert r.returncode != 0
health(); preserved(); report['portConflictProtected'] = True; print('occupied port rejected without changing active instance', flush=True)

# Fault injection into a disposable package copy; recompute its manifest deliberately.
execute('cp -a ' + PACKAGE + ' /tmp/tolink-package-check/broken-package')
execute("printf '\nif (require.main === module) throw new Error(\"intentional-startup-failure\");\n' >> /tmp/tolink-package-check/broken-package/app/server.js")
execute("cd /tmp/tolink-package-check/broken-package; printf 'ffffffffffffffff\n' > BUILD_ID; find . -type f ! -name SHA256SUMS -print0 | sort -z | xargs -0 sha256sum > SHA256SUMS")
r = install(package='/tmp/tolink-package-check/broken-package', log='rollback'); assert r.returncode != 0
health(); preserved(); report['failedUpgradeRolledBack'] = True; print('failed upgrade restored working service and original state', flush=True)
assert execute('sha256sum /etc/systemd/system/tolink-relay.service /etc/tolink/relay.json') == production
assert execute('systemctl is-active tolink-relay') == 'active'
report['existingServiceUnchanged'] = True
report['buildId'] = (bundle / 'BUILD_ID').read_text().strip()
report['testedPlatform'] = 'Ubuntu 24.04 ARM64 / systemd'
(ROOT / 'dist/server-package-verification.json').write_text(json.dumps(report, indent=2))
print(json.dumps(report, indent=2))
