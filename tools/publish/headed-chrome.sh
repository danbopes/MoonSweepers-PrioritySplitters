#!/usr/bin/env bash
# A visible Chrome on the desktop session, using the Playwright MCP profile that holds the Nexus Mods login,
# with the DevTools port open so the Playwright MCP can drive it through browserType().connectOverCDP().
# Why not the MCP's own browser: a Claude session started from a terminal has no DISPLAY, so that browser is
# headless, and Cloudflare's bot check only clears in a real window. Why this profile: Nexus has no upload
# API, and the login lives in whichever MCP profile was last used to sign in.
#
#   headed-chrome.sh start      launch (no-op if already up)
#   headed-chrome.sh status     list open tabs
#   headed-chrome.sh tab <url>  open a Chrome-owned tab (tabs made with ctx.newPage() die when the call ends)
#   headed-chrome.sh stop       quit it, so the profile is free for the next MCP session
set -euo pipefail
port=9222
cmd=${1:-status}
root=$(cd "$(dirname "$0")/../.." && pwd)
log="$root/tools/.tmp/headed-chrome.log"

desktop_env() {
  # Borrow the display variables from a process that is on the desktop.
  local pid
  for pid in $(pgrep -u "$USER" -x 'steam|gnome-shell|kwin_wayland|plasmashell|sway|Hyprland|Xwayland' 2>/dev/null); do
    if tr '\0' '\n' < "/proc/$pid/environ" 2>/dev/null | grep -qE '^(WAYLAND_DISPLAY|DISPLAY)='; then
      tr '\0' '\n' < "/proc/$pid/environ" | grep -E '^(DISPLAY|WAYLAND_DISPLAY|XAUTHORITY|XDG_RUNTIME_DIR|XDG_SESSION_TYPE|DBUS_SESSION_BUS_ADDRESS)='
      return 0
    fi
  done
  return 1
}

profile_dir() {
  # The MCP profile whose cookie jar holds the freshest nexusmods_session cookie. Only names and expiry are read.
  python3 -I - "$HOME" <<'PY'
import datetime, glob, os, shutil, sqlite3, sys, tempfile
home = sys.argv[1]
best = None
for db in glob.glob(f"{home}/.cache/ms-playwright*/mcp-chrome-*/Default/Cookies"):
    tmp = tempfile.NamedTemporaryFile(suffix=".sqlite", delete=False).name
    try:
        shutil.copy(db, tmp)
        row = sqlite3.connect(tmp).execute(
            "select max(expires_utc) from cookies where host_key like '%nexusmods.com' and name = 'nexusmods_session'").fetchone()
    finally:
        os.unlink(tmp)
    if row and row[0] and (best is None or row[0] > best[0]):
        best = (row[0], os.path.dirname(os.path.dirname(db)))
if best is None:
    sys.exit("no Chrome profile under ~/.cache/ms-playwright* holds a nexusmods_session cookie: sign in to Nexus in the Playwright browser once")
expires = datetime.datetime(1601, 1, 1) + datetime.timedelta(microseconds=best[0])
if expires < datetime.datetime.utcnow():
    print(f"warning: the Nexus session in {best[1]} expired on {expires:%Y-%m-%d}; sign in again in the window", file=sys.stderr)
print(best[1])
PY
}

case $cmd in
  start)
    if curl -s --max-time 1 "http://127.0.0.1:$port/json/version" >/dev/null; then
      echo "already running on port $port"; exit 0
    fi
    profile=$(profile_dir)
    if pgrep -f -- "--user-data-dir=$profile" >/dev/null; then
      echo "profile $profile is in use by another Chrome" >&2; exit 1
    fi
    rm -f "$profile/SingletonLock" "$profile/SingletonSocket" "$profile/SingletonCookie"
    mkdir -p "$(dirname "$log")"
    envs=$(desktop_env) || { echo "no desktop session found to show the window on" >&2; exit 1; }
    # shellcheck disable=SC2086
    env -i HOME="$HOME" USER="$USER" PATH=/usr/local/bin:/usr/bin:/bin LANG="${LANG:-en_US.UTF-8}" $envs \
      setsid nohup /opt/google/chrome/chrome --user-data-dir="$profile" --remote-debugging-port=$port \
      --remote-allow-origins='*' --window-size=1400,1000 --no-first-run --no-default-browser-check \
      --disable-session-crashed-bubble --hide-crash-restore-bubble about:blank >"$log" 2>&1 </dev/null &
    disown
    for _ in $(seq 1 40); do
      curl -s --max-time 1 "http://127.0.0.1:$port/json/version" >/dev/null && break
      sleep 0.5
    done
    curl -s "http://127.0.0.1:$port/json/version" | python3 -I -c 'import json,sys; print("headed Chrome up:", json.load(sys.stdin)["Browser"])' \
      || { echo "Chrome did not open; see $log" >&2; exit 1; }
    echo "profile: $profile"
    ;;
  stop)
    profile=$(profile_dir 2>/dev/null || true)
    pkill -f -- "--remote-debugging-port=$port --remote-allow-origins" || true
    echo "stopped${profile:+ ($profile is free again)}"
    ;;
  status)
    curl -s --max-time 1 "http://127.0.0.1:$port/json/list" \
      | python3 -I -c 'import json,sys; [print(t["id"][:8], t["url"][:110]) for t in json.load(sys.stdin) if t["type"] == "page"]' \
      || echo "not running"
    ;;
  tab)
    curl -s -X PUT "http://127.0.0.1:$port/json/new?$2" | python3 -I -c 'import json,sys; d=json.load(sys.stdin); print(d["id"][:8], d["url"])'
    ;;
  *)
    echo "usage: $0 start|status|tab <url>|stop" >&2; exit 2
    ;;
esac
