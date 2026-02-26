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
curl -fsSL https://deb.nodesource.com/setup_20.x | sudo -E bash -
sudo apt install -y nodejs
node -v
npm -v
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
npm ci
npm run build
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
  "Cors": {
    "AllowedOrigins": ["https://your-domain.example"]
  },
  "Settings": {
    "DefaultJavaArgs": "-Xms4G -Xmx8G"
  }
}
```

If nginx serves the frontend from the same domain and proxies `/api` and `/ws`, you can leave `AllowedOrigins` empty because same-origin requests do not require CORS.

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

## 9. HTTPS (recommended)

Use Let's Encrypt:

```bash
sudo apt install -y certbot python3-certbot-nginx
sudo certbot --nginx -d your-domain.example
```

## 10. Upgrade / redeploy process

### Option A: Upgrade using prebuilt release package

```bash
cd /tmp
wget https://github.com/<owner>/<repo>/releases/download/commit-<sha>/spawner-full-linux-x64-<sha>.tar.gz
tar -xzf spawner-full-linux-x64-<sha>.tar.gz
sudo rm -rf /opt/spawner/app/backend
sudo cp -r spawner/backend /opt/spawner/app/backend
sudo rm -rf /opt/spawner/www/*
sudo cp -r spawner/www/* /opt/spawner/www/
sudo chown -R spawner:spawner /opt/spawner/app
sudo chown -R www-data:www-data /opt/spawner/www
sudo systemctl restart spawner
sudo systemctl status spawner
```

### Option B: Upgrade from source

```bash
# Pull latest source
git pull

# Publish backend
dotnet publish Spawner/Spawner.csproj -c Release -o /opt/spawner/app/backend

# Build frontend
cd Frontend/spawner
npm ci
npm run build
sudo rm -rf /opt/spawner/www/*
sudo cp -r dist/* /opt/spawner/www/

# Restart backend
sudo systemctl restart spawner
sudo systemctl status spawner
```

## Troubleshooting

- Backend fails to start: `journalctl -u spawner -n 200 --no-pager`
- API works but UI fails: check nginx `root`, `try_files`, and browser network requests
- Websocket issues: confirm nginx `/ws/` block includes upgrade headers
- Cross-origin browser errors: set `Cors:AllowedOrigins` when frontend and backend are on different origins
