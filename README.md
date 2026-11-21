# Spawner

Spawner is a Minecraft server instance manager with:

- `Spawner/`: ASP.NET Core backend API + websocket server
- `Frontend/spawner/`: React + Vite admin UI
- `Spawner.Tests/`: backend unit tests
- `docs/`: deployment and integration notes

## Production-ready changes in this repo

- Backend/test targets aligned to `.NET 10`
- Administrator login protects HTTP APIs and WebSocket connections
- Frontend route-level lazy loading to reduce initial bundle load
- CI workflow for backend tests + frontend production build
- Ubuntu Server LTS deployment guide (systemd + nginx)

## Access security

Spawner requires an administrator login. There are no default credentials or public registration. All API endpoints (including files, icons, and exports) and WebSocket connections require authentication; only the session/CSRF bootstrap and login endpoints are public.

Configure `Authentication__Username`, `Authentication__PasswordHash`, and `Authentication__PublicOrigin` before starting the backend. Generate the password hash interactively with `python3 scripts/hash-admin-password.py` (16-character minimum). Store the hash in a protected environment file, never in source control. `PublicOrigin` is the exact browser origin, for example `https://spawner.example.com`, without a path.

Production requires HTTPS through a same-origin reverse proxy. Keep the backend on `127.0.0.1:5000`; only loopback proxies are trusted to supply `X-Forwarded-Proto`. See [deployment instructions](docs/ubuntu-server-lts.md) for credentials, TLS, and proxy headers. Cross-origin frontend/API deployments and the old `VITE_API_ORIGIN` / `VITE_WS_ORIGIN` settings are no longer supported.

Sessions use HttpOnly, SameSite=Strict cookies (Secure in production), expire after eight hours without renewal, and are revoked on logout or backend restart. Logout/expiry also disconnects existing sockets. State changes require an antiforgery token and the configured Origin; WebSocket handshakes require that Origin. Login attempts have a global limit of ten per minute. This limits distributed guessing but can temporarily delay legitimate sign-ins during an attack; use edge rate limiting for sustained abuse. Sessions are local to one backend process, so multiple replicas require a shared session store before deployment.

The administrator can upload executable server content and change launch settings. Treat this account as trusted host administration and run Spawner as a dedicated unprivileged OS user. This is not a multi-tenant security boundary. Changing the password hash requires restarting the service, which invalidates existing sessions.

## Local development

### Backend

```bash
# Generate a hash first, then set these in your local environment:
export Authentication__Username='admin'
export Authentication__PasswordHash='<generated hash>'
export Authentication__PublicOrigin='http://localhost:5173'
dotnet run --project Spawner
```

Notes:
- Authentication remains required in development; loopback HTTP is allowed.
- The frontend Vite config proxies `/api` and `/ws` to `http://localhost:5000` in dev.

### Frontend

Use Node.js 26+ and pnpm 12.21.0 (pinned in `package.json`).

```bash
cd Frontend/spawner
pnpm install --frozen-lockfile
pnpm run dev
```

## Build for production

### Backend publish

```bash
dotnet publish Spawner/Spawner.csproj -c Release -o .tmp-build/backend
```

### Frontend build

```bash
cd Frontend/spawner
pnpm install --frozen-lockfile
pnpm run build
```

Output:
- Backend publish output in `.tmp-build/backend`
- Frontend static files in `Frontend/spawner/dist`

## Install (Prebuilt Release)

If GitHub Releases are enabled with `.github/workflows/release-per-commit.yml`, each commit can publish prebuilt artifacts:

- `spawner-backend-linux-x64-<commit>.tar.gz`
- `spawner-frontend-dist-<commit>.tar.gz`
- `spawner-full-linux-x64-<commit>.tar.gz` (recommended)

For Ubuntu Server setup using the prebuilt release packages, see `docs/ubuntu-server-lts.md`.

For existing production installations, install the release updater once:

```bash
sudo install -m 0755 scripts/update-spawner-release.sh /usr/local/bin/spawner-update
```

Then stop Minecraft instances in the UI and run `sudo spawner-update`. This
installs the release for the current `master` commit, preserves local configuration,
and restores the previous application files if startup fails. You can also pass
`commit-<full-sha>` to select a release. See the deployment guide for dependencies,
configuration and backup details. New releases include a standalone
`spawner-update` asset for servers without a checkout.

## Ubuntu Server LTS install / setup

See `docs/ubuntu-server-lts.md`.

## Configuration

Backend settings are loaded from `Spawner/appsettings*.json` and environment variables.

Important production settings:

- `ASPNETCORE_ENVIRONMENT=Production`
- `ASPNETCORE_URLS=http://127.0.0.1:5000`
- `Authentication__Username=<administrator username>`
- `Authentication__PasswordHash=<generated hash>`
- `Authentication__PublicOrigin=https://your-domain.example`
- `Settings__DefaultJavaArgs=-Xms4G -Xmx8G`

Runtime requirement:

- Install the `.NET 10` ASP.NET Core runtime on the host running the backend.

## CI

GitHub Actions workflow: `.github/workflows/ci.yml`

## Prebuilt Releases Per Commit

This repo also includes `.github/workflows/release-per-commit.yml`, which can create a prebuilt GitHub Release for each pushed commit:

- backend publish (`linux-x64`, framework-dependent, .NET 10)
- frontend static build (`dist/`)
- combined package (`backend` + `www`)

Important:

- `Frontend/spawner` must be tracked as a normal folder (not a gitlink/submodule pointer), or the release workflow will fail early with a clear error.
- GitHub Actions must be enabled for the repository, and the workflow file must be committed to the default branch.
