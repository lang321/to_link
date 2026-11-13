#!/usr/bin/env bash
# REQ-REMOTE-20261105-05: offline, isolated Node runtime; preserve identities on upgrade.
set -Eeuo pipefail
umask 027
BUNDLE_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
INSTANCE=tolink
PORT_OVERRIDE=''
BIND_OVERRIDE=''
PUBLIC_URL=''
CHECK_ONLY=0
usage() {
  cat <<'HELP'
用法：sudo bash install.sh [--port 19000] [--public-url http://服务器IP:19000]
                         [--bind 0.0.0.0] [--instance 名称]
      bash install.sh --check
默认安装 /opt/tolink；配置 /etc/tolink；设备数据 /var/lib/tolink。
升级不指定参数时保留原配置、注册密钥和设备数据。公网 HTTPS 需另配反向代理。
HELP
}
die() { printf '[ToLink] 错误：%s\n' "$*" >&2; exit 1; }
while (($#)); do
  case "$1" in
    --port|--public-url|--bind|--instance)
      (($# >= 2)) || die "$1 缺少参数"
      case "$1" in
        --port) PORT_OVERRIDE="$2";; --public-url) PUBLIC_URL="$2";;
        --bind) BIND_OVERRIDE="$2";; --instance) INSTANCE="$2";;
      esac
      shift 2;;
    --check) CHECK_ONLY=1; shift;;
    --help|-h) usage; exit 0;;
    *) die "未知参数 $1";;
  esac
done
[[ "$(uname -s)" == Linux ]] || die '请在 Linux 服务器上运行'
[[ "$INSTANCE" =~ ^[a-z][a-z0-9-]{0,31}$ ]] || die '实例名称只能包含小写字母、数字和连字符'
case "$(uname -m)" in x86_64) ARCH=x64;; aarch64|arm64) ARCH=arm64;; *) die '仅支持 Linux x86_64 / ARM64';; esac
for utility in sha256sum find cp mv ln; do command -v "$utility" >/dev/null || die "缺少系统工具 $utility"; done
(cd "$BUNDLE_DIR" && sha256sum --check --strict SHA256SUMS >/dev/null) || die '部署包文件校验失败，请重新解压'
NODE="$BUNDLE_DIR/runtime/linux-$ARCH/node"
"$NODE" --version || die '内置 Node 无法运行；需要 glibc 2.28+ 及 libstdc++，不支持 Alpine/musl'
"$NODE" --check "$BUNDLE_DIR/app/server.js"
"$NODE" --check "$BUNDLE_DIR/app/auth.js"
for file in "$BUNDLE_DIR"/app/public/*.js; do "$NODE" --check "$file"; done
"$NODE" -e 'require(process.argv[1]+"/node_modules/ws"); require(process.argv[1]+"/server.js");' "$BUNDLE_DIR/app"
if ((CHECK_ONLY)); then printf '[ToLink] 部署包与 %s 运行环境检查通过\n' "$ARCH"; exit 0; fi
((EUID == 0)) || die '请使用 sudo bash install.sh'
[[ -d /run/systemd/system ]] || die '需要 systemd；容器内请使用 systemd 主机安装方式'
for utility in systemctl flock ss getent useradd groupadd install hostname; do command -v "$utility" >/dev/null || die "缺少系统工具 $utility"; done
exec 9>/run/lock/tolink-install.lock
flock -n 9 || die '已有部署正在运行'
APP_DIR="/opt/$INSTANCE"
CONFIG_DIR="/etc/$INSTANCE"
STATE_DIR="/var/lib/$INSTANCE"
SERVICE="$INSTANCE"
[[ "$INSTANCE" != tolink ]] || SERVICE=tolink-relay
UNIT_FILE="/etc/systemd/system/$SERVICE.service"
BUILD_ID="$(cat "$BUNDLE_DIR/BUILD_ID")"
[[ "$BUILD_ID" =~ ^[a-f0-9]{16}$ ]] || die '构建标识无效'
RELEASE="$APP_DIR/releases/$BUILD_ID"
CONFIG="$CONFIG_DIR/relay.json"
if [[ -d "$APP_DIR" && -n "$(find "$APP_DIR" -mindepth 1 -maxdepth 1 -print -quit)" && ! -f "$APP_DIR/.tolink-managed" ]]; then
  # Permit migration of the existing flat-layout ToLink service, but never adopt an unrelated app.
  "$NODE" -e 'const fs=require("fs");if(JSON.parse(fs.readFileSync(process.argv[1]+"/package.json")).name!=="tolink-windows-remote")process.exit(1)' "$APP_DIR" 2>/dev/null || die "$APP_DIR 已存在且不是 ToLink 安装"
fi
getent group tolink >/dev/null || groupadd --system tolink
id -u tolink >/dev/null 2>&1 || useradd --system --gid tolink --no-create-home --shell /usr/sbin/nologin tolink
install -d -m 755 "$APP_DIR" "$APP_DIR/releases"
[[ -f "$APP_DIR/.tolink-managed" ]] || printf 'REQ-REMOTE-20261105-05\n' >"$APP_DIR/.tolink-managed"
install -d -m 750 -o root -g tolink "$CONFIG_DIR"
install -d -m 700 -o tolink -g tolink "$STATE_DIR"
STAGING="$(mktemp -d "$APP_DIR/.install-XXXXXX")"
ROLLBACK_NEEDED=0
OLD_ACTIVE="$(systemctl is-active "$SERVICE" 2>/dev/null || true)"
OLD_ENABLED="$(systemctl is-enabled "$SERVICE" 2>/dev/null || true)"
OLD_LINK="$(readlink "$APP_DIR/current" 2>/dev/null || true)"
HAD_CONFIG=0; HAD_UNIT=0
BACKUP="$CONFIG_DIR/backups/$(date +%Y%m%d-%H%M%S)-$$"
install -d -m 700 "$BACKUP"
[[ ! -f "$CONFIG" ]] || { cp -a "$CONFIG" "$BACKUP/relay.json"; HAD_CONFIG=1; }
[[ ! -f "$UNIT_FILE" ]] || { cp -a "$UNIT_FILE" "$BACKUP/service.unit"; HAD_UNIT=1; }
cleanup() { rm -rf -- "$STAGING"; }
rollback() {
  local code=$?
  ((code != 0)) || code=1
  trap - ERR INT TERM
  if ((ROLLBACK_NEEDED)); then
    printf '[ToLink] 新版本启动失败，正在恢复上一版本…\n' >&2
    systemctl stop "$SERVICE" >/dev/null 2>&1 || true
    if ((HAD_CONFIG)); then cp -a "$BACKUP/relay.json" "$CONFIG";
    elif [[ -f "$CONFIG" ]]; then mv "$CONFIG" "$BACKUP/failed-relay.json"; fi
    if [[ -n "$OLD_LINK" ]]; then ln -sfn "$OLD_LINK" "$APP_DIR/.current-rollback"; mv -Tf "$APP_DIR/.current-rollback" "$APP_DIR/current";
    elif [[ -L "$APP_DIR/current" ]]; then rm "$APP_DIR/current"; fi
    if ((HAD_UNIT)); then cp -a "$BACKUP/service.unit" "$UNIT_FILE";
    else systemctl disable "$SERVICE" >/dev/null 2>&1 || true; [[ ! -f "$UNIT_FILE" ]] || mv "$UNIT_FILE" "$BACKUP/failed-service.unit"; fi
    systemctl daemon-reload
    if ((HAD_UNIT)); then
      if [[ "$OLD_ENABLED" == enabled ]]; then systemctl enable "$SERVICE" >/dev/null 2>&1; else systemctl disable "$SERVICE" >/dev/null 2>&1 || true; fi
      if [[ "$OLD_ACTIVE" == active ]]; then systemctl start "$SERVICE" || printf '[ToLink] 旧服务恢复失败，请查看 journalctl\n' >&2; fi
    fi
    printf '[ToLink] 已恢复原配置；设备数据未覆盖。备份：%s\n' "$BACKUP" >&2
  fi
  exit "${code:-1}"
}
trap cleanup EXIT
trap rollback ERR INT TERM
DETECTED_IP="$(hostname -I 2>/dev/null | awk '{for(i=1;i<=NF;i++)if($i ~ /^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$/ && $i !~ /^127\./){print $i;exit}}')"
DETECTED_IP="${DETECTED_IP:-127.0.0.1}"
"$NODE" "$BUNDLE_DIR/tools/configure.js" "$CONFIG" "$STAGING/relay.json" "$STATE_DIR" "$DETECTED_IP" "$PORT_OVERRIDE" "$BIND_OVERRIDE" "$PUBLIC_URL"
PORT="$("$NODE" -p 'JSON.parse(require("fs").readFileSync(process.argv[1])).port' "$STAGING/relay.json")"
CURRENT_PID="$(systemctl show "$SERVICE" -p MainPID --value 2>/dev/null || true)"
while IFS= read -r listener; do
  [[ -z "$listener" ]] && continue
  if [[ ! "$CURRENT_PID" =~ ^[1-9][0-9]*$ || "$listener" != *"pid=$CURRENT_PID,"* ]]; then
    die "端口 $PORT 已被其他服务占用，原服务未改变；请使用 --port 指定空闲端口"
  fi
done < <(ss -H -ltnp "sport = :$PORT")
if [[ ! -d "$RELEASE" ]]; then
  install -d -m 755 "$STAGING/release/app" "$STAGING/release/runtime"
  cp -a "$BUNDLE_DIR/app/." "$STAGING/release/app/"
  install -m 755 "$NODE" "$STAGING/release/runtime/node"
  install -m 644 "$BUNDLE_DIR/runtime/NODE-LICENSE.txt" "$STAGING/release/runtime/NODE-LICENSE.txt"
  chown -R root:root "$STAGING/release"
  find "$STAGING/release" -type d -exec chmod 755 {} +
  find "$STAGING/release/app" -type f -exec chmod 644 {} +
  chmod 755 "$STAGING/release"
  mv "$STAGING/release" "$RELEASE"
fi
cat >"$STAGING/service.unit" <<UNIT
[Unit]
Description=ToLink desktop relay (REQ-REMOTE-20261105-05)
After=network.target
[Service]
Type=simple
User=tolink
Group=tolink
WorkingDirectory=$APP_DIR/current/app
Environment=TOLINK_CONFIG=$CONFIG
ExecStart=$APP_DIR/current/runtime/node $APP_DIR/current/app/server.js
Restart=on-failure
RestartSec=3
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
ReadWritePaths=$STATE_DIR
LimitNOFILE=8192
MemoryMax=1G
[Install]
WantedBy=multi-user.target
UNIT
ROLLBACK_NEEDED=1
systemctl stop "$SERVICE" >/dev/null 2>&1 || true
[[ ! -f "$STATE_DIR/devices.json" ]] || cp -a "$STATE_DIR/devices.json" "$BACKUP/devices.json"
install -m 640 -o root -g tolink "$STAGING/relay.json" "$CONFIG"
ln -sfn "$RELEASE" "$APP_DIR/.current-next"
mv -Tf "$APP_DIR/.current-next" "$APP_DIR/current"
install -m 644 "$STAGING/service.unit" "$UNIT_FILE"
systemctl daemon-reload
systemctl enable "$SERVICE" >/dev/null
systemctl restart "$SERVICE"
HEALTHY=0
for attempt in {1..12}; do
  if "$NODE" "$BUNDLE_DIR/tools/health.js" "$CONFIG" >/dev/null 2>&1; then HEALTHY=1; break; fi
  sleep 1
done
if (( ! HEALTHY )); then printf '[ToLink] 健康检查未通过\n' >&2; false; fi
ROLLBACK_NEEDED=0
printf '%s\n' "$BUILD_ID" >"$APP_DIR/.tolink-managed"
install -d -m 755 "$APP_DIR/tools"
install -m 644 "$BUNDLE_DIR/tools/health.js" "$APP_DIR/tools/health.js"
"$NODE" "$BUNDLE_DIR/tools/info.js" "$CONFIG" "$SERVICE" >"$CONFIG_DIR/connection.txt"
chmod 600 "$CONFIG_DIR/connection.txt"
printf '\n[ToLink] 部署成功，服务已启用开机自启动。\n'
cat "$CONFIG_DIR/connection.txt"
printf '\n状态：systemctl status %s\n日志：journalctl -u %s -n 50 --no-pager\n' "$SERVICE" "$SERVICE"
printf '配置/设备数据备份：%s\n防火墙未作修改；请确认访问端口已放行。\n' "$BACKUP"
