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
