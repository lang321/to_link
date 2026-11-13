#!/usr/bin/env python3
"""REQ-REMOTE-20261105-01: package a fresh enrollment config, never clone the live device ID."""
import base64
import json
import pathlib
import zipfile
from remote import ROOT, powershell

dist = ROOT / 'dist'
encoded = powershell(r"[Convert]::ToBase64String([IO.File]::ReadAllBytes('D:\ToLink\ToLink.exe'))")
(dist / 'ToLink.exe').write_bytes(base64.b64decode(encoded.strip()))
relay = json.loads((ROOT / 'private/relay.json').read_text())
config = {'Server': 'ws://192.168.100.101:19000', 'EnrollmentToken': relay['enrollmentToken'], 'AccessPassword': '', 'Fps': 6, 'MaxWidth': 1280, 'Quality': 55}
readme = '''ToLink Windows x64 测试版
需求：REQ-REMOTE-20261105-01

解压到可写目录，双击 ToLink.exe。首次启动会向测试服务器注册，显示本机 9 位设备 ID。
在客户端勾选“显示密码”查看初始密码；修改后点击“保存密码”。
浏览器访问 http://192.168.100.101:19000，输入本机 ID 和密码连接。
Mac 本机也可访问 http://10.211.55.3:19000。

每台新电脑应使用此原始包。不要复制已注册电脑的 agent.json，否则会复用同一设备身份。
运行中的客户端再次双击可打开窗口；关闭窗口留在托盘，托盘“退出”停止控制。
本包包含测试服务器注册凭据，仅供内部分发。

H.265 / H.264 使用 Intel Quick Sync 硬件编码；目标机已验证 Intel UHD 630。
不支持硬编的机器会尝试兼容模式，最终可回退到 JPEG。
请保留 ffmpeg.exe 与 ToLink.exe 在同一目录。FFmpeg 许可证及来源见 THIRD-PARTY.txt 和 FFmpeg-LICENSE.txt。
仅覆盖已登录桌面；锁屏、UAC 安全桌面、音频不在本测试版范围内。支持本机向 Windows 粘贴多个普通文件（32 个 / 100 MiB）。
'''
third_party = '''FFmpeg 9.0.2 essentials build by Gyan Doshi, GPLv3.
Build: https://www.gyan.dev/ffmpeg/builds/
Source: https://github.com/FFmpeg/FFmpeg/commit/946fcce07b
Official source download: https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz
Archive SHA256: 4705843ccaaf54257c16ad90f3e952ece33c17df964ecf7bfdbb0f49c7171077
FFmpeg is executed as a separate process. Preserve its notices when redistributing.
'''
ffmpeg = dist / 'ffmpeg/ffmpeg-9.0.2-essentials_build'
output = dist / 'ToLink-Windows-x64.zip'
with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED, compresslevel=5) as archive:
    archive.write(dist / 'ToLink.exe', 'ToLink.exe')
    archive.write(ffmpeg / 'bin/ffmpeg.exe', 'ffmpeg.exe')
    archive.write(ffmpeg / 'LICENSE', 'FFmpeg-LICENSE.txt')
    archive.writestr('agent.json', json.dumps(config, ensure_ascii=False, indent=2))
    archive.writestr('使用说明.txt', readme)
    archive.writestr('THIRD-PARTY.txt', third_party)
output.chmod(0o600)
print('Packaged ' + output.name + ': ' + str(output.stat().st_size) + ' bytes')
