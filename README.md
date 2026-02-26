# Spawner

Spawner is a Minecraft server instance manager with:

- `Spawner/`: ASP.NET Core backend API + websocket server
- `Frontend/spawner/`: React + Vite admin UI
- `Spawner.Tests/`: backend unit tests
- `docs/`: deployment and integration notes

## Production-ready changes in this repo

- Backend/test targets aligned to `.NET 10`
- Production CORS behavior defaults to same-origin unless `Cors:AllowedOrigins` is configured
- Frontend route-level lazy loading to reduce initial bundle load
- CI workflow for backend tests + frontend production build
- Ubuntu Server LTS deployment guide (systemd + nginx)

## Local development

### Backend

```bash
dotnet run --project Spawner
```

Notes:
- Development CORS is permissive for local UI work.
- The frontend Vite config proxies `/api` and `/ws` to `http://localhost:5000` in dev.

### Frontend

```bash
cd Frontend/spawner
npm ci
npm run dev
```

Optional frontend env variables (`Frontend/spawner/.env`):

- `VITE_API_ORIGIN=http://localhost:5000`
- `VITE_WS_ORIGIN=ws://localhost:5000`

## Build for production

### Backend publish

```bash
dotnet publish Spawner/Spawner.csproj -c Release -o .tmp-build/backend
```

### Frontend build

```bash
cd Frontend/spawner
npm ci
npm run build
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

## Ubuntu Server LTS install / setup

See `docs/ubuntu-server-lts.md`.

## Configuration

Backend settings are loaded from `Spawner/appsettings*.json` and environment variables.

Important production settings:

- `ASPNETCORE_ENVIRONMENT=Production`
- `ASPNETCORE_URLS=http://127.0.0.1:5000`
- `Cors__AllowedOrigins__0=https://your-domain.example` (if frontend is hosted on a different origin)
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
