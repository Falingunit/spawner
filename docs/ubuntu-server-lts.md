# Ubuntu Server LTS Deployment Guide

This guide installs and runs Spawner on Ubuntu Server LTS using:

- `.NET 10 SDK / Runtime`
- `Node.js` (only required when building the frontend from source)
- `systemd` (backend service)
- `nginx` (reverse proxy + static frontend hosting)

You can deploy either:

- `Option A (recommended):` install from prebuilt GitHub Release artifacts
- `Option B:` build from source on the server

## 1. Install system dependencies

```bash
sudo apt update
sudo apt install -y curl wget gnupg ca-certificates nginx unzip
```

## 2. Install .NET 10 (Microsoft package repo)

For Ubuntu 22.04 / 24.04 LTS:

```bash
wget https://packages.microsoft.com/config/ubuntu/$(. /etc/os-release; echo $VERSION_ID)/packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt update
sudo apt install -y dotnet-sdk-10.0 aspnetcore-runtime-10.0
```

Verify:

```bash
dotnet --info
```

## 3. Install Node.js (build-time only, source-build deployments)

```bash
curl -fsSL https://deb.nodesource.com/setup_26.x | sudo -E bash -
sudo apt install -y nodejs
sudo npm install -g pnpm@12.21.0
node -v
pnpm -v
```

Skip this step if you deploy using the prebuilt release package.

## 4. Create app user and directories

```bash
sudo useradd --system --home /opt/spawner --create-home --shell /usr/sbin/nologin spawner
sudo mkdir -p /opt/spawner/app
sudo mkdir -p /opt/spawner/www
sudo chown -R spawner:spawner /opt/spawner
```

## 5. Install application files

Choose one of the following installation methods.

### Option A: Install from prebuilt GitHub Release (recommended)

Download the combined package from the release for the commit you want:

```bash
cd /tmp
wget https://github.com/<owner>/<repo>/releases/download/commit-<sha>/spawner-full-linux-x64-<sha>.tar.gz
tar -xzf spawner-full-linux-x64-<sha>.tar.gz
sudo rm -rf /opt/spawner/app/backend
sudo mkdir -p /opt/spawner/app
sudo cp -r spawner/backend /opt/spawner/app/backend
sudo rm -rf /opt/spawner/www/*
sudo cp -r spawner/www/* /opt/spawner/www/
```

### Option B: Build from source on the server

From your deployment checkout location:

### Backend publish

```bash
dotnet publish Spawner/Spawner.csproj -c Release -o /opt/spawner/app/backend
```

### Frontend build

```bash
cd Frontend/spawner
pnpm install --frozen-lockfile
pnpm run build
sudo rm -rf /opt/spawner/www/*
sudo cp -r dist/* /opt/spawner/www/
```

Ensure ownership:

```bash
sudo chown -R spawner:spawner /opt/spawner/app
sudo chown -R www-data:www-data /opt/spawner/www
```

## 6. Backend production config

Create `/opt/spawner/app/backend/appsettings.Production.json` (or edit the published one):

```json
{
  "Settings": {
    "DefaultJavaArgs": "-Xms4G -Xmx8G"
  }
}
```

The frontend, `/api`, and `/ws` must share one HTTPS origin. Before starting the service, generate a password hash using `python3 scripts/hash-admin-password.py` from the source checkout (or copy that script from the release's source archive). It prompts without echoing the password and prints only the salted PBKDF2 hash.

Create `/etc/spawner-auth.env` with mode `0600`, owned by root, containing:

```ini
Authentication__Username=admin
Authentication__PasswordHash=pbkdf2-sha256-600000:<generated salt>:<generated digest>
Authentication__PublicOrigin=https://your-domain.example
```

Replace the entire hash value with the script output. Do not commit this file or put credentials in frontend variables. There is no default login; missing or invalid configuration prevents startup. Restart the service after rotating credentials to revoke all sessions.

## 7. systemd service

Create `/etc/systemd/system/spawner.service`:

```ini
[Unit]
Description=Spawner Backend
After=network.target

[Service]
User=spawner
Group=spawner
WorkingDirectory=/opt/spawner/app/backend
ExecStart=/usr/bin/dotnet /opt/spawner/app/backend/Spawner.dll
Restart=always
RestartSec=5
EnvironmentFile=/etc/spawner-auth.env
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5000

[Install]
WantedBy=multi-user.target
```

Enable/start:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now spawner
sudo systemctl status spawner
```

Logs:

```bash
journalctl -u spawner -f
```

## 8. nginx reverse proxy + static frontend

Create `/etc/nginx/sites-available/spawner`:

```nginx
server {
    listen 80;
    server_name your-domain.example;

    root /opt/spawner/www;
    index index.html;

    add_header X-Content-Type-Options nosniff always;
    add_header X-Frame-Options DENY always;
    add_header Referrer-Policy no-referrer always;
    add_header Content-Security-Policy "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; font-src 'self'; connect-src 'self' https://meta.fabricmc.net; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'" always;

    add_header Cache-Control "no-store" always;

    location / {
        try_files $uri $uri/ /index.html;
    }

    location /api/ {
        proxy_pass http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }

    location /ws/ {
        proxy_pass http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

Enable config:

```bash
sudo ln -s /etc/nginx/sites-available/spawner /etc/nginx/sites-enabled/spawner
sudo nginx -t
sudo systemctl reload nginx
```

## 9. HTTPS (required before signing in)

The HTTP configuration above is only for TLS certificate bootstrap. Use Let's Encrypt and enable its HTTP-to-HTTPS redirect:

```bash
sudo apt install -y certbot python3-certbot-nginx
sudo certbot --nginx --redirect -d your-domain.example
```

Add `add_header Strict-Transport-Security "max-age=31536000" always;` to the HTTPS server block after verifying TLS works. Keep port 5000 inaccessible externally. The backend rejects production requests without HTTPS, trusting `X-Forwarded-Proto` only from a loopback proxy; do not broaden that trust to arbitrary networks. A remote/container proxy requires an explicit trusted proxy configuration before use.

Verify that `/api/v1/servers` returns 401 without a session, the login page works over HTTPS, and logout prevents further API and WebSocket access. Authentication is single-administrator with full host-management privileges; run the service as the dedicated `spawner` user. A backend restart signs everyone out. Do not configure multiple backend replicas without implementing shared sessions and revocation.

## 10. Upgrade / redeploy process

### One-command release updates (recommended)

Install the updater once from your checkout:

```bash
sudo apt install -y curl python3 rsync util-linux
sudo install -m 0755 scripts/update-spawner-release.sh /usr/local/bin/spawner-update
```

No checkout on production? New releases include a standalone `spawner-update`
asset. Download that asset from the chosen GitHub Release, then install it:

```bash
sudo install -m 0755 ./spawner-update /usr/local/bin/spawner-update
```

For each update, stop Minecraft instances in the UI, wait for downloads to
finish, and keep the UI idle during maintenance. Run:

```bash
sudo spawner-update
```

This installs the release for the **current `master` commit**, including prereleases.
If that commit's release is still building or failed, it exits without changing the
installation. It does not select releases from other branches. To choose a
particular release, including an older version:

```bash
sudo spawner-update commit-<full-40-character-sha>
```

The updater downloads and validates the package before stopping the service,
checks that all instances are offline, backs up backend/frontend files to
`/opt/spawner-backups`, installs both components, and checks the running backend's
anonymous `/api/v1/auth/session` endpoint. Before stopping the service, it checks
`/api/v1/servers`; on auth-enabled installations it prompts for the administrator
username, password and public HTTPS origin, logs in with CSRF protection, checks
the instance states, and logs out. Credentials are not stored. Run it from an
interactive terminal. A deployment or startup failure automatically restores
the previous application files and attempts to restart the previous backend.
It prevents concurrent updater runs. Minecraft instances remain stopped; refresh
the UI, verify the update, and start them when ready. Nginx does not need restarting.

After restarting, the updater waits for the API to become ready, retrying up to
`HEALTH_ATTEMPTS` times (default 30) with two seconds between checks. Older updater
versions print temporary curl connection errors during this wait; a single error
does not mean the update failed. Look for the final `Updated to commit-...` message
or a rollback message. If readiness checks fail, inspect
`sudo journalctl -u spawner -n 100 --no-pager` for startup errors.

When upgrading an installation from before login was introduced, install the
latest updater and configure authentication **before** updating:

```bash
curl -fL https://raw.githubusercontent.com/Falingunit/spawner/master/scripts/update-spawner-release.sh -o /tmp/spawner-update &&
sudo install -m 0755 /tmp/spawner-update /usr/local/bin/spawner-update
sudo spawner-update --configure-auth
sudo spawner-update
```

The setup command prompts for the administrator username, the exact public HTTPS
origin used in the browser (no path), and a new password. It writes a salted
password hash to `appsettings.Production.json`, preserves other settings, and saves
a protected backup of the previous config. It does not restart the service.
The service must use `ASPNETCORE_ENVIRONMENT=Production`. This is an alternative
to the environment-file configuration in section 6; an environment file is not
required with this method. Existing `Authentication__*` environment variables
override JSON settings and must be updated or removed separately.

Readiness checks support both auth-enabled releases and older releases during
rollback. For a literal loopback API address, the updater supplies
`X-Forwarded-Proto: https` through ASP.NET's trusted loopback proxy boundary and
handles secure cookies locally. Other API addresses must use HTTPS. Authentication
and the check for busy instances remain enforced; 400/401 responses are not treated
as healthy. A missing or invalid login configuration still fails startup and
triggers rollback.

Existing `appsettings*.json` files and backend directories `Instances`, `Spawner`,
and `JavaRuntimes` are preserved. New configuration files are installed only when
absent; merge new settings from release notes manually. Normal instance data and
Java runtimes under the service user's app-data directory are untouched. Keep
custom data outside the application directories. Application backups are **not**
world backups, and rollback does not reverse data migrations; take a separate
instance-data backup before upgrades. Retained backups are not automatically pruned.

The defaults match this guide and use the public `Falingunit/spawner` repository.
For custom paths, service names or ports, create `/etc/spawner-update.conf`:

```bash
REPO=Falingunit/spawner
BRANCH=master
INSTALL_ROOT=/opt/spawner
FRONTEND_INSTALL_DIR=/opt/spawner/www
BACKUP_ROOT=/opt/spawner-backups
SERVICE=spawner
API_URL=http://127.0.0.1:5000
BACKEND_OWNER=spawner:spawner
FRONTEND_OWNER=www-data:www-data
HEALTH_ATTEMPTS=30
```

If nginx serves `/var/www/spawner/www` and the backend listens on port 5001,
set `FRONTEND_INSTALL_DIR=/var/www/spawner/www` and
`API_URL=http://127.0.0.1:5001`. The frontend need not be moved. When omitted,
`FRONTEND_INSTALL_DIR` defaults to `$INSTALL_ROOT/www`.

This is sourced as shell configuration; it must be root-owned and not writable by
other users (`sudo chown root:root /etc/spawner-update.conf` and
`sudo chmod 0644 /etc/spawner-update.conf`). The updater requires a running,
reachable existing installation, Linux x64 and the ASP.NET Core 10 runtime.
It does not require Node.js, pnpm, Git or the .NET SDK. Private GitHub repositories
are not supported by the download commands. Installing the updater is separate
from updating the application; reinstall it when updater fixes are released.

### Source-build deployments

`scripts/update-spawner.sh` remains available for building `main` on the server.
It requires Git, the .NET SDK and pnpm, and its configuration is at the top of the
script. Prefer the release updater above for production installations.

## Troubleshooting

- Backend fails to start: `journalctl -u spawner -n 200 --no-pager`
- API works but UI fails: check nginx `root`, `try_files`, and browser network requests
- Websocket issues: confirm nginx `/ws/` block includes upgrade headers
- Login returns 403: ensure `Authentication__PublicOrigin` exactly matches the browser origin and proxy `/api` and `/ws` through that same origin.
- Production requests return 400: verify TLS and the loopback nginx `X-Forwarded-Proto` header.
- Login returns 429: wait one minute for the global login attempt budget to reset.
