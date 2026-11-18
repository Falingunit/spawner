#!/usr/bin/env bash
# Install a prebuilt release into the Ubuntu/systemd layout.
set -Eeuo pipefail

usage() {
  cat <<'HELP'
Usage: sudo spawner-update [commit-<full-sha> | <full-sha>]
Without a tag, install the release for the current master commit.
Stop Minecraft instances in the UI first and wait for downloads to finish.
Configuration: /etc/spawner-update.conf (optional, root-owned shell assignments)
Requires: curl, python3, rsync, flock, dotnet and systemd. No build tools needed.
HELP
}
[[ "${1:-}" != --help && "${1:-}" != -h ]] || { usage; exit 0; }
[[ $# -le 1 ]] || { usage >&2; exit 1; }
[[ $(id -u) == 0 ]] || { echo 'Run with sudo.' >&2; exit 1; }

REPO=Falingunit/spawner
BRANCH=master
INSTALL_ROOT=/opt/spawner
BACKUP_ROOT=/opt/spawner-backups
SERVICE=spawner
API_URL=http://127.0.0.1:5000
BACKEND_OWNER=spawner:spawner
FRONTEND_OWNER=www-data:www-data
HEALTH_ATTEMPTS=30
if [[ -f /etc/spawner-update.conf ]]; then
  [[ $(stat -c %u /etc/spawner-update.conf) == 0 ]] || { echo 'Config must be root-owned.' >&2; exit 1; }
  [[ -z $(find /etc/spawner-update.conf -perm /022 -print) ]] || { echo 'Config must not be group/world writable.' >&2; exit 1; }
  source /etc/spawner-update.conf
fi
for cmd in curl python3 rsync flock dotnet systemctl; do
  command -v "$cmd" >/dev/null || { echo "Missing dependency: $cmd" >&2; exit 1; }
done
[[ $(uname -m) == x86_64 ]] || { echo 'Release packages require Linux x64.' >&2; exit 1; }
dotnet --list-runtimes | grep -q '^Microsoft.AspNetCore.App 10\.' || { echo 'Install the ASP.NET Core 10 runtime first.' >&2; exit 1; }
[[ "$INSTALL_ROOT" == /* && "$INSTALL_ROOT" != / && "$BACKUP_ROOT" == /* && "$BACKUP_ROOT" != / ]] || exit 1
BACKEND="$INSTALL_ROOT/app/backend"
FRONTEND="${FRONTEND_INSTALL_DIR:-$INSTALL_ROOT/www}"
[[ "$FRONTEND" == /* && "$FRONTEND" != / ]] || { echo 'Frontend path must be an absolute directory other than /.' >&2; exit 1; }
[[ -f "$BACKEND/Spawner.dll" && -d "$FRONTEND" ]] || { echo 'Existing Spawner installation not found.' >&2; exit 1; }
mkdir -p "$BACKUP_ROOT"
exec 9>"$BACKUP_ROOT/update.lock"
flock -n 9 || { echo 'Another update is running.' >&2; exit 1; }
STAGING=$(mktemp -d)
BACKUP=''
ROLLBACK=false
STOPPED=false

# Keep local configuration and runtime data, including legacy/fallback locations.
PRESERVE=(--exclude='/appsettings*.json' --exclude='/Instances' --exclude='/Spawner' --exclude='/JavaRuntimes')
health() {
  local i
  for ((i=0; i<HEALTH_ATTEMPTS; i++)); do
    if systemctl is-active --quiet "$SERVICE" &&
      curl -fsS --max-time 5 "$API_URL/api/v1/servers" -o "$STAGING/health.json" &&
      python3 -c 'import json,sys; assert isinstance(json.load(open(sys.argv[1]))["servers"], list)' "$STAGING/health.json"; then
      return 0
    fi
    sleep 2
  done
  return 1
}
finish() {
  local result=$?
  trap - EXIT INT TERM
  if [[ "$ROLLBACK" == true ]]; then
    echo "Update failed; restoring application files from $BACKUP" >&2
    if systemctl stop "$SERVICE" &&
      rsync -a --delete "${PRESERVE[@]}" "$BACKUP/backend/" "$BACKEND/" &&
      rsync -a --delete "$BACKUP/www/" "$FRONTEND/" &&
      systemctl start "$SERVICE" && health; then
      echo 'Previous application restored. Minecraft instances remain stopped.' >&2
    else
      echo "Rollback needs attention. Backup: $BACKUP; logs: journalctl -u $SERVICE" >&2
    fi
    result=1
  elif [[ "$STOPPED" == true ]]; then
    systemctl start "$SERVICE" || true
  fi
  rm -rf "$STAGING"
  exit "$result"
}
trap finish EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

SHA=${1:-}
if [[ -z "$SHA" ]]; then
  echo "Resolving $REPO branch $BRANCH..."
  curl -fsSL --retry 3 --connect-timeout 15 --max-time 60 \
    "https://api.github.com/repos/$REPO/commits/$BRANCH" -o "$STAGING/commit.json"
  SHA=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["sha"])' "$STAGING/commit.json")
fi
SHA=${SHA#commit-}
[[ "$SHA" =~ ^[0-9a-f]{40}$ ]] || { echo 'Use a full 40-character commit SHA or commit-<sha> tag.' >&2; exit 1; }
echo "Downloading commit-$SHA (if unavailable, wait for the release workflow or specify an older tag)..."
curl -fL --retry 3 --connect-timeout 15 --max-time 600 \
  "https://github.com/$REPO/releases/download/commit-$SHA/spawner-full-linux-x64-$SHA.tar.gz" \
  -o "$STAGING/release.tar.gz"
# Reject links and paths outside the package before extracting as root.
python3 - "$STAGING" <<'PY'
import pathlib, sys, tarfile
root = pathlib.Path(sys.argv[1])
with tarfile.open(root / 'release.tar.gz') as archive:
    for item in archive.getmembers():
        path = pathlib.PurePosixPath(item.name)
        if path.is_absolute() or '..' in path.parts or not path.parts or path.parts[0] != 'spawner' or not (item.isfile() or item.isdir()):
            raise SystemExit('Unsafe release archive entry: ' + item.name)
    archive.extractall(root)
PY
[[ -f "$STAGING/spawner/backend/Spawner.dll" && -f "$STAGING/spawner/www/index.html" ]] || { echo 'Incomplete release package.' >&2; exit 1; }
grep -qx "commit=$SHA" "$STAGING/spawner/BUILD_INFO.txt" || { echo 'Package commit does not match requested release.' >&2; exit 1; }

# Fail closed if the API is unavailable or any instance is busy.
curl -fsS --max-time 10 "$API_URL/api/v1/servers" -o "$STAGING/servers.json"
python3 - "$STAGING/servers.json" <<'PY'
import json, sys
servers = json.load(open(sys.argv[1]))['servers']
assert isinstance(servers, list)
busy = [s.get('name', s.get('id', '?')) for s in servers if s.get('status') != 'offline']
if busy:
    raise SystemExit('Stop instances and wait for downloads before updating: ' + ', '.join(busy))
PY

echo 'Stopping Spawner and backing up application files...'
STOPPED=true
systemctl stop "$SERVICE"
# Restart the existing installation if backup creation fails before deployment.
ROLLBACK=false
BACKUP=$(mktemp -d "$BACKUP_ROOT/update-$(date +%Y%m%d-%H%M%S)-XXXXXX") || { systemctl start "$SERVICE"; exit 1; }
if ! cp -a "$BACKEND" "$BACKUP/backend" || ! cp -a "$FRONTEND" "$BACKUP/www"; then
  systemctl start "$SERVICE"
  exit 1
fi
ROLLBACK=true
rsync -a --delete "${PRESERVE[@]}" "$STAGING/spawner/backend/" "$BACKEND/"
# Install newly introduced configuration files without replacing local settings.
for config in "$STAGING/spawner/backend"/appsettings*.json; do
  [[ -f "$config" ]] || continue
  [[ -e "$BACKEND/$(basename "$config")" ]] || cp -a "$config" "$BACKEND/"
done
rsync -a --delete "$STAGING/spawner/www/" "$FRONTEND/"
chown -R "$BACKEND_OWNER" "$BACKEND"
chown -R "$FRONTEND_OWNER" "$FRONTEND"
systemctl start "$SERVICE"
health || { echo 'New backend failed its API health check.' >&2; exit 1; }
ROLLBACK=false
STOPPED=false
cp "$STAGING/spawner/BUILD_INFO.txt" "$INSTALL_ROOT/BUILD_INFO.txt"
echo "Updated to commit-$SHA. Backup: $BACKUP"
echo 'Refresh the UI and start your Minecraft instances when ready.'
