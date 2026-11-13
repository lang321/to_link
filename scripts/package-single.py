#!/usr/bin/env python3
"""REQ-REMOTE-20261105-01: build and verify the single-file Windows distribution."""
import base64
import gzip
import hashlib
import json
import zipfile
from remote import ROOT, WINDOWS, upload, windows, powershell

build = ROOT / 'dist/single-build'
build.mkdir(parents=True, exist_ok=True, mode=0o700)
with zipfile.ZipFile(ROOT / 'dist/ToLink-Windows-x64.zip') as package:
    for name in ['agent.json', 'FFmpeg-LICENSE.txt', 'THIRD-PARTY.txt']:
        (build / name).write_bytes(package.read(name))
    with package.open('ffmpeg.exe') as source, (build / 'ffmpeg.gz').open('wb') as target:
        sha = hashlib.sha256()
        with gzip.GzipFile(fileobj=target, mode='wb', compresslevel=9, mtime=0) as compressed:
            while True:
                block = source.read(1024 * 1024)
                if not block:
                    break
                sha.update(block)
                compressed.write(block)
(build / 'agent.json').chmod(0o600)
encoder_hash = sha.hexdigest()
(build / 'SingleFileManifest.cs').write_text('namespace ToLink { internal static class SingleFileManifest { internal const string EncoderHash = "' + encoder_hash + '"; } }')
remote = 'D:\\ToLink\\single-build\\'
for name in ['ToLink.cs', 'VideoEncoder.cs', 'ServerSettings.cs', 'IncomingFiles.cs', 'StreamingClipboard.cs', 'SingleFileRuntime.cs', 'DesktopBridge.cs']:
    upload(WINDOWS / name, remote + name)
for name in ['SingleFileManifest.cs', 'ffmpeg.gz', 'agent.json', 'FFmpeg-LICENSE.txt', 'THIRD-PARTY.txt']:
    if name == 'ffmpeg.gz':
        existing = powershell(r"if(Test-Path 'D:\ToLink\single-build\ffmpeg.gz'){(Get-FileHash 'D:\ToLink\single-build\ffmpeg.gz').Hash}").strip().lower()
        if existing == hashlib.sha256((build / name).read_bytes()).hexdigest():
            print('Reusing verified encoder payload', flush=True)
            continue
    print('Uploading ' + name, flush=True)
    upload(build / name, remote + ('bootstrap.json' if name == 'agent.json' else name))
print(windows(r'''cd /d D:\ToLink\single-build
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /platform:x64 /optimize+ /define:SINGLE_FILE /out:ToLink.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /resource:ffmpeg.gz,ToLink.ffmpeg.gz /resource:bootstrap.json,ToLink.agent.json /resource:FFmpeg-LICENSE.txt,ToLink.FFmpeg-LICENSE.txt /resource:THIRD-PARTY.txt,ToLink.THIRD-PARTY.txt ToLink.cs VideoEncoder.cs ServerSettings.cs IncomingFiles.cs StreamingClipboard.cs DesktopBridge.cs SingleFileRuntime.cs SingleFileManifest.cs''', 60000))

# Exercise the exact extraction method on Windows without registering a new device or stopping the live agent.
proof = powershell(r'''$a=[Reflection.Assembly]::LoadFrom('D:\ToLink\single-build\ToLink.exe'); $t=$a.GetType('ToLink.SingleFileRuntime'); $m=$t.GetMethod('Prepare'); $target='D:\ToLink\single-build\verification'; $m.Invoke($null,@($target))|Out-Null; $config=Join-Path $target 'agent.json'; $saved=[IO.File]::ReadAllText($config); try { [IO.File]::WriteAllText($config,'{"DeviceId":"preserve-test","AccessPassword":"test-only"}'); $mtime=(Get-Item (Join-Path $target 'ffmpeg.exe')).LastWriteTimeUtc; $m.Invoke($null,@($target))|Out-Null; if([IO.File]::ReadAllText($config) -notmatch 'preserve-test'){throw 'Configuration overwritten'}; if((Get-Item (Join-Path $target 'ffmpeg.exe')).LastWriteTimeUtc -ne $mtime){throw 'Unchanged encoder was rewritten'}; @{encoderHash=(Get-FileHash (Join-Path $target 'ffmpeg.exe')).Hash; exeHash=(Get-FileHash 'D:\ToLink\single-build\ToLink.exe').Hash; configPreserved=$true; cacheReused=$true; cachePath=([Environment]::GetFolderPath('LocalApplicationData')+'\ToLink')}|ConvertTo-Json -Compress } finally {[IO.File]::WriteAllText($config,$saved)}''', 60000)
result = json.loads(proof)
assert result['encoderHash'].lower() == encoder_hash
print('Windows extraction, checksum, config preservation and cache reuse passed', flush=True)

# The embedded encoder is already verified locally. Transfer only the PE prefix/suffix,
# then verify the complete reconstructed executable; avoid retransmitting ~37 MiB.
parts = json.loads(powershell(r'''Add-Type -TypeDefinition @"
using System;
public static class EmbeddedPayload {
    public static int Find(byte[] file, byte[] payload) {
        for (int i=0; i<=file.Length-payload.Length; i++) {
            if(file[i]!=payload[0]) continue;
            int j=1; while(j<payload.Length && file[i+j]==payload[j]) j++;
            if(j==payload.Length) return i;
        }
        throw new Exception("Verified encoder payload not found in executable");
    }
}
"@;
$file=[IO.File]::ReadAllBytes('D:\ToLink\single-build\ToLink.exe');
$payload=[IO.File]::ReadAllBytes('D:\ToLink\single-build\ffmpeg.gz');
$offset=[EmbeddedPayload]::Find($file,$payload); $tail=$offset+$payload.Length;
if($offset+$file.Length-$tail -gt 1048576){throw 'Unexpected executable layout'};
@{prefix=[Convert]::ToBase64String($file,0,$offset);suffix=[Convert]::ToBase64String($file,$tail,$file.Length-$tail);payloadHash=(Get-FileHash 'D:\ToLink\single-build\ffmpeg.gz').Hash;exeHash=(Get-FileHash 'D:\ToLink\single-build\ToLink.exe').Hash}|ConvertTo-Json -Compress''', 60000))
assert parts['payloadHash'].lower() == hashlib.sha256((build / 'ffmpeg.gz').read_bytes()).hexdigest()
data = base64.b64decode(parts['prefix'], validate=True) + (build / 'ffmpeg.gz').read_bytes() + base64.b64decode(parts['suffix'], validate=True)
assert hashlib.sha256(data).hexdigest() == result['exeHash'].lower() == parts['exeHash'].lower()
output = ROOT / 'dist/ToLink-Single.exe'
temp = output.with_suffix('.verified')
temp.write_bytes(data); temp.chmod(0o600); temp.replace(output)
(build / 'verification.json').write_text(json.dumps(result, indent=2))
print('Single-file EXE verified: ' + str(output.stat().st_size) + ' bytes')
