# Spawner Frontend

React + Vite admin UI for the Spawner backend.

## Requirements

- Node.js 20+
- npm 10+

## Local development

```bash
npm ci
npm run dev
```

Dev proxy behavior (`vite.config.ts`):

- `/api` -> `http://localhost:5000`
- `/ws` -> `ws://localhost:5000`

## Environment variables

Create `.env` (optional) from `.env.example`:

```bash
cp .env.example .env
```

Supported values:

- `VITE_API_ORIGIN` (example: `https://api.example.com`)
- `VITE_WS_ORIGIN` (example: `wss://api.example.com`)

If unset, the app uses same-origin by default and has localhost convenience fallbacks for dev.

## Production build

```bash
npm ci
npm run build
```

Output directory: `dist/`
