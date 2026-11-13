# 仓库结构调整 · REQ-REPO-20261113-01

**需求 · REQ-REPO-20261113-01** 补充适合 GitHub 首页的 README，将 Windows 客户端、服务端、网页、部署工具、文档和本地产物分开组织。

**详细设计 · REQ-REPO-20261113-01**

| 原路径 | 新路径 |
| --- | --- |
| `Windows/windows-remote/native/` | `client/windows/` |
| `Windows/windows-remote/ops/*.cs` | `client/windows/tests/` |
| `Windows/windows-remote/server.js`、`auth.js`、npm 文件 | `server/` |
| `Windows/windows-remote/public/`、`test/` | `server/public/`、`server/test/` |
| `Windows/windows-remote/ops/*.js` | `server/scripts/` |
| `Windows/windows-remote/ops/*.py` | `scripts/` |
| `Windows/windows-remote/deploy/` | `deploy/linux/` |
| 原技术说明 | `docs/`，原模块 README 改为 `development-history.md` |
| `Windows/windows-remote/private/`、`dist/` | 根目录 `private/`、`dist/` |
| 原 Windows 分发文件、Server Linux 包 | `dist/releases/windows/`、`dist/releases/server/` |
| 重复的旧服务端目录 EXE、旧说明等 | `dist/legacy/` |

**代码开发 · REQ-REPO-20261113-01** 新增根 README、`.gitignore`、脚本说明和共享路径定义 `scripts/layout.py`；修正打包源路径、输出目录、辅助脚本资源路径和默认服务配置路径。服务端默认读根目录 `private/relay.json`，`TOLINK_CONFIG` 仍可覆盖。示例配置改为本机地址。运行时锁定清单放在 `deploy/linux/runtime-lock.json`，压缩包缓存保留在 `dist/server-runtime/`。

**代码审核 · REQ-REPO-20261113-01** 原有 68 个版本控制文件全部找到对应位置；Windows C# 运行代码与测试代码内容未变。配置、依赖和产物未删除，分别保留于被忽略目录。目录及 Git 路径统一使用小写 `server`。历史 EXE 含测试注册配置，已从当前源码布局移出；Git 历史未改写。项目许可证仍待确定。

**测试 · REQ-REPO-20261113-01**

- 服务端、网页函数和安装配置测试 32/32 通过。
- README 初始化与启动步骤在隔离目录通过：拒绝覆盖已有配置、健康检查、页面访问、设备注册和状态持久化。
- Python 脚本编译检查、Node 辅助脚本语法检查、Linux shell 语法检查通过。
- Linux 服务端包在新结构下构建成功，产物位于 `dist/releases/server/`。
- 本次未修改 Windows 运行逻辑，也未重新部署在线服务。Windows 原生功能验收沿用对应功能记录。
- 本地核对结果保存在 `dist/REQ-REPO-20261113-01-verification.json`，不纳入 Git。
