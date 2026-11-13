# 构建与开发脚本

所有命令从仓库根目录执行。共享目录由 `scripts/layout.py` 定义，安装包与缓存写入 `dist/`。

## Linux 服务端包

```bash
npm --prefix server ci --ignore-scripts
python3 scripts/package-server.py
```

准备条件：按 `deploy/linux/runtime-lock.json` 的文件名和 SHA-256，将对应 Node.js 发行压缩包放入 `dist/server-runtime/`。脚本不自动下载运行时；缺少缓存或校验失败时不能打包。

产物：`dist/releases/server/ToLink-Server-linux.tar.gz` 及 `.sha256`。包包含 `deploy/linux` 安装工具、服务端／网页、运行时和依赖，安装后运行目录保持不变。

## Windows 客户端

普通版本在 Windows 命令提示符执行：

```bat
cd client\windows
build.cmd
```

输出 `ToLink.exe` 和 `ToLink.DesktopHost.exe`。编译器使用系统 `.NET Framework` 的 `csc.exe`；单文件版本另外嵌入编码器、初始配置及第三方声明。

`package-windows.py`、`package-single.py`、`remote.py`、`deploy.py` 和 `verify-server-package.py` 目前仍是原开发环境的辅助脚本，依赖 Windows HTTP 执行服务、固定测试路径或 Parallels VM，**不是新克隆仓库即可运行的通用构建／部署入口**。使用前应调整环境参数：

| 脚本 | 输入／作用 |
| --- | --- |
| `scripts/package-windows.py` | 读取开发环境 EXE、`private/relay.json` 及 `dist/ffmpeg/`，生成内部便携包 |
| `scripts/package-single.py` | 读取 `dist/ToLink-Windows-x64.zip`，远程编译并校验单文件 EXE；输出 `dist/ToLink-Single.exe` |
| `scripts/deploy.py` | 部署原开发测试环境；会修改目标服务与配置，不用于通用安装 |
| `scripts/verify-server-package.py` | 在专用 VM 实例执行部署验证及故障注入 |

上述 Windows 打包流程会携带注册配置。公开发布前应改为发布专用配置，并核查嵌入资源。

## 测试与工具

- `npm --prefix server test`：服务端、网页工具函数和 Linux 安装配置回归。
- `client/windows/tests/`：Windows 编译／原生剪贴板／流式复制／锁屏辅助服务测试。部分用例会操作系统剪贴板及测试窗口，需在隔离测试机运行；其中的历史测试路径需按环境调整。
- `server/scripts/progress-fixture.js`：本机模拟被控端。通过 `TOLINK_FIXTURE_IMAGE` 提供 JPEG 测试图；未设置时读取 `dist/fixtures/desktop.jpg`。测试账号仅用于此本地 fixture。
- `server/scripts/verify-native-paste.js`：原生剪贴板集成测试。通过 `TOLINK_NATIVE_TEST_EXE` 指定测试程序，默认读取 `dist/windows-tests/NativePasteTest.exe`。
- `server/scripts/load-test.js`：模拟转发压测，需显式提供测试配置和中转地址；结果写入 `dist/load-result.json`。

`dist/legacy/` 保存目录整理前的本地产物；`dist/releases/` 保存已有发行包。它们均不纳入 Git，也不会因为移动目录而被重新发布。
