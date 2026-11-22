#!/usr/bin/env bash
# Install a prebuilt release into the Ubuntu/systemd layout.
set -Eeuo pipefail

usage() {
  cat <<'HELP'
Usage: sudo spawner-update [commit-<full-sha> | <full-sha>]
       sudo spawner-update --configure-auth
Without a tag, install the release for the current master commit.
Use --configure-auth once before upgrading a pre-login installation.
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
# Local HTTP is supported only through the trusted loopback proxy boundary.
# Never send administrator credentials over a remote plaintext connection.
python3 - "$API_URL" <<'PY'
import sys, urllib.parse
url = urllib.parse.urlsplit(sys.argv[1])
if (url.scheme not in ('http', 'https') or not url.hostname or url.username or url.password
        or url.query or url.fragment or url.path not in ('', '/')
        or (url.scheme == 'http' and url.hostname not in ('127.0.0.1', '::1'))):
    raise SystemExit('API_URL must be an HTTPS origin or literal loopback HTTP origin.')
PY
API_URL=${API_URL%/}
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
  echo "Waiting for $SERVICE API at $API_URL..."
  for ((i=0; i<HEALTH_ATTEMPTS; i++)); do
    if {
      systemctl is-active --quiet "$SERVICE" &&
      python3 "$STAGING/api.py" "$API_URL" health
    } >"$STAGING/health-error.log" 2>&1; then
      return 0
    fi
    sleep 2
  done
  echo "API health check failed after $HEALTH_ATTEMPTS attempts at $API_URL." >&2
  cat "$STAGING/health-error.log" >&2
  echo "Check service status and startup logs: systemctl status $SERVICE; journalctl -u $SERVICE -n 100 --no-pager" >&2
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

# Keep the HTTP client embedded so the installed updater remains a single file.
cat >"$STAGING/api.py" <<'PY'
import getpass, http.cookies, json, sys, urllib.error, urllib.parse, urllib.request

base, mode = sys.argv[1:]
cookies = http.cookies.SimpleCookie()

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None

# Do not route local administrative requests through environment-configured proxies.
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())

def request(path, body=None, extra=None):
    headers = dict(extra or {})
    if urllib.parse.urlsplit(base).hostname in ('127.0.0.1', '::1'):
        headers['X-Forwarded-Proto'] = 'https'
    if cookies:
        headers['Cookie'] = '; '.join(m.OutputString(attrs=[]) for m in cookies.values())
    if body is not None:
        headers['Content-Type'] = 'application/json'
    req = urllib.request.Request(base + path, headers=headers,
        data=json.dumps(body).encode() if body is not None else None)
    with opener.open(req, timeout=5) as response:
        for cookie in response.headers.get_all('Set-Cookie', []):
            cookies.load(cookie)
        data = response.read()
        return json.loads(data) if data else None

def servers():
    result = request('/api/v1/servers')['servers']
    if not isinstance(result, list): raise ValueError('Invalid servers response')
    return result

try:
    if mode == 'health':
        try:
            session = request('/api/v1/auth/session')
            if not isinstance(session.get('authenticated'), bool) or not session.get('csrfToken'):
                raise ValueError('Invalid session response')
        except urllib.error.HTTPError as error:
            if error.code != 404: raise
            # Releases predating login have no session endpoint (including rollback).
            servers()
    else:
        try:
            result = servers()
        except urllib.error.HTTPError as error:
            if error.code != 401: raise
            print('Spawner requires login to verify that all instances are stopped.')
            with open('/dev/tty') as tty_in, open('/dev/tty', 'w') as tty:
                tty.write('Administrator username: '); tty.flush()
                username = tty_in.readline().strip()
                password = getpass.getpass('Administrator password: ', stream=tty)
            session = request('/api/v1/auth/session')
            # The backend validates the exact public origin for login, even over loopback.
            with open('/dev/tty') as tty_in, open('/dev/tty', 'w') as tty:
                tty.write('Public HTTPS origin (same as browser address): '); tty.flush()
                origin = tty_in.readline().strip().rstrip('/')
            request('/api/v1/auth/login', dict(username=username, password=password),
                {'Origin': origin, 'X-CSRF-Token': session['csrfToken']})
            result = servers()
            request('/api/v1/auth/logout', {},
                {'Origin': origin, 'X-CSRF-Token': request('/api/v1/auth/session')['csrfToken']})
        busy = [str(s.get('name', s.get('id', '?'))) for s in result if s.get('status') != 'offline']
        if busy:
            raise ValueError('Stop instances and wait for downloads before updating: ' + ', '.join(busy))
except (OSError, ValueError, KeyError, TypeError) as error:
    print('Spawner API check failed: ' + str(error), file=sys.stderr)
    if mode != 'health':
        print('No application files changed. Check API_URL and login details; run interactively if login is required.', file=sys.stderr)
    sys.exit(1)
PY

if [[ "${1:-}" == --configure-auth ]]; then
  python3 - "$BACKEND/appsettings.Production.json" "$BACKUP_ROOT" <<'PY'
import base64, datetime, getpass, hashlib, json, os, pathlib, secrets, shutil, sys, tempfile, urllib.parse
path = pathlib.Path(sys.argv[1])
print('Configure production login. Existing Authentication settings will be replaced.')
print('Systemd Authentication__* environment overrides must be removed or updated separately.')
with open('/dev/tty') as tty_in, open('/dev/tty', 'w') as tty:
    def ask(prompt):
        tty.write(prompt); tty.flush()
        value = tty_in.readline()
        if not value: raise SystemExit('Input cancelled.')
        return value.strip()
    username = ask('Administrator username [admin]: ') or 'admin'
    origin = ask('Public HTTPS origin (for example https://spawner.example.com): ').rstrip('/')
    url = urllib.parse.urlsplit(origin)
    if (not 1 <= len(username) <= 128 or url.scheme != 'https' or not url.hostname
            or url.username or url.password or url.path or url.query or url.fragment):
        raise SystemExit('Use a username of 1–128 characters and a valid HTTPS origin without a path.')
    password = getpass.getpass('New administrator password (16–1024 characters): ', stream=tty)
    if not 16 <= len(password) <= 1024:
        raise SystemExit('Use between 16 and 1024 characters.')
    if password != getpass.getpass('Confirm password: ', stream=tty):
        raise SystemExit('Passwords did not match.')
    config = json.loads(path.read_text()) if path.exists() else {}
    salt = secrets.token_bytes(32)
    digest = hashlib.pbkdf2_hmac('sha256', password.encode(), salt, 600000)
    config['Authentication'] = dict(Username=username, PublicOrigin=origin,
        PasswordHash='pbkdf2-sha256-600000:' + base64.b64encode(salt).decode() + ':' + base64.b64encode(digest).decode())
    if path.exists():
        backup = pathlib.Path(sys.argv[2]) / (path.name + '.before-auth-' + datetime.datetime.now().strftime('%Y%m%d%H%M%S%f'))
        shutil.copy2(path, backup)
        os.chmod(backup, 0o600)
        print('Previous configuration saved to', backup)
    owner = path.stat() if path.exists() else path.parent.stat()
    fd, temporary = tempfile.mkstemp(dir=path.parent, prefix='.auth-')
    try:
        with os.fdopen(fd, 'w') as output:
            json.dump(config, output, indent=2); output.write('\n')
        os.chown(temporary, owner.st_uid, owner.st_gid)
        os.chmod(temporary, 0o640)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary): os.unlink(temporary)
print('Login configured in', path)
print('Ensure the service uses ASPNETCORE_ENVIRONMENT=Production. Then run sudo spawner-update.')
PY
  chown "$BACKEND_OWNER" "$BACKEND/appsettings.Production.json"
  exit 0
fi

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

# Fail closed if the API is unavailable, authentication fails, or any instance is busy.
python3 "$STAGING/api.py" "$API_URL" preflight

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
health || {
  echo 'New backend failed its API health check. If logs report missing Authentication settings, run sudo spawner-update --configure-auth before retrying.' >&2
  exit 1
}
ROLLBACK=false
STOPPED=false
cp "$STAGING/spawner/BUILD_INFO.txt" "$INSTALL_ROOT/BUILD_INFO.txt"
echo "Updated to commit-$SHA. Backup: $BACKUP"
echo 'Refresh the UI and start your Minecraft instances when ready.'
