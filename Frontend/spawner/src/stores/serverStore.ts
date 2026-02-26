import { create } from "zustand";

import type { Server } from "@/types/server";
import type { ServerPropertiesState } from "@/lib/serverProperties";
import {
  apiGetConsoleHistory,
  apiGetLogsTail,
  apiGetLaunchSettings,
  apiGetProperties,
  apiListServers,
  apiSaveLaunchSettings,
  apiSaveProperties,
  apiGetWhitelist,
  apiSaveWhitelist,
  apiSendConsoleCommand,
  apiStartServer,
  apiStopServer,
  apiForceStopServer,
  apiArchiveServer,
  apiUnarchiveServer,
  apiDeleteServer,
} from "@/lib/api";
import { RealtimeClient } from "@/lib/realtimeClient";
import { mapApiPropertiesToFormState } from "@/lib/serverPropertiesMapping";
import type { WhitelistEntry } from "@/types/whitelist";
import type { LaunchSettings } from "@/lib/api";

export type RealtimeStatus = "disconnected" | "connected";

type PropertiesEntry = { state: ServerPropertiesState; revision: string };
type WhitelistState = { entries: WhitelistEntry[]; revision: string };
type LaunchEntry = { settings: LaunchSettings; revision: string };

type ServerStoreState = {
  loaded: boolean;
  error: string | null;
  realtimeStatus: RealtimeStatus;
  servers: Server[];

  consoleById: Record<string, string[]>;
  consoleLoadingById: Record<string, boolean>;

  logsById: Record<string, string[]>;
  logsLoadingById: Record<string, boolean>;

  propertiesById: Record<string, PropertiesEntry | undefined>;
  propertiesLoadingById: Record<string, boolean>;
  propertiesMissingById: Record<string, boolean>;

  launchById: Record<string, LaunchEntry | undefined>;
  launchLoadingById: Record<string, boolean>;

  whitelistById: Record<string, WhitelistState | undefined>;
  whitelistLoadingById: Record<string, boolean>;

  init: () => Promise<void>;
  refreshServers: () => Promise<void>;
  connectRealtime: () => void;
  disconnectRealtime: () => void;

  updateServer: (id: string, patch: Partial<Server> | Server) => void;
  bumpServerIcon: (id: string) => void;
  toggleServer: (id: string) => Promise<void>;
  forceStopServer: (id: string) => Promise<void>;
  archiveServer: (id: string) => Promise<void>;
  unarchiveServer: (id: string) => Promise<void>;
  deleteServer: (id: string) => Promise<void>;

  loadConsole: (id: string) => Promise<void>;
  subscribeConsole: (id: string) => void;
  unsubscribeConsole: (id: string) => void;
  clearConsole: (id: string) => void;
  sendConsoleCommand: (id: string, command: string) => Promise<void>;

  loadLogs: (id: string) => Promise<void>;
  subscribeLogs: (id: string) => void;
  unsubscribeLogs: (id: string) => void;
  clearLogs: (id: string) => void;

  loadProperties: (id: string) => Promise<void>;
  saveProperties: (id: string, state: ServerPropertiesState) => Promise<void>;

  loadLaunchSettings: (id: string) => Promise<void>;
  saveLaunchSettings: (id: string, settings: LaunchSettings) => Promise<void>;

  loadWhitelist: (id: string) => Promise<void>;
  saveWhitelist: (id: string, entries: WhitelistEntry[]) => Promise<void>;
  subscribeWhitelist: (id: string) => void;
  unsubscribeWhitelist: (id: string) => void;
};

let realtime: RealtimeClient | null = null;

type PollState = { attempts: number; timer: number | null };
const statusPolls = new Map<string, PollState>();

function clearStatusPoll(id: string) {
  const p = statusPolls.get(id);
  if (!p) return;
  if (p.timer != null) window.clearTimeout(p.timer);
  statusPolls.delete(id);
}

function ensureStatusPoll(id: string, refreshServers: () => Promise<void>, getServer: () => Server | undefined) {
  if (typeof window === "undefined") return;
  if (statusPolls.has(id)) return;

  const state: PollState = { attempts: 0, timer: null };
  statusPolls.set(id, state);

  const tick = async () => {
    state.attempts++;

    try {
      await refreshServers();
    } catch {
      // ignore; we'll retry
    }

    const s = getServer();
    const busy = s?.status === "starting" || s?.status === "stopping";

    // Stop polling once we're not in a transitional state, or after ~90s.
    if (!busy || state.attempts >= 45) {
      clearStatusPoll(id);
      return;
    }

    // 0.5s, 0.5s, 1s, 1s, 2s, 2s, 4s... (max 4s)
    const step = Math.min(4_000, 500 * 2 ** Math.floor(state.attempts / 2));
    state.timer = window.setTimeout(() => void tick(), step);
  };

  state.timer = window.setTimeout(() => void tick(), 500);
}

function withIconFallback(s: Server): Server {
  // Backend currently returns `/api/v1/servers/{id}/icon`; if not implemented, UI should still look OK.
  if (!s.iconUrl) return { ...s, iconUrl: "/spawner.png" };
  return s;
}

function applyPatch<T extends object>(target: T, patch: unknown): T {
  if (!patch || typeof patch !== "object") return target;
  return { ...target, ...(patch as Partial<T>) };
}

function upsertServers(current: Server[], next: Server[]) {
  const byId = new Map(current.map((s) => [s.id, s] as const));
  for (const s of next) byId.set(s.id, withIconFallback(s));
  return Array.from(byId.values());
}

function handleServersSnapshot(set: (fn: (s: ServerStoreState) => Partial<ServerStoreState>) => void, servers: unknown) {
  if (!Array.isArray(servers)) return;
  set((state) => ({
    servers: upsertServers(
      state.servers,
      servers.filter((x): x is Server => typeof x === "object" && x != null && "id" in x) as Server[],
    ),
    loaded: true,
    error: null,
  }));
}

function handleServerPatch(set: (fn: (s: ServerStoreState) => Partial<ServerStoreState>) => void, serverId: unknown, patch: unknown) {
  if (typeof serverId !== "string") return;
  set((state) => ({
    servers: state.servers.map((s) => (s.id === serverId ? withIconFallback(applyPatch(s, patch)) : s)),
  }));
}

function handleConsoleLine(
  set: (fn: (s: ServerStoreState) => Partial<ServerStoreState>) => void,
  serverId: unknown,
  line: unknown,
) {
  if (typeof serverId !== "string" || typeof line !== "string") return;
  set((state) => ({
    consoleById: {
      ...state.consoleById,
      [serverId]: [...(state.consoleById[serverId] ?? []), line].slice(-500),
    },
  }));
}

function handleLogLine(
  set: (fn: (s: ServerStoreState) => Partial<ServerStoreState>) => void,
  serverId: unknown,
  line: unknown,
) {
  if (typeof serverId !== "string" || typeof line !== "string") return;
  set((state) => ({
    logsById: {
      ...state.logsById,
      [serverId]: [...(state.logsById[serverId] ?? []), line].slice(-2000),
    },
  }));
}

export const useServerStore = create<ServerStoreState>((set, get) => ({
  loaded: false,
  error: null,
  realtimeStatus: "disconnected",
  servers: [],

  consoleById: {},
  consoleLoadingById: {},

  logsById: {},
  logsLoadingById: {},

  propertiesById: {},
  propertiesLoadingById: {},
  propertiesMissingById: {},

  launchById: {},
  launchLoadingById: {},

  whitelistById: {},
  whitelistLoadingById: {},

  init: async () => {
    if (get().loaded) return;
    try {
      const servers = await apiListServers("basic");
      set({ servers: servers.map(withIconFallback), loaded: true, error: null });
    } catch (e) {
      set({ loaded: true, error: e instanceof Error ? e.message : "Failed to load servers" });
    }
  },

  refreshServers: async () => {
    try {
      const servers = await apiListServers("basic");
      set({ servers: servers.map(withIconFallback), loaded: true, error: null });
    } catch (e) {
      set({ error: e instanceof Error ? e.message : "Failed to refresh servers" });
    }
  },

  connectRealtime: () => {
    if (realtime) return;

    realtime = new RealtimeClient("/ws/v1");
    realtime.onStatus = (status) => set({ realtimeStatus: status });
    realtime.onEnvelope = (env) => {
      if (env.type !== "event") return;

      const payload = env.payload as { kind?: string; [k: string]: unknown };
      if (!payload || typeof payload !== "object") return;

      if (payload.kind === "snapshot") {
        handleServersSnapshot(set, (payload as { servers?: unknown }).servers);
        return;
      }

      if (payload.kind === "server.patch") {
        const { serverId, patch } = payload as { serverId?: unknown; patch?: unknown };
        handleServerPatch(set, serverId, patch);
        return;
      }

      if (payload.kind === "console.line") {
        const { serverId, line } = payload as { serverId?: unknown; line?: unknown };
        handleConsoleLine(set, serverId, line);
        return;
      }

      if (payload.kind === "log.line") {
        const { serverId, line } = payload as { serverId?: unknown; line?: unknown };
        handleLogLine(set, serverId, line);
        return;
      }

      if (payload.kind === "properties.updated") {
        const { serverId, patch, revision } = payload as {
          serverId?: unknown;
          patch?: unknown;
          revision?: unknown;
        };
        if (typeof serverId !== "string" || typeof revision !== "string") return;
        set((state) => {
          const prev = state.propertiesById[serverId];
          if (!prev) return {};
          const parsedPatch = mapApiPropertiesToFormState(
            patch && typeof patch === "object" ? (patch as Record<string, unknown>) : {},
          );
          return {
            propertiesById: {
              ...state.propertiesById,
              [serverId]: {
                revision,
                state: applyPatch(prev.state, parsedPatch) as ServerPropertiesState,
              },
            },
          };
        });
      }

      if (payload.kind === "whitelist.updated") {
        const { serverId, entries, revision } = payload as {
          serverId?: unknown;
          entries?: unknown;
          revision?: unknown;
        };
        if (typeof serverId !== "string" || typeof revision !== "string") return;
        if (!Array.isArray(entries)) return;
        set((state) => ({
          whitelistById: {
            ...state.whitelistById,
            [serverId]: { entries: entries as WhitelistEntry[], revision },
          },
        }));
      }
    };

    // Keep servers always subscribed; other topics are subscribed per-page.
    realtime.subscribe(["servers"]);
    void realtime.connect();
  },

  disconnectRealtime: () => {
    realtime?.close();
    realtime = null;
    set({ realtimeStatus: "disconnected" });
  },

  updateServer: (id, patch) =>
    set((state) => ({
      servers: state.servers.map((s) => (s.id === id ? withIconFallback({ ...s, ...patch }) : s)),
    })),

  bumpServerIcon: (id) => {
    const v = Date.now();
    set((state) => ({
      servers: state.servers.map((s) =>
        s.id === id ? { ...s, iconUrl: `/api/v1/servers/${encodeURIComponent(id)}/icon?v=${v}` } : s,
      ),
    }));
  },

  toggleServer: async (id) => {
    const current = get().servers.find((s) => s.id === id);
    if (!current) return;

    if ((current as { archived?: boolean }).archived) {
      set({ error: "Server is archived." });
      return;
    }

    try {
      if (current.status === "online") {
        get().updateServer(id, { status: "stopping" });
        await apiStopServer(id);
        ensureStatusPoll(id, get().refreshServers, () => get().servers.find((s) => s.id === id));
        return;
      }

      if (current.status === "offline") {
        get().clearConsole(id);
        get().updateServer(id, { status: "starting" });
        await apiStartServer(id);
        ensureStatusPoll(id, get().refreshServers, () => get().servers.find((s) => s.id === id));
      }

      if (current.status === "downloading") {
        set({ error: "Server is still downloading/initializing." });
        return;
      }
    } catch (e) {
      // Restore the most conservative state; WS will reconcile if needed.
      get().updateServer(id, { status: current.status });
      set({ error: e instanceof Error ? e.message : "Failed to toggle server" });
    }
  },

  forceStopServer: async (id) => {
    const current = get().servers.find((s) => s.id === id);
    if (!current) return;
    try {
      get().updateServer(id, { status: "stopping" });
      await apiForceStopServer(id);
      ensureStatusPoll(id, get().refreshServers, () => get().servers.find((s) => s.id === id));
    } catch (e) {
      get().updateServer(id, { status: current.status });
      set({ error: e instanceof Error ? e.message : "Failed to force stop server" });
      throw e;
    }
  },

  archiveServer: async (id) => {
    try {
      await apiArchiveServer(id);
      await get().refreshServers();
    } catch (e) {
      set({ error: e instanceof Error ? e.message : "Failed to archive server" });
      throw e;
    }
  },

  unarchiveServer: async (id) => {
    try {
      await apiUnarchiveServer(id);
      await get().refreshServers();
    } catch (e) {
      set({ error: e instanceof Error ? e.message : "Failed to restore server" });
      throw e;
    }
  },

  deleteServer: async (id) => {
    try {
      await apiDeleteServer(id);
      await get().refreshServers();
    } catch (e) {
      set({ error: e instanceof Error ? e.message : "Failed to delete server" });
      throw e;
    }
  },

  loadConsole: async (id) => {
    set((s) => ({ consoleLoadingById: { ...s.consoleLoadingById, [id]: true } }));
    try {
      const lines = await apiGetConsoleHistory(id, 500);
      set((s) => ({
        consoleById: { ...s.consoleById, [id]: lines },
        consoleLoadingById: { ...s.consoleLoadingById, [id]: false },
      }));
    } catch (e) {
      set((s) => ({ consoleLoadingById: { ...s.consoleLoadingById, [id]: false } }));
      set({ error: e instanceof Error ? e.message : "Failed to load console" });
    }
  },

  subscribeConsole: (id) => {
    realtime?.subscribe([`server:${id}:console`]);
  },

  unsubscribeConsole: (id) => {
    realtime?.unsubscribe([`server:${id}:console`]);
  },

  clearConsole: (id) =>
    set((s) => ({
      consoleById: { ...s.consoleById, [id]: [] },
    })),

  sendConsoleCommand: async (id, command) => {
    const cmd = command.trim();
    if (!cmd) return;
    try {
      await apiSendConsoleCommand(id, cmd, crypto.randomUUID());
    } catch (e) {
      set({ error: e instanceof Error ? e.message : "Failed to send command" });
      throw e;
    }
  },

  loadLogs: async (id) => {
    set((s) => ({ logsLoadingById: { ...s.logsLoadingById, [id]: true } }));
    try {
      const lines = await apiGetLogsTail(id, 1500);
      set((s) => ({
        logsById: { ...s.logsById, [id]: lines },
        logsLoadingById: { ...s.logsLoadingById, [id]: false },
      }));
    } catch (e) {
      set((s) => ({ logsLoadingById: { ...s.logsLoadingById, [id]: false } }));
      set({ error: e instanceof Error ? e.message : "Failed to load logs" });
    }
  },

  subscribeLogs: (id) => {
    realtime?.subscribe([`server:${id}:logs`]);
  },

  unsubscribeLogs: (id) => {
    realtime?.unsubscribe([`server:${id}:logs`]);
  },

  clearLogs: (id) =>
    set((s) => ({
      logsById: { ...s.logsById, [id]: [] },
    })),

  loadProperties: async (id) => {
    set((s) => ({ propertiesLoadingById: { ...s.propertiesLoadingById, [id]: true } }));
    try {
      const { state, revision, missing } = await apiGetProperties(id);
      set((s) => ({
        propertiesById: { ...s.propertiesById, [id]: { state, revision } },
        propertiesLoadingById: { ...s.propertiesLoadingById, [id]: false },
        propertiesMissingById: { ...s.propertiesMissingById, [id]: Boolean(missing) },
      }));
    } catch (e) {
      set((s) => ({ propertiesLoadingById: { ...s.propertiesLoadingById, [id]: false } }));
      set({ error: e instanceof Error ? e.message : "Failed to load properties" });
    }
  },

  saveProperties: async (id, state) => {
    const current = get().propertiesById[id];
    if (get().propertiesMissingById[id]) throw new Error("server.properties not found");
    if (!current) throw new Error("Properties not loaded");

    try {
      const { state: nextState, revision } = await apiSaveProperties(id, current.revision, state);
      set((s) => ({
        propertiesById: { ...s.propertiesById, [id]: { state: nextState, revision } },
      }));
    } catch (e) {
      set({ error: e instanceof Error ? e.message : "Failed to save properties" });
      throw e;
    }
  },

  loadLaunchSettings: async (id) => {
    set((s) => ({ launchLoadingById: { ...s.launchLoadingById, [id]: true } }));
    try {
      const { settings, revision } = await apiGetLaunchSettings(id);
      set((s) => ({
        launchById: { ...s.launchById, [id]: { settings, revision } },
        launchLoadingById: { ...s.launchLoadingById, [id]: false },
      }));
    } catch (e) {
      set((s) => ({ launchLoadingById: { ...s.launchLoadingById, [id]: false } }));
      set({ error: e instanceof Error ? e.message : "Failed to load launch settings" });
    }
  },

  saveLaunchSettings: async (id, settings) => {
    const current = get().launchById[id];
    if (!current) throw new Error("Launch settings not loaded");

    try {
      const { settings: nextSettings, revision } = await apiSaveLaunchSettings(id, current.revision, settings);
      set((s) => ({
        launchById: { ...s.launchById, [id]: { settings: nextSettings, revision } },
      }));
    } catch (e) {
      set({ error: e instanceof Error ? e.message : "Failed to save launch settings" });
      throw e;
    }
  },

  loadWhitelist: async (id) => {
    set((s) => ({ whitelistLoadingById: { ...s.whitelistLoadingById, [id]: true } }));
    try {
      const { entries, revision } = await apiGetWhitelist(id);
      set((s) => ({
        whitelistById: { ...s.whitelistById, [id]: { entries, revision } },
        whitelistLoadingById: { ...s.whitelistLoadingById, [id]: false },
      }));
    } catch (e) {
      set((s) => ({ whitelistLoadingById: { ...s.whitelistLoadingById, [id]: false } }));
      set({ error: e instanceof Error ? e.message : "Failed to load whitelist" });
    }
  },

  saveWhitelist: async (id, entries) => {
    const current = get().whitelistById[id];
    if (!current) throw new Error("Whitelist not loaded");

    try {
      const { entries: nextEntries, revision } = await apiSaveWhitelist(id, current.revision, entries);
      set((s) => ({
        whitelistById: { ...s.whitelistById, [id]: { entries: nextEntries, revision } },
      }));
    } catch (e) {
      set({ error: e instanceof Error ? e.message : "Failed to save whitelist" });
      throw e;
    }
  },

  subscribeWhitelist: (id) => {
    realtime?.subscribe([`server:${id}:whitelist`]);
  },

  unsubscribeWhitelist: (id) => {
    realtime?.unsubscribe([`server:${id}:whitelist`]);
  },
}));
