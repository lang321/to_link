# 文件粘贴 · REQ-REMOTE-20261109-03

**需求 · REQ-REMOTE-20261109-03** 从本机向 Windows 传输多个普通文件。浏览器提供剪贴板文件时支持 ⌘V / Ctrl+V，也可点击“粘贴文件”多选。

**设计 · REQ-REMOTE-20261109-03** 选中远程资源管理器的目标位置后发起传输。中转按设备、已认证的单控制会话和批次 ID 路由。客户端确认写入一个 64 KiB 分块后才读取下一个分块，整批最多 32 个文件、100 MiB。未收齐时取消或断开会清理本批暂存文件。成功后以 CF_HDROP 和复制标志发布，前台窗口与开始传输时一致才发送 Ctrl+V。

**开发 · REQ-REMOTE-20261109-03** public/app.js、server.js、native/IncomingFiles.cs、native/ToLink.cs。单文件客户端将数据放在用户 ToLink 缓存目录下的 IncomingFiles。最近发布的一批保留，其他超过一天的批次在后续接收时清理，缓存预留上限 1 GiB。不同输入文件可同名，每个文件保存在独立子目录。

**审核 · REQ-REMOTE-20261109-03** 校验文件名、声明大小、批次、分块边界、总量和 ACK。Windows 拒绝路径分隔符、保留设备名及无效字符。服务端转发二进制块，设备确认完毕后才接受提交。

**验证 · REQ-REMOTE-20261109-03** 20 项 Node 回归通过。Windows 回环中转和真实客户端收到 3 个文件，CF_HDROP 条目、中文文件名、零字节文件、70,000 字节跨块二进制及普通文本文件内容核对通过。测试窗口收到原生 Ctrl+V。未使用资源管理器实际目标目录做覆盖或冲突处理验收。

**文本与页面** REQ-REMOTE-20261105-02 快捷粘贴走 Windows Unicode 文本剪贴板和 Ctrl+V，保留 1000 UTF-16 单元限制。REQ-REMOTE-20261109-02 删除底部“发送文字”输入区；1728×887 浏览器验收中页面宽高与视口完全一致。

## 原生流式粘贴 · REQ-REMOTE-20261111-01

**需求 · REQ-REMOTE-20261111-01** 用户进一步明确需要云桌面式体验：粘贴后立即开始传输，由 Windows 原生复制窗口显示进度，而非整批下载完成后才触发复制。Windows 采用流式虚拟文件；macOS 和旧客户端保留网页进度条。

**详细设计 · REQ-REMOTE-20261111-01** `hello.streamingFilePaste` 协商能力；浏览器 `filesBegin.streaming=true` 先发送文件名、大小。Windows 通过 `FileGroupDescriptorW / FileContents / IStream` 立即发布虚拟文件并发送 Ctrl+V。每个 64 KiB 分块写入受限暂存目录、Flush 后开放给 IStream；资源管理器同时读取并写目标文件。IStream 等待尚未到达的数据，不能把暂时无数据当作 EOF。原生窗口负责进度、冲突和取消。数据对象的 `EndOperation` 取消回调中止后续接收；浏览器断开亦终止未完成流。剪贴板 COM 使用独立 STA，不阻塞主界面和网络接收。

**代码开发 · REQ-REMOTE-20261111-01** 新增 `native/StreamingClipboard.cs`；复用 `IncomingFiles` 的文件名校验、大小上限、暂存和分块回执。更新 `ToLink.cs`、中转能力透传、浏览器能力选择和构建脚本。支持流式模式的 Windows 不再显示网页上传卡片；兼容模式显示按远端 ACK 计算的进度、文件名、总字节、准备粘贴及错误状态。

**代码审核 · REQ-REMOTE-20261111-01** 不自动执行传入路径或命令，文件仍限制为 32 个、100 MiB；复制模式不删除源文件。不把网络发送完毕称为目标目录复制完成。系统复制未结束时拒绝启动另一批原生复制。文件读取支持 Seek/Clone、正确 EOF、超时和取消。网页旧批次异步回调不能覆盖新批次。`STREAM_PROOF` 编译开关仅用于测试索引诊断，发行版不记录它。

**测试 · REQ-REMOTE-20261111-01**

- Node 回归 29/29：含能力协商、未传数据即可触发粘贴回执、原生取消后重新开始、旧客户端拒绝流式请求。
- Windows 原生复制：4 MiB 文件到达约 256 KiB 时，系统 IStream 已实际读出分块；最终 SHA-256 一致。真实复制窗口截图位于项目 `output/playwright/native-stream-copy.jpg`。
- Windows 多文件：大文件、中文文件名、零字节文件共 3 个，均通过目标文件内容校验；本轮 `D:\soft` 测试输出已清理。系统请求顺序为索引 0、1、2。
- 原生取消：在系统“取消”按钮上执行操作后，约 320 KiB 处停止接收。
- IStream：分块等待、正确 EOF、内容、Seek/Clone、取消回归通过。
- 浏览器：原生模式即时粘贴提示和失败提示通过；兼容模式的多文件、0 字节、远端错误、停止／断开、重新传输及 390px 布局通过。浏览器此次拒绝原生 Fullscreen API，已验证现有撑满回退路径，未将其计作原生全屏实测。

部署：本地测试中转与 `192.168.100.105` Windows 客户端更新；保留设备配置和锁屏服务。其他环境需同时更新服务器包及 Windows EXE。此次无需更新 macOS APP。

使用：先在远端资源管理器选中目标文件夹，再在浏览器粘贴文件；浏览器未提供文件内容时仍需点“粘贴文件”选择。传输期间保持浏览器连接。网络数据完整到达后，最终复制结果以 Windows 窗口为准。小文件的系统窗口可能很快消失。

依据：[Shell 文件剪贴板格式](https://learn.microsoft.com/en-us/windows/win32/shell/clipboard)、[异步数据提取](https://learn.microsoft.com/en-us/windows/win32/api/shldisp/nn-shldisp-idataobjectasynccapability)。

**用户验收 · REQ-REMOTE-20261111-01** 2026-11-11 用户确认“功能已经正常了”，并要求收尾。Windows EXE 与服务器分发包已核对哈希；测试浏览器和本地模拟被控端已关闭，保留测试证据与回退备份。
