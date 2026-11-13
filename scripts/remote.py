#!/usr/bin/env python3
"""REQ-REMOTE-20261105-01: deployment transports; never print access credentials."""
import base64
import json
import pathlib
import shlex
import subprocess
import urllib.parse
import urllib.request

from layout import ROOT, SERVER, WINDOWS
VM = 'Ubuntu 24.04 ARM64'
BASE = 'http://192.168.100.105:8765'
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))

def windows(script, timeout=30000):
    body = json.dumps({'script': script, 'timeout': timeout}).encode()
    req = urllib.request.Request(BASE + '/api/exec', body, {'Content-Type': 'application/json'})
    with OPENER.open(req, timeout=timeout / 1000 + 10) as response:
        result = json.load(response)
    if not result.get('ok'):
        raise RuntimeError(json.dumps(result, ensure_ascii=False))
    return result['stdout']

def powershell(script, timeout=30000):
    encoded = base64.b64encode(("$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'; " + script).encode('utf-16le')).decode()
    return windows('powershell.exe -NoProfile -NonInteractive -EncodedCommand ' + encoded, timeout)

def upload(local, remote):
    url = BASE + '/api/upload?' + urllib.parse.urlencode({'path': remote})
    req = urllib.request.Request(url, pathlib.Path(local).read_bytes(), {'Content-Type': 'application/octet-stream'})
    with OPENER.open(req, timeout=180) as response:
        result = json.load(response)
    if not result.get('ok'): raise RuntimeError(str(result))

def vm(command):
    # prlctl joins the guest command arguments; retain quoting for the second shell parse.
    return subprocess.check_output(['prlctl', 'exec', VM, '/bin/bash', '-lc', shlex.quote(command)], text=True, timeout=20)

def vm_file(local, remote):
    import shlex
    encoded = base64.b64encode(pathlib.Path(local).read_bytes()).decode()
    vm('mkdir -p ' + shlex.quote(str(pathlib.PurePosixPath(remote).parent)))
    temp = shlex.quote(remote + '.b64')
    vm(': > ' + temp)
    for offset in range(0, len(encoded), 2000):
        vm("printf '%s' '" + encoded[offset:offset+2000] + "' >> " + temp)
    mode = oct(pathlib.Path(local).stat().st_mode & 0o777)[2:]
    vm('base64 -d ' + temp + ' > ' + shlex.quote(remote) + ' && chmod ' + mode + ' ' + shlex.quote(remote) + ' && rm ' + temp)

if __name__ == '__main__':
    import sys
    if sys.argv[1] == 'ps': print(powershell(sys.stdin.read()))
    elif sys.argv[1] == 'vm': print(vm(sys.stdin.read()))
