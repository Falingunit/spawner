# Spawner Frontend — API + WebSocket Specifications

This document defines the REST API and WebSocket event protocol needed to make the UI feel responsive and “instant”, with optimistic updates and realtime reconciliation.

The current UI surfaces:
- Dashboard: list of servers + start/stop + live player counts
- Server page:
  - Server card (status, port, players)
  - Server properties editor (search, refresh, save)
  - Console (send command, live output)
  - Logs tab (planned)
  - Whitelist tab (planned)

---

## Goals

- **Fast first paint**: fetch a small server list quickly, then stream deltas.
- **Optimistic UX**: start/stop, save properties, send console command should update UI immediately with rollback on failure.
- **Console accuracy**: console output must be authoritative (no fabricated “executed” lines). The UI may show a *separate* “sending…” indicator for commands, but the console stream itself only shows server-produced output.
- **Realtime correctness**: authoritative state arrives via WebSocket events to reconcile local optimistic state.
- **Resume after disconnect**: client can reconnect and request missed events to avoid full refresh.
- **Low overhead**: push only what changes; prefer patch/diff events over full snapshots where possible.

---

## Versioning & Base URLs

- REST base: `/api/v1`
- WebSocket: `/ws/v1`
- All payloads are JSON UTF‑8.

### API Version negotiation
- REST: `Accept: application/json; version=1`
- WS: first message includes `protocolVersion: 1`

---

## Auth & Security (recommended)

Any of:
- Cookie session (same-site)
- Bearer token: `Authorization: Bearer <token>`

WebSocket auth:
- Prefer header-based auth if supported.
- Otherwise `wss://<host>/ws/v1?token=<jwt>` (avoid logging tokens in proxies).

Authorization considerations:
- “Read-only” users should not be allowed to start/stop, save properties, whitelist changes, console commands.

---

## Shared Types (Frontend Alignment)

### `ServerStatus`
`"online" | "offline" | "starting" | "stopping"`

### `Server`
Matches `src/types/server.ts`
```json
{
  "id": "string",
  "name": "string",
  "iconUrl": "string",
  "version": "string",
  "type": "string",
  "status": "online|offline|starting|stopping",
  "playersOnline": 0,
  "playersMax": 20,
  "port": 25565,
  "motd": "string"
}
```

### `ServerProperties`
Key-value map based on `src/config/serverPropertiesSchema.ts`.
```json
{
  "motd": "A Minecraft Server",
  "server-port": 25565,
  "max-players": 20,
  "...": "..."
}
```

### Error model
```json
{
  "error": {
    "code": "string",
    "message": "string",
    "details": { }
  }
}
```

---

## REST API

Design principles:
- **ETag + conditional GET** for list/details.
- **Idempotency keys** for start/stop/save to safely retry.
- **Return updated resource** (or at least a “job” state) to reduce extra round trips.

### 1) List servers (fast dashboard load)
`GET /api/v1/servers`

Query params:
- `fields` (optional): `basic|full` (default `basic`)

Response (`200`):
```json
{
  "servers": [ { "id": "...", "name": "...", "status": "online", "playersOnline": 2, "playersMax": 20, "port": 25565, "version": "...", "type": "...", "iconUrl": "...", "motd": "..." } ],
  "serverTime": "2026-02-01T12:34:56.000Z"
}
```

Caching:
- `ETag: "<hash>"`
- Client: `If-None-Match`

### 2) Get one server (direct navigation / details)
`GET /api/v1/servers/{serverId}`

Response (`200`): `{ "server": <Server> }`

### 3) Start server (optimistic)
`POST /api/v1/servers/{serverId}:start`

Headers:
- `Idempotency-Key: <uuid>`

Response (`202`):
```json
{
  "job": { "id": "job_...", "type": "start", "serverId": "...", "state": "queued|running|done|failed" },
  "server": { "id": "...", "status": "starting" }
}
```

### 4) Stop server (optimistic)
`POST /api/v1/servers/{serverId}:stop` (same semantics as start)

### 5) Get server.properties
`GET /api/v1/servers/{serverId}/properties`

Response (`200`):
```json
{
  "properties": { "motd": "...", "server-port": 25565, "max-players": 20 },
  "revision": "rev_...", 
  "serverTime": "2026-02-01T12:34:56.000Z"
}
```

### 6) Save server.properties
`PUT /api/v1/servers/{serverId}/properties`

Headers:
- `If-Match: <revision>` (prevents overwriting concurrent changes)
- `Idempotency-Key: <uuid>`

Body:
```json
{ "properties": { "...": "..." } }
```

Response (`200`):
```json
{
  "properties": { "...": "..." },
  "revision": "rev_next"
}
```

### 7) Send console command
`POST /api/v1/servers/{serverId}/console/commands`

Body:
```json
{ "command": "say hello", "requestId": "uuid" }
```

Response (`202`):
```json
{ "accepted": true, "requestId": "uuid" }
```

Notes:
- `accepted: true` means the command was received/queued, **not** that it executed successfully.
- The **only** source of console output should be the server’s console stream (history + WS events).

### 8) Fetch console history (for initial load)
`GET /api/v1/servers/{serverId}/console/history?limit=300`

Response (`200`):
```json
{ "lines": [ "..." ], "serverTime": "..." }
```

### 9) Logs list + tail (planned UI)
Option A (simple): paged list
- `GET /api/v1/servers/{serverId}/logs?cursor=<opaque>&limit=200`

Option B (recommended): list files + tail endpoint
- `GET /api/v1/servers/{serverId}/logs/files`
- `GET /api/v1/servers/{serverId}/logs/tail?file=latest.log&cursor=<opaque>&limit=200`

### 10) Whitelist (planned UI)
- `GET /api/v1/servers/{serverId}/whitelist`
- `POST /api/v1/servers/{serverId}/whitelist` (add entry)
- `DELETE /api/v1/servers/{serverId}/whitelist/{entryId}`

Whitelist entry:
```json
{ "id": "uuid_or_name", "name": "PlayerName", "uuid": "..." }
```

---

## WebSocket Protocol (Realtime)

### Why WS is needed
The UI should not poll for:
- status transitions (starting/stopping → online/offline)
- player count changes
- console output
- log tail
- cross-client edits to properties/whitelist

### Connection
`GET /ws/v1` upgraded to WebSocket.

Client → server first message:
```json
{
  "type": "hello",
  "protocolVersion": 1,
  "clientId": "uuid",
  "resumeFromEventId": "optional_event_id",
  "subscriptions": [
    { "topic": "servers" }
  ]
}
```

Server → client:
```json
{
  "type": "hello_ack",
  "protocolVersion": 1,
  "sessionId": "sess_...",
  "serverTime": "2026-02-01T12:34:56.000Z",
  "lastEventId": "evt_..."
}
```

### Event envelope
All server-pushed events share:
```json
{
  "type": "event",
  "eventId": "evt_...",
  "topic": "servers|server:<id>:console|server:<id>:properties|...",
  "ts": "2026-02-01T12:34:56.000Z",
  "payload": { }
}
```

### Client command envelope (optional but recommended)
```json
{
  "type": "cmd",
  "requestId": "uuid",
  "name": "server.start|server.stop|properties.save|console.send|...",
  "args": { }
}
```

Server response to commands:
```json
{ "type": "cmd_ack", "requestId": "uuid" }
```
or
```json
{ "type": "cmd_error", "requestId": "uuid", "error": { "code": "...", "message": "..." } }
```

### Topics & subscriptions
Client may subscribe/unsubscribe at runtime:
```json
{ "type": "subscribe", "topics": ["servers", "server:abc:console"] }
{ "type": "unsubscribe", "topics": ["server:abc:console"] }
```

#### Topic list
- `servers` (global list deltas + snapshots)
- `server:{id}` (single server deltas)
- `server:{id}:properties` (properties revision + changes)
- `server:{id}:console` (console stream)
- `server:{id}:logs` (log tail stream)
- `server:{id}:whitelist` (whitelist changes)

### Server events (minimum set)

#### A) Server list snapshot (on subscribe)
Topic: `servers`
```json
{
  "type": "event",
  "eventId": "evt_...",
  "topic": "servers",
  "payload": {
    "kind": "snapshot",
    "servers": [ { "id": "...", "status": "online", "playersOnline": 1, "playersMax": 20, "port": 25565, "name": "...", "iconUrl": "...", "type": "...", "version": "...", "motd": "..." } ]
  }
}
```

#### B) Server patch (high frequency)
Topic: `servers` (or `server:{id}`)
```json
{
  "type": "event",
  "eventId": "evt_...",
  "topic": "servers",
  "payload": {
    "kind": "server.patch",
    "serverId": "...",
    "patch": { "playersOnline": 3, "status": "online" }
  }
}
```

#### C) Status lifecycle (optional sugar)
```json
{ "kind": "server.status", "serverId": "...", "status": "starting|online|stopping|offline", "reason": "..." }
```

#### D) Properties revision update
Topic: `server:{id}:properties`
```json
{
  "kind": "properties.updated",
  "serverId": "...",
  "revision": "rev_next",
  "patch": { "motd": "..." }
}
```

#### E) Console line
Topic: `server:{id}:console`
```json
{ "kind": "console.line", "serverId": "...", "line": "[12:00:00] [INFO]: ...", "level": "info|warn|error" }
```

#### F) Logs tail line
Topic: `server:{id}:logs`
```json
{ "kind": "logs.line", "serverId": "...", "file": "latest.log", "line": "..." }
```

#### G) Whitelist changed
Topic: `server:{id}:whitelist`
```json
{ "kind": "whitelist.changed", "serverId": "...", "added": [ { "id": "...", "name": "...", "uuid": "..." } ], "removed": [ "..." ] }
```

### Heartbeats & latency
- Server sends: `{ "type": "ping", "ts": "..." }` every ~15s
- Client replies: `{ "type": "pong", "ts": "...", "echo": "<ping_ts>" }`
- Client may compute RTT and optionally display “connected” vs “degraded”.

### Resume semantics
- Every event has `eventId` (monotonic per-session or global).
- Client reconnects with `resumeFromEventId`.
- If resume is not possible, server replies with `hello_ack` + `resume: false`, and client should re-fetch REST snapshots.

### Backpressure
Console/logs can be high-volume:
- Server may batch:
```json
{ "kind": "console.batch", "serverId": "...", "lines": ["...", "..."] }
```
- Client should cap memory (e.g., keep last 300–1000 lines).

---

## Responsiveness Rules (Frontend UX)

### Dashboard
- Initial REST `GET /servers` for immediate render.
- WS `servers` subscription for instant player/status changes.
- Start/stop:
  - Optimistically set `status: starting/stopping` immediately.
  - Send REST start/stop (or WS cmd) with idempotency key.
  - Reconcile with `server.patch` events.

### Server Properties
- On open:
  - REST `GET /servers/{id}/properties` for current values + revision.
  - WS subscribe `server:{id}:properties` for cross-client edits.
- Save:
  - Optimistically update local UI.
  - Prefer `PUT` with `If-Match` revision to detect conflicts.
  - On conflict: return `409` with server-side latest revision; UI can show “Reload/Overwrite”.

### Console
- On open:
  - REST `GET /console/history` for immediate content.
  - WS subscribe `server:{id}:console` for live output.
- Send command:
  - Do **not** inject fake console output (including `> cmd`) into the console stream.
  - REST/WS send with `requestId`; show “sending…” / “failed to send” UI state outside the console stream (e.g., near the input).
  - The server may optionally echo commands into the console stream (as real output) and/or emit a correlated event such as `console.commandAck` with the `requestId`.

---

## Implementation Notes (Server-side)

- Prefer **one WS connection per browser**; multiplex topics.
- Emit patches for small updates; emit snapshots only on subscribe or after resume failure.
- Use a stable `serverId` that matches the frontend route `/servers/:id`.
- Consider gzip/permessage-deflate on WS for logs/console.
