# Spawner Frontend

React + Vite admin UI for the Spawner backend.

## Requirements

- Node.js 26+
- pnpm 12.21.0

## Local development

```bash
pnpm install --frozen-lockfile
pnpm run dev
```

Dev proxy behavior (`vite.config.ts`):

- `/api` -> `http://localhost:5000`
- `/ws` -> `ws://localhost:5000`

## Authentication

The UI and API must share one browser origin. Use the Vite proxy locally and the nginx proxy in production. Configure backend administrator credentials and `Authentication__PublicOrigin=http://localhost:5173` for local development; see the root README. The backend must use HTTPS in production. The former `VITE_API_ORIGIN` and `VITE_WS_ORIGIN` overrides are no longer used.

## Production build

```bash
pnpm install --frozen-lockfile
pnpm run build
```

Output directory: `dist/`
