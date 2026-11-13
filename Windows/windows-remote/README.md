# ToLink Windows 远程桌面 · 测试版

需求编号：**REQ-REMOTE-20261105-01**。日期：2026-11-05（用户时区）。

## 需求 · REQ-REMOTE-20261105-01

- Windows 主动推流到服务器，浏览器通过服务器进行远程控制。
- 每台客户端首次注册获得固定的 9 位 ID，可在客户端设置独立密码。
- 浏览器输入设备 ID + 密码连接；各设备认证、画面、控制指令相互隔离。
- 支持鼠标、滚轮、键盘、中文文本输入、全屏、托盘、停止连接及自动重连。
- 规模目标为 100 台以上；本次完成单机真实验证与 128 路模拟转发测试，未进行 100 台真实 Windows 联测。

## 详细设计 · REQ-REMOTE-20261105-01

```text
Windows 192.168.100.105 / ToLink.exe
    → ws://192.168.100.101:19000（Mac NAT 入口）
    → Ubuntu 10.211.55.3:19000 / Node.js 中转
    ↔ 浏览器 http://10.211.55.3:19000
```

Windows 使用 C# / WinForms / .NET Framework，GDI 采集并比较像素，FFmpeg / Intel Quick Sync 硬编码 H.265 或 H.264，`SendInput` 执行键鼠。Node.js 只转发，不解码、不编码。浏览器通过 MSE 播放视频，透明 Canvas 捕获操作；JPEG 为备用模式。

| 接口 | 认证与行为 |
|---|---|
| `POST /api/register` | 安装包注册凭据；分配随机 9 位 ID、独立设备令牌；密码使用 scrypt + 随机盐存储 |
| `POST /api/device/password` | 设备令牌；更新本机密码，撤销该设备已有浏览器会话 |
| `POST /api/login` | `deviceId + password`；返回 HttpOnly / SameSite Cookie，会话只绑定该 ID |
| `GET /api/status` | 只返回当前会话授权的设备 |
| `/ws/agent?device=ID` | Bearer 设备令牌；上传视频分片或 JPEG、桌面尺寸与应用层心跳 |
| `/ws/viewer?device=ID` | 检查 Cookie、设备绑定及 Origin；每设备仅允许一个控制连接 |

状态写入 `/var/lib/tolink/devices.json`，先写临时文件再原子替换。重启服务保留设备 ID、令牌与密码哈希；浏览器会话需重新登录。当前状态文件由单个服务进程独占，不支持多个实例共享写入。

JPEG 最多保留一帧待确认画面，后续重复/过期帧丢弃。视频积压触发重同步时，当前控制页清理旧解码队列，再通过同一连接请求新的初始化分片。正在发送文件时，编码器重启延后到该批文件结束。浏览器传输存活使用 WebSocket ping/pong 判断，登录会话仍按配置过期。像素未变化时停止向编码器喂帧，额外两个重复输入排空编码器与封装缓冲，确保最后一次变化能显示。静止后仅保留连接心跳；鼠标、文本光标、动画、时钟变化均算画面变化。无观看者时停止采集和编码。断开、失焦、停止控制或修改密码会释放按键。

## 代码开发 · REQ-REMOTE-20261105-01

| 文件 | 用途 |
|---|---|
| `native/ToLink.cs`、`native/VideoEncoder.cs`、`native/build.cmd` | Windows 客户端、诊断窗口与远程编译 |
| `server.js`、`auth.js` | 多设备中转、注册、密码和会话管理 |
| `public/` | ID + 密码登录、视频播放与含黑边的坐标映射 |
| `ops/remote.py`、`ops/deploy.py` | 通过现有 server.js 和 Parallels Guest Tools 部署 |
| `test/relay.test.js`、`test/geometry.test.js` | 鉴权、隔离、视频背压、撤销、注册、持久化与坐标映射回归 |
| `ops/load-test.js` | 模拟并发帧转发与控制探针，不模拟采集和解码 |

### 使用

1. 本机浏览器打开 `http://10.211.55.3:19000`；局域网设备可用 `http://192.168.100.101:19000`。
2. 输入 Windows 客户端显示的 ID 和密码。本测试设备 ID：`352633028`。初始密码见本地 `private/access.txt`。
3. 点击“启用控制”，再点击画面中的目标位置。中文可通过页面下方文本框发送，不修改剪贴板。
4. Windows 桌面快捷方式 **ToLink** 打开客户端；可复制 ID、显示/保存密码、停止连接。关闭窗口后留在托盘，托盘“退出”彻底停止。

### 部署位置与包

- Windows：`D:\ToLink\ToLink.exe`，配置 `D:\ToLink\agent.json`，日志 `D:\ToLink\ToLink.log`。
- 虚拟机：`/opt/tolink`；配置 `/etc/tolink/relay.json`；服务 `tolink-relay.service`；设备状态 `/var/lib/tolink/devices.json`。
- 本地包：`dist/ToLink-Windows-x64.zip`。安装包包含注册凭据，仅内部使用；每台新电脑解压后启动自动注册。**不要把已注册电脑的 agent.json 复制给另一台电脑**，否则会重复使用同一设备身份。
- 注册完成后，客户端会删除本地配置中的公共注册凭据，保存独立 ID 和令牌。清空或丢失本机配置将失去原设备身份。
- 本次只启用了虚拟机中转服务自启动；Windows 客户端没有配置开机自启动，重新登录 Windows 后手动启动即可。

```sh
# 本地回归
npm ci --ignore-scripts
npm test
# 虚拟机诊断
systemctl status tolink-relay
journalctl -u tolink-relay -n 50 --no-pager
# 停止中转
sudo systemctl stop tolink-relay
```

Parallels 转发规则名：`tolink-remote-19000`。回退网络时仅移除此规则：`prlsrvctl net set Shared --nat-tcp-del tolink-remote-19000`。现有 CICD 接收服务、VPN、代理与其他虚拟机配置未作修改。

## 代码审核 · REQ-REMOTE-20261105-01

- 检查了设备令牌、独立密码、跨设备会话拒绝、重复发布者/控制者、输入字段限制、消息大小、帧背压和断线按键释放。
- 密码修改仅允许设备令牌，浏览器密码不能改写设备凭据。服务端不保存明文访问密码；本机配置含本机密码，部署目录已限制访问。
- 修复了首次登录空会话清理错误、旧重连回调覆盖新会话、旧框架协议心跳触发的 RSV 协议错误路径。
- 保留 `[ToLink]` 关键日志及每 10 秒流量耗时日志，用于后续定位；不记录密码、令牌或输入文本。修复了全屏视频和鼠标层尺寸不一致，以及 MSE 缓冲清理过近可能删除当前关键帧组的问题。
- Node 依赖锁定在 `package-lock.json`，依赖审计无已知漏洞。未新增前端框架或全局依赖。

## 测试 · REQ-REMOTE-20261105-01

20 项自动化回归通过。真实 Windows 与浏览器验证记录、容量结果见 `VALIDATION.md`。

视频默认使用原生 1600×900、20 FPS 采样上限、QSV 恒定质量 20；画面无变化时不持续编码。当前机型已实测 H.265 Main Level 4 与 H.264 Baseline Level 4。JPEG 备用为最大宽度 1280、质量 55、6 FPS，并跳过重复帧。视频帧率为按需变化，不代表静止画面仍每秒发送 20 帧。全屏与普通窗口都按实际画面显示区域计算鼠标坐标，排除黑边。

边界：测试网使用 HTTP/WS；外网部署需 HTTPS/WSS 和有效证书。首版覆盖已登录桌面，不支持锁屏、UAC 安全桌面、音频或高帧率视频。硬编路径已实测 Intel UHD 630；其他显卡未验收，失败可回退 JPEG。多屏使用拼接画面，尚未做真实多屏验收。浏览器会话绑定单个设备，更换 ID 会撤销该浏览器的旧设备会话。

100 台以上的正式部署需根据实际码率设置节点容量并扩展；不要将“128 路模拟通过”解释为任意分辨率/帧率的容量承诺。大帧测试已证明单进程有上限。当前已用视频编码和静止检测降低流量；后续可按设备固定归属划分中转节点，该横向扩展能力未包含在本测试版中。恒定质量模式的码率会随场景变化，不能按简单动画的流量估算所有办公场景。

性能优化实测与取舍见 `PERFORMANCE.md`：原尺寸 DIB 采集、静止降频/输入唤醒、指针独立传输及固定帧内存复用。

## 单文件分发 · REQ-REMOTE-20261105-01

现在优先使用上一级 Windows/ToLink.exe（约 37 MiB）。运行时自动把内置编码器释放到 `%LOCALAPPDATA%\ToLink`，主程序仍直接运行当前 EXE。配置和日志保存在该缓存目录；更新复用已校验编码器，保留设备身份。首次可迁移 EXE 旁的旧 agent.json。旧多文件版本保留于 `dist/portable-before-single/`。

构建命令：`python3 ops/package-single.py`。Windows 实测通过首次释放、SHA256 校验、重复启动复用缓存、配置不覆盖、缓存损坏修复；证据为 `dist/single-build/verification.json`。本次未替换正在运行的远程客户端。

新增 REQ-REMOTE-20261105-02 / 03：远程画面直接 ⌘V / Ctrl+V 粘贴纯文本（单次最多 1000 UTF-16 单元）；Windows 窗口可保存服务器地址，各地址分别保留设备身份。首次启动未注册客户端需先点“保存并连接”。详见 `CLIPBOARD-SERVER-SETTINGS.md`。

## 浏览器撑满 · REQ-REMOTE-20261105-04

- 需求（REQ-REMOTE-20261105-04）：保留原生全屏，新增保留标签页和地址栏的“撑满窗口”。
- 详细设计（REQ-REMOTE-20261105-04）：复用 expanded 固定布局与既有等比例坐标映射，独立于 Fullscreen API；保留“退出撑满”和 Esc 入口。
- 代码开发（REQ-REMOTE-20261105-04）：仅更新 public/index.html、public/app.js、public/style.css，不改 Windows EXE 或推流协议。
- 代码审核（REQ-REMOTE-20261105-04）：退出或断开时清理页面滚动锁定；Esc 退出撑满时拦截事件，避免误操作远程窗口；视图切换释放远程按键。
- 测试（REQ-REMOTE-20261105-04）：4 项坐标映射回归通过。Chrome 实测撑满区域 left=0、top=0、1728×887，与内容区尺寸一致；退出撑满、Esc 退出、原生全屏及退出全屏均通过界面验证。截图见 dist/window-fill-controls.png。

服务端一键部署包：`../../Server/ToLink-Server-linux.tar.gz`。REQ-REMOTE-20261105-05，内置双架构 Node；解压后 `sudo bash install.sh`。安装、升级与失败回滚实测见 `SERVER-DEPLOYMENT.md`。

2026-11-09 更新（REQ-REMOTE-20261105-02、REQ-REMOTE-20261109-02/03）：普通控制页采用整屏布局，文本通过系统剪贴板粘贴。启用控制后，本机复制的多个普通文件可粘贴至 Windows；浏览器没有提供文件内容时使用工具栏的“粘贴文件”。每批最多 32 个文件、合计 100 MiB。详情见 FILE-PASTE.md。

## 锁屏唤醒 · REQ-REMOTE-20261110-01

新增浏览器“唤醒屏幕”。Windows 安装本地辅助服务后可显示锁屏桌面并转发键鼠，解锁后恢复原编码；已在测试机实测。macOS 增加屏幕唤亮及采集恢复，实机锁屏登录待用户手动验收。首次安装、权限边界、测试证据和回退见 [LOCK-SCREEN.md](LOCK-SCREEN.md)。

## 原生流式文件粘贴 · REQ-REMOTE-20261111-01

新版 Windows 通过虚拟文件剪贴板立即启动系统复制窗口，边接收文件边复制到目标目录，支持系统取消。服务器与 Windows EXE 需同时更新。macOS 和旧客户端显示网页接收进度。原理、边界及测试见 [FILE-PASTE.md](FILE-PASTE.md)。
