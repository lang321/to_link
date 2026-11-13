ToLink 服务端一键部署包
需求编号：REQ-REMOTE-20261105-05

【快速部署】
tar -xzf ToLink-Server-linux.tar.gz
cd tolink-server
sudo bash install.sh

脚本自动选择 x86_64/ARM64 内置 Node.js，创建服务账户、生成注册密钥、安装并启动 systemd 服务。
不需要联网、不需要 npm install、不修改系统 Node、不安装 FFmpeg、不依赖数据库。
需要 systemd、内核 4.18+、glibc 2.28+、libstdc++ 6.0.25+。建议使用仍受支持的发行版，例如 Ubuntu 22.04/24.04、Debian 12+、Rocky 8+。实际验收环境为 Ubuntu 24.04 ARM64。
不支持 macOS、Windows、Alpine/musl 或没有 systemd 的普通容器。

【指定端口和对外地址】
sudo bash install.sh --port 19000 --public-url http://服务器IP:19000

默认从本机网卡推断地址。云服务器公网 IP、NAT 映射地址或域名不一定能自动识别，需指定 --public-url。
端口已被其他服务占用时会拒绝部署，不会终止占用端口的程序。

【客户端连接】
部署结束会显示浏览器地址和注册密钥，也保存到 /etc/tolink/connection.txt。
Windows 客户端填写服务器地址、注册密钥，点击“保存并连接”，获得设备 ID。
浏览器打开服务地址，使用设备 ID 和设备密码登录。注册密钥不是设备密码。

【服务管理】
sudo bash manage.sh status
sudo bash manage.sh restart
sudo bash manage.sh stop
sudo bash manage.sh start
sudo bash manage.sh logs
sudo bash manage.sh info

【升级】
解压新包，重新执行 sudo bash install.sh。
不带参数时保留配置、注册密钥和设备数据；带参数只修改对应设置。
程序保存在 /opt/tolink/releases/，current 指向当前版本。
旧配置和设备文件快照保存在 /etc/tolink/backups/，旧程序目录不自动删除。
新版本健康检查失败时自动恢复旧程序、配置和 service 文件；设备数据不覆盖。
自定义 stateFile 不在 /var/lib/tolink/devices.json 时拒绝自动迁移，避免误改其他数据目录。

【持久化】
/etc/tolink/relay.json：配置、注册密钥
/var/lib/tolink/devices.json：设备 ID、设备令牌、密码哈希
请备份以上两处。不要让多个实例共享写入同一个设备文件。

【公网 HTTPS】
本包默认提供 HTTP/WS。公网使用需先准备域名、证书和 HTTPS 反向代理。
sudo bash install.sh --bind 127.0.0.1 --public-url https://remote.example.com
上面的命令不会自动安装 Nginx 或申请证书；仅设置应用监听地址、Origin 和 Secure Cookie。
Nginx 必须代理整个站点到 http://127.0.0.1:19000，并配置：
proxy_http_version 1.1;
proxy_set_header Upgrade $http_upgrade;
proxy_set_header Connection "upgrade";
proxy_read_timeout 3600s;
proxy_buffering off;
不要只代理网页而遗漏 /ws/agent、/ws/viewer 和 /api/ 路径。
脚本不改变防火墙，需自行放行对外访问端口。

【包检查与隔离实例】
bash install.sh --check
sudo bash install.sh --instance tolink-test --port 19003 --public-url http://服务器IP:19003
sudo bash manage.sh status tolink-test
独立实例拥有各自的程序、配置、数据和 systemd 服务。默认服务名为 tolink-relay。

maxDevices 是注册设备数量限制，不代表任意码率下的并发保证。此前完成 128 路模拟转发验证，未完成百台实机及长时联测。
Node 运行环境来自 nodejs.org 官方发行包，原包 SHA256 和版本记录在 runtime/SOURCE.json。
许可证见 runtime/NODE-LICENSE.txt 和 app/node_modules/ws/LICENSE。
