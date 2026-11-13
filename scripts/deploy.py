#!/usr/bin/env python3
"""REQ-REMOTE-20261105-01: deploy the authorized local VM / Windows test environment."""
import base64
import json
import os
import secrets
import tarfile
from remote import ROOT, SERVER, upload, vm, vm_file, powershell

private = ROOT / 'private'
private.mkdir(exist_ok=True, mode=0o700)
os.chmod(private, 0o700)
config_path = private / 'relay.json'
previous_config = json.loads(config_path.read_text()) if config_path.exists() else {}
config = {'port': 19000, 'host': '0.0.0.0', 'enrollmentToken': previous_config.get('enrollmentToken') or secrets.token_hex(32),
          'stateFile': '/var/lib/tolink/devices.json', 'maxDevices': 1000,
          'origins': ['http://10.211.55.3:19000', 'http://192.168.100.101:19000', 'http://localhost:19000']}
config_path.write_text(json.dumps(config, ensure_ascii=False, indent=2)); os.chmod(config_path, 0o600)
agent_path = private / 'agent.json'
agent = json.loads(agent_path.read_text()) if agent_path.exists() else {}
# A registered installation is authoritative: preserve locally changed passwords and stable IDs.
remote_config = powershell(r"if(Test-Path D:\ToLink\agent.json){[Convert]::ToBase64String([IO.File]::ReadAllBytes('D:\ToLink\agent.json'))}")
if remote_config.strip():
    live_agent = json.loads(base64.b64decode(remote_config.strip()))
    if str(live_agent.get('DeviceId', '')).isdigit(): agent = live_agent
if not str(agent.get('DeviceId', '')).isdigit():
    agent = {'Server': 'ws://192.168.100.101:19000', 'EnrollmentToken': config['enrollmentToken'], 'AccessPassword': previous_config.get('password') or secrets.token_urlsafe(12)}
agent.update({'Fps': 6, 'MaxWidth': 1280, 'Quality': 55})
agent_path.write_text(json.dumps(agent, indent=2)); os.chmod(agent_path, 0o600)

dist = ROOT / 'dist'; dist.mkdir(exist_ok=True)
archive = dist / 'relay.tar.gz'
with tarfile.open(archive, 'w:gz') as tar:
    for name in ['server.js', 'auth.js', 'package.json', 'package-lock.json', 'public', 'node_modules']:
        tar.add(SERVER / name, arcname=name)
print('Uploading relay package to VM')
vm_file(archive, '/tmp/tolink-relay.tar.gz')
print(vm("set -e; id tolink >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin tolink; install -d -m 755 /opt/tolink; install -d -m 750 -o root -g tolink /etc/tolink; install -d -m 700 -o tolink -g tolink /var/lib/tolink; tar xzf /tmp/tolink-relay.tar.gz -C /opt/tolink; chown -R root:root /opt/tolink"))
vm_file(config_path, '/etc/tolink/relay.json')
service = '''[Unit]
Description=ToLink remote desktop relay (REQ-REMOTE-20261105-01)
After=network.target
[Service]
Type=simple
User=tolink
Group=tolink
WorkingDirectory=/opt/tolink
Environment=TOLINK_CONFIG=/etc/tolink/relay.json
ExecStart=/usr/bin/nodejs /opt/tolink/server.js
Restart=on-failure
RestartSec=3
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
ReadWritePaths=/var/lib/tolink
LimitNOFILE=8192
MemoryMax=1G
[Install]
WantedBy=multi-user.target
'''
service_path = dist / 'tolink-relay.service'; service_path.write_text(service)
vm_file(service_path, '/etc/systemd/system/tolink-relay.service')
print(vm('chmod 640 /etc/tolink/relay.json; chown root:tolink /etc/tolink/relay.json; systemctl daemon-reload; systemctl enable --now tolink-relay; systemctl restart tolink-relay; systemctl is-active tolink-relay'))
upload(agent_path, 'D:\\ToLink\\agent.json')
print(powershell(r'''$sid=[System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value; & icacls.exe 'D:\ToLink' /inheritance:r /grant:r ('*'+$sid+':(OI)(CI)F') '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null; $shell=New-Object -ComObject WScript.Shell; $shortcut=$shell.CreateShortcut([Environment]::GetFolderPath('Desktop')+'\ToLink.lnk'); $shortcut.TargetPath='D:\ToLink\ToLink.exe'; $shortcut.WorkingDirectory='D:\ToLink'; $shortcut.Save(); Start-Process 'D:\ToLink\ToLink.exe' -ArgumentList '--start'; Start-Sleep -Seconds 2; Get-Process ToLink | Select-Object Id,SessionId,WorkingSet64 | ConvertTo-Json -Compress'''))
live = powershell(r"[Convert]::ToBase64String([IO.File]::ReadAllBytes('D:\ToLink\agent.json'))")
agent = json.loads(base64.b64decode(live.strip()))
agent_path.write_text(json.dumps(agent, indent=2))
access = private / 'access.txt'
access.write_text('REQ-REMOTE-20261105-01\n浏览器地址：http://10.211.55.3:19000\n设备 ID：' + str(agent.get('DeviceId', '注册中')) + '\n设备密码：' + agent['AccessPassword'] + '\n局域网入口：http://192.168.100.101:19000\n')
os.chmod(access, 0o600)
exe = powershell(r"[Convert]::ToBase64String([IO.File]::ReadAllBytes('D:\ToLink\ToLink.exe'))")
(dist / 'ToLink.exe').write_bytes(base64.b64decode(exe.strip()))
print('Deployment complete. Credentials: private/access.txt (not printed).')
