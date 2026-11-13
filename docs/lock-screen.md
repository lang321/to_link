# 锁屏／息屏唤醒 · REQ-REMOTE-20261110-01

## 需求 · REQ-REMOTE-20261110-01

Windows、macOS 被控端在线时，浏览器可唤亮屏幕，在系统登录界面完成登录后继续控制。
系统睡眠、休眠、关机、Mac 合盖和 FileVault 开机解密不在本次范围。
系统密码由用户在远端登录界面输入，不保存、不写日志、不自动解锁。

## 详细设计 · REQ-REMOTE-20261110-01

- 浏览器“唤醒屏幕” → 已认证设备 WebSocket → 被控端 `wake` → 系统显示器唤醒 → `wakeResult`。服务端按设备隔离，2 秒限流，拒绝附带命令参数。
- Windows 普通桌面沿用现有采集／输入。锁屏进入安全桌面后，经本地命名管道请求 LocalSystem 辅助进程采集 JPEG 和发送键鼠；解锁后恢复用户选择的编码。
- Windows 系统服务仅为安装用户的活动控制台会话启动辅助进程；管道限制用户 SID／会话，拒绝网络用户。客户端校验服务进程位于受保护安装目录。仅开放采集、键鼠、释放和唤亮。
- macOS 使用 `IOPMAssertionDeclareUserActivity` 唤亮显示器，监听锁屏／解锁、显示器唤醒和会话恢复通知，重建 ScreenCaptureKit 采集。采集失败延迟重试；仍需系统录屏和辅助功能授权。
- 锁屏界面拒绝剪贴板粘贴。ToLink 访问密码与操作系统登录密码相互独立。

## 代码开发 · REQ-REMOTE-20261110-01

| 位置 | 变化 |
|---|---|
| `server.js`、`server/public/` | 能力协商、唤醒按钮、设备隔离、限流、结果反馈 |
| `client/windows/DesktopBridge.cs` | 当前输入桌面检测、有界本地管道请求、服务来源校验 |
| `client/windows/DesktopHost.cs` | 系统服务、用户会话辅助进程、独立线程绑定输入桌面、20 秒无输入释放 |
| `client/windows/ToLink.cs` | 锁屏 JPEG 路径、键鼠转发、解锁恢复编码 |
| `client/windows/install-lock-support.ps1` | 管理员安装／升级／卸载，保护 Program Files 安装目录 |
| `macOS/Sources/ScreenPower.swift`、`Agent.swift` | 显示器唤醒、锁屏通知、采集恢复、锁屏剪贴板限制 |

## 代码审核 · REQ-REMOTE-20261110-01

- 服务不接收服务器令牌或系统密码，不开放命令执行、文件访问、注册表操作接口。
- 管道请求 ≤16 KiB、响应 ≤8 MiB，连接／读写有超时；服务只接受安装用户的同一控制台会话。
- 安全桌面操作使用新线程，先绑定桌面再调用 GDI／输入；线程退出后释放句柄。
- 避免将锁屏瞬间的 GDI 失效当成硬件编码故障；解锁恢复原编码。
- 不修改登录策略、UAC、密码策略或睡眠设置。首次安装需要 Windows UAC 授权。
- macOS 锁屏状态通知仅用于恢复采集和限制粘贴，不代替系统权限校验。锁屏画面及键鼠能力尚待实机验收，不能据编译成功认定已可远程解锁。

## 测试 · REQ-REMOTE-20261110-01

日期：2026-11-10（用户时区）。

| 检查 | 结果 |
|---|---|
| Node 回归 | 27/27 通过；新增唤醒能力、隔离、限流、参数拒绝 |
| Windows .NET Framework / C# 5 编译 | 普通客户端、单文件客户端、辅助服务通过 |
| Windows 普通用户调用真实系统服务 | JPEG 解码、唤醒、释放、未知命令／剪贴板拒绝通过 |
| Windows 浏览器实测 | 锁屏 JPEG、唤醒按钮、远程 Enter 登录、解锁后恢复 H.265 通过 |
| Windows 息屏 | 发出显示器关闭指令后，浏览器唤醒返回成功，画面和登录恢复；未使用外部设备测量显示器电源 |
| Windows 系统密码输入 | 测试账号无密码，仅验证登录按钮及 Enter；带密码账号、Ctrl+Alt+Del 策略未验收 |
| macOS 自动回归 | H.264、H.265 各 12 帧解码，文件剪贴板／重名／空文件／取消通过（绝对输出路径） |
| macOS | 编译与签名通过；按用户要求稍后手动验收真实锁屏／密码输入／解锁恢复 |

Windows 单文件分发包完整 SHA-256：`28660f5f7811f17d511b125a7688ee2edbaf06b246058c39099485efb22add41`。打包回传复用已校验的编码器资源，拼合后验证完整 EXE 哈希。

证据：`dist/lock-all-tests.txt`、`dist/lock-runtime.txt`；项目 `output/playwright/windows-locked.png`、`windows-restored-h265.png`。

测试环境已更新本地中转服务及 `192.168.100.105` 客户端；设备 ID、密码、服务器配置保留。Windows 测试结束处于已登录状态，测试浏览器已断开。

### 使用与回退

1. Windows 复制整个 `Windows` 分发目录。首次运行“安装锁屏支持.cmd”，使用运行 ToLink 的同一 Windows 账号接受 UAC，然后正常打开 `ToLink.exe`。
2. 已连接设备的浏览器点击“唤醒屏幕”，再“启用控制”，在登录界面输入系统密码。没有辅助服务时会明确提示安装。
3. Windows 服务卸载：管理员 PowerShell 执行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\install-lock-support.ps1 -Uninstall`。保留设备配置和程序文件。
4. macOS 更新 `ToLink.app` 后检查“屏幕录制”和“辅助功能”权限。锁屏后验证登录画面、键鼠输入和解锁恢复；出现黑屏／不可输入时，保留现象和系统版本继续定位，不应视为已验收。
5. 备份：本地 `dist/REQ-REMOTE-20261110-01-before`；测试 Windows `D:\ToLink\ToLink.before-REQ-REMOTE-20261110-01.exe`；中转 `/opt/tolink/backup-REQ-REMOTE-20261110-01`。

依据：[Windows 输入桌面](https://learn.microsoft.com/en-us/windows/win32/winstation/desktops)、[线程绑定桌面](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setthreaddesktop)、[Apple 显示器唤醒](https://developer.apple.com/documentation/iokit/1557127-iopmassertiondeclareuseractivity)。
