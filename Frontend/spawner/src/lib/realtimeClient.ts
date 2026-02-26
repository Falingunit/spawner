export type RealtimeEnvelope =
  | {
      type: "hello_ack";
      protocolVersion: number;
      sessionId?: string;
      serverTime?: string;
      lastEventId?: string;
      resume?: boolean;
    }
  | { type: "ping"; ts?: string }
  | { type: "cmd_ack"; requestId: string }
  | { type: "cmd_error"; requestId: string; error?: { code?: string; message?: string } }
  | {
      type: "event";
      eventId: string;
      topic: string;
      ts?: string;
      payload: unknown;
    };

type HelloMsg = {
  type: "hello";
  protocolVersion: 1;
  clientId?: string;
  resumeFromEventId?: string;
  subscriptions?: { topic: string }[];
};

type SubscribeMsg = { type: "subscribe"; topics: string[] };
type UnsubscribeMsg = { type: "unsubscribe"; topics: string[] };
type PongMsg = { type: "pong"; ts?: string };

function wsBase() {
  const explicit = (import.meta.env.VITE_WS_ORIGIN as string | undefined) ?? "";
  if (explicit) return explicit;

  const apiOrigin = (import.meta.env.VITE_API_ORIGIN as string | undefined) ?? "";
  if (apiOrigin) {
    if (apiOrigin.startsWith("https://")) return "wss://" + apiOrigin.slice("https://".length);
    if (apiOrigin.startsWith("http://")) return "ws://" + apiOrigin.slice("http://".length);
    return apiOrigin;
  }

  // Local-dev convenience: if the frontend is on a different localhost port,
  // default the backend websocket to `:5000`.
  try {
    const { protocol, hostname, port } = window.location;
    const isLocal = hostname === "localhost" || hostname === "127.0.0.1";
    if (isLocal && port && port !== "5000") {
      const proto = protocol === "https:" ? "wss:" : "ws:";
      return `${proto}//${hostname}:5000`;
    }
  } catch {
    // ignore
  }

  const proto = window.location.protocol === "https:" ? "wss:" : "ws:";
  return `${proto}//${window.location.host}`;
}

function sleep(ms: number) {
  return new Promise<void>((resolve) => window.setTimeout(resolve, ms));
}

export class RealtimeClient {
  private readonly path: string;
  private ws: WebSocket | null = null;
  private desiredTopics = new Set<string>();
  private queue: string[] = [];
  private closed = false;

  private reconnectAttempt = 0;
  private lastEventId: string | null = null;

  onEnvelope?: (env: RealtimeEnvelope) => void;
  onStatus?: (status: "connected" | "disconnected") => void;

  constructor(path = "/ws/v1") {
    this.path = path;
  }

  setLastEventId(eventId: string) {
    this.lastEventId = eventId;
  }

  getLastEventId() {
    return this.lastEventId;
  }

  subscribe(topics: string[]) {
    for (const t of topics) this.desiredTopics.add(t);
    this.send({ type: "subscribe", topics } as SubscribeMsg);
  }

  unsubscribe(topics: string[]) {
    for (const t of topics) this.desiredTopics.delete(t);
    this.send({ type: "unsubscribe", topics } as UnsubscribeMsg);
  }

  async connect() {
    this.closed = false;
    await this.connectLoop();
  }

  close() {
    this.closed = true;
    try {
      this.ws?.close();
    } catch {
      // ignore
    }
    this.ws = null;
    this.onStatus?.("disconnected");
  }

  private async connectLoop() {
    while (!this.closed) {
      try {
        await this.openOnce();
        return;
      } catch {
        this.onStatus?.("disconnected");
        this.reconnectAttempt++;
        const backoff = Math.min(8000, 500 * 2 ** Math.min(4, this.reconnectAttempt));
        await sleep(backoff);
      }
    }
  }

  private async openOnce() {
    const url = `${wsBase()}${this.path}`;
    const ws = new WebSocket(url);
    this.ws = ws;

    await new Promise<void>((resolve, reject) => {
      ws.onopen = () => resolve();
      ws.onerror = () => reject(new Error("ws connect failed"));
    });

    const clientIdKey = "spawner_ws_client_id";
    const clientId = localStorage.getItem(clientIdKey) ?? crypto.randomUUID();
    localStorage.setItem(clientIdKey, clientId);

    const hello: HelloMsg = {
      type: "hello",
      protocolVersion: 1,
      clientId,
      resumeFromEventId: this.lastEventId ?? undefined,
      subscriptions: Array.from(this.desiredTopics).map((topic) => ({ topic })),
    };
    ws.send(JSON.stringify(hello));

    ws.onmessage = (ev) => {
      try {
        const env = JSON.parse(String(ev.data)) as RealtimeEnvelope;
        if (env.type === "ping") {
          this.send({ type: "pong", ts: env.ts } as PongMsg);
          return;
        }
        if (env.type === "event") this.lastEventId = env.eventId;
        this.onEnvelope?.(env);
      } catch {
        // ignore invalid payloads
      }
    };

    ws.onclose = () => {
      this.onStatus?.("disconnected");
      this.ws = null;
      if (!this.closed) void this.connectLoop();
    };

    ws.onerror = () => {
      // let onclose handle reconnect
    };

    this.onStatus?.("connected");
    this.reconnectAttempt = 0;
    this.flushQueue();
  }

  private send(obj: object) {
    const json = JSON.stringify(obj);
    if (this.ws?.readyState === WebSocket.OPEN) {
      this.ws.send(json);
      return;
    }
    this.queue.push(json);
  }

  private flushQueue() {
    if (this.ws?.readyState !== WebSocket.OPEN) return;
    while (this.queue.length) {
      const msg = this.queue.shift();
      if (msg) this.ws.send(msg);
    }
  }
}
