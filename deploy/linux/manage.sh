#!/usr/bin/env bash
# REQ-REMOTE-20261105-05
set -euo pipefail
ACTION="${1:-status}"
INSTANCE="${2:-tolink}"
[[ "$INSTANCE" =~ ^[a-z][a-z0-9-]{0,31}$ ]] || { echo '实例名称无效' >&2; exit 1; }
SERVICE="$INSTANCE"; [[ "$INSTANCE" != tolink ]] || SERVICE=tolink-relay
case "$ACTION" in
  start|stop|restart) ((EUID == 0)) || { echo '请使用 sudo' >&2; exit 1; }; systemctl "$ACTION" "$SERVICE";;
  status) systemctl status "$SERVICE" --no-pager;;
  logs) journalctl -u "$SERVICE" -n 100 --no-pager;;
  info) cat "/etc/$INSTANCE/connection.txt";;
  *) echo '用法：sudo bash manage.sh start|stop|restart|status|logs|info [实例名称]' >&2; exit 1;;
esac
