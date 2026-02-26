import type { Server } from "@/types/server";
import { mapApiPropertiesToFormState, mapFormStateToApiProperties } from "@/lib/serverPropertiesMapping";
import type { ServerPropertiesState } from "@/lib/serverProperties";
import type { WhitelistEntry } from "@/types/whitelist";

type ApiError = { error?: { code?: string; message?: string } };

async function readJson<T>(res: Response): Promise<T> {
  const text = await res.text();
  if (!text) return {} as T;
  return JSON.parse(text) as T;
}

function apiBase() {
  const explicit = (import.meta.env.VITE_API_ORIGIN as string | undefined) ?? "";
  if (explicit) return explicit;

  // Local-dev convenience: if the frontend is on a different localhost port,
  // default the backend to `:5000` (matches Vite proxy + the default ASP.NET port).
  try {
    const { protocol, hostname, port } = window.location;
    const isLocal = hostname === "localhost" || hostname === "127.0.0.1";
    if (isLocal && protocol.startsWith("http") && port && port !== "5000") {
      return `${protocol}//${hostname}:5000`;
    }
  } catch {
    // ignore (e.g. SSR / non-browser)
  }

  return "";
}

async function apiFetch(path: string, init?: RequestInit) {
  const res = await fetch(`${apiBase()}${path}`, {
    ...init,
    headers: {
      Accept: "application/json",
      ...(init?.headers ?? {}),
    },
  });

  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const body = await readJson<ApiError>(res);
      const m = body?.error?.message;
      if (m) message = m;
    } catch {
      // ignore
    }
    throw new Error(message);
  }

  return res;
}

export async function apiListServers(fields: "basic" | "full" = "basic") {
  const res = await apiFetch(`/api/v1/servers?fields=${encodeURIComponent(fields)}`);
  const data = (await readJson<{ servers: Server[] }>(res)) as { servers: Server[] };
  return data.servers ?? [];
}

export async function apiStartServer(serverId: string) {
  const idem = crypto.randomUUID();
  await apiFetch(`/api/v1/servers/${encodeURIComponent(serverId)}:start`, {
    method: "POST",
    headers: { "Idempotency-Key": idem },
  });
}

export async function apiStopServer(serverId: string) {
  const idem = crypto.randomUUID();
  await apiFetch(`/api/v1/servers/${encodeURIComponent(serverId)}:stop`, {
    method: "POST",
    headers: { "Idempotency-Key": idem },
  });
}

export async function apiForceStopServer(serverId: string) {
  const idem = crypto.randomUUID();
  await apiFetch(`/api/v1/servers/${encodeURIComponent(serverId)}:stop?force=true`, {
    method: "POST",
    headers: { "Idempotency-Key": idem },
  });
}

export async function apiArchiveServer(serverId: string) {
  await apiFetch(`/api/v1/servers/${encodeURIComponent(serverId)}:archive`, { method: "POST" });
}

export async function apiUnarchiveServer(serverId: string) {
  await apiFetch(`/api/v1/servers/${encodeURIComponent(serverId)}:unarchive`, { method: "POST" });
}

export async function apiDeleteServer(serverId: string) {
  await apiFetch(`/api/v1/servers/${encodeURIComponent(serverId)}`, { method: "DELETE" });
}

export async function apiGetConsoleHistory(serverId: string, limit = 300) {
  const res = await apiFetch(
    `/api/v1/servers/${encodeURIComponent(serverId)}/console/history?limit=${encodeURIComponent(
      String(limit),
    )}`,
  );
  const data = await readJson<{ lines: string[] }>(res);
  return data.lines ?? [];
}

export async function apiSendConsoleCommand(serverId: string, command: string, requestId: string) {
  await apiFetch(`/api/v1/servers/${encodeURIComponent(serverId)}/console/commands`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ command, requestId }),
  });
}

export async function apiGetLogsTail(serverId: string, limit = 500) {
  const res = await apiFetch(
    `/api/v1/servers/${encodeURIComponent(serverId)}/logs/tail?limit=${encodeURIComponent(String(limit))}`,
  );
  const data = await readJson<{ lines: string[]; source?: string }>(res);
  return data.lines ?? [];
}

export type ApiLogFile = {
  name: string;
  size: number;
  lastWriteTimeUtc: string;
  compressed: boolean;
};

export async function apiListLogFiles(serverId: string) {
  const res = await apiFetch(`/api/v1/servers/${encodeURIComponent(serverId)}/logs/files`);
  const data = await readJson<{ files: ApiLogFile[] }>(res);
  return data.files ?? [];
}

export async function apiGetLogFileTail(serverId: string, name: string, limit = 500) {
  const res = await apiFetch(
    `/api/v1/servers/${encodeURIComponent(serverId)}/logs/file/${encodeURIComponent(
      name,
    )}/tail?limit=${encodeURIComponent(String(limit))}`,
  );
  const data = await readJson<{ lines: string[] }>(res);
  return data.lines ?? [];
}

export type ApiPropertiesResponse = {
  properties: Record<string, unknown>;
  revision: string;
};

export async function apiGetProperties(serverId: string) {
  const res = await fetch(`${apiBase()}/api/v1/servers/${encodeURIComponent(serverId)}/properties`, {
    headers: { Accept: "application/json" },
  });

  if (res.status === 404) {
    return { state: mapApiPropertiesToFormState({}), revision: "", missing: true as const };
  }

  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const body = await readJson<ApiError>(res);
      const m = body?.error?.message;
      if (m) message = m;
    } catch {
      // ignore
    }
    throw new Error(message);
  }

  const data = await readJson<ApiPropertiesResponse>(res);
  const state = mapApiPropertiesToFormState(data.properties ?? {});
  return { state, revision: data.revision, missing: false as const };
}

export async function apiSaveProperties(
  serverId: string,
  revision: string,
  state: ServerPropertiesState,
) {
  const idem = crypto.randomUUID();
  const body = mapFormStateToApiProperties(state);

  const res = await apiFetch(`/api/v1/servers/${encodeURIComponent(serverId)}/properties`, {
    method: "PUT",
    headers: {
      "Content-Type": "application/json",
      "If-Match": revision,
      "Idempotency-Key": idem,
    },
    body: JSON.stringify(body),
  });

  const data = await readJson<ApiPropertiesResponse>(res);
  const nextState = mapApiPropertiesToFormState(data.properties ?? {});
  return { state: nextState, revision: data.revision };
}

export type ApiWhitelistResponse = {
  entries: WhitelistEntry[];
  revision: string;
};

export async function apiGetWhitelist(serverId: string) {
  const res = await apiFetch(`/api/v1/servers/${encodeURIComponent(serverId)}/whitelist`);
  const data = await readJson<ApiWhitelistResponse>(res);
  return { entries: data.entries ?? [], revision: data.revision };
}

export async function apiSaveWhitelist(serverId: string, revision: string, entries: WhitelistEntry[]) {
  const res = await apiFetch(`/api/v1/servers/${encodeURIComponent(serverId)}/whitelist`, {
    method: "PUT",
    headers: {
      "Content-Type": "application/json",
      "If-Match": revision,
    },
    body: JSON.stringify({ entries }),
  });

  const data = await readJson<ApiWhitelistResponse>(res);
  return { entries: data.entries ?? [], revision: data.revision };
}

export type ApiResolvePlayerResponse = { name: string; uuid: string };

export async function apiResolvePlayer(args: { name?: string; uuid?: string }) {
  const qs = new URLSearchParams();
  if (args.name) qs.set("name", args.name);
  if (args.uuid) qs.set("uuid", args.uuid);

  const res = await apiFetch(`/api/v1/players/resolve?${qs.toString()}`);
  return readJson<ApiResolvePlayerResponse>(res);
}

export type LaunchSettings = {
  javaPath: string;
  javaArgs: string;
  serverJarName: string;
};

export type ApiLaunchSettingsResponse = { settings: LaunchSettings; revision: string };

export async function apiGetLaunchSettings(serverId: string) {
  const res = await apiFetch(`/api/v1/servers/${encodeURIComponent(serverId)}/launch-settings`);
  const data = await readJson<ApiLaunchSettingsResponse>(res);
  return { settings: data.settings, revision: data.revision };
}

export async function apiSaveLaunchSettings(serverId: string, revision: string, settings: LaunchSettings) {
  const res = await apiFetch(`/api/v1/servers/${encodeURIComponent(serverId)}/launch-settings`, {
    method: "PUT",
    headers: {
      "Content-Type": "application/json",
      "If-Match": revision,
    },
    body: JSON.stringify(settings),
  });

  const data = await readJson<ApiLaunchSettingsResponse>(res);
  return { settings: data.settings, revision: data.revision };
}

export type ApiInstanceType = { id: string; label: string; implemented: boolean };

export async function apiListInstanceTypes() {
  const res = await apiFetch(`/api/v1/instance-types`);
  const data = await readJson<{ types: ApiInstanceType[] }>(res);
  return data.types ?? [];
}

export type ApiMinecraftVersion = { id: string; type: "release" | "snapshot" | string; releaseTimeUtc: string };
export type ApiMinecraftVersionsResponse = {
  latest: { release: string; snapshot: string };
  versions: ApiMinecraftVersion[];
};

export async function apiListMinecraftVersions() {
  const res = await apiFetch(`/api/v1/minecraft/versions`);
  return readJson<ApiMinecraftVersionsResponse>(res);
}

type FabricMetaError = { error?: string; message?: string };

async function fabricFetch(path: string, init?: RequestInit) {
  const res = await fetch(`https://meta.fabricmc.net${path}`, {
    ...init,
    headers: {
      Accept: "application/json",
      ...(init?.headers ?? {}),
    },
  });

  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const body = await readJson<FabricMetaError>(res);
      if (body?.error) message = body.error;
      else if (body?.message) message = body.message;
    } catch {
      // ignore
    }
    throw new Error(message);
  }

  return res;
}

export type ApiFabricGameVersion = {
  version: string;
  stable: boolean;
};

export async function apiListFabricGameVersions() {
  const res = await fabricFetch(`/v2/versions/game`);
  return readJson<ApiFabricGameVersion[]>(res);
}

export type ApiFabricLoaderVersion = {
  version: string;
};

export async function apiListFabricLoaderVersions(gameVersion: string, init?: RequestInit) {
  const res = await fabricFetch(`/v2/versions/loader/${encodeURIComponent(gameVersion)}`, init);
  const data = await readJson<Array<{ loader?: { version?: string } }>>(res);
  return (data ?? [])
    .map((x) => x?.loader?.version?.trim() ?? "")
    .filter((x): x is string => x.length > 0)
    .map((version) => ({ version }));
}

export async function apiCreateServer(args: { name: string; type: string; version: string; fabricLoaderVersion?: string }) {
  const res = await apiFetch(`/api/v1/servers`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(args),
  });
  const data = await readJson<{ server: Server }>(res);
  return data.server;
}

export async function apiImportServerFromPath(args: { name: string; version: string; serverJarPath: string }) {
  const res = await apiFetch(`/api/v1/servers:import-path`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(args),
  });
  const data = await readJson<{ server: Server }>(res);
  return data.server;
}

export async function apiSetInstanceType(serverId: string, type: string) {
  await apiFetch(`/api/v1/servers/${encodeURIComponent(serverId)}:set-type`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ type }),
  });
}

export async function apiRenameInstance(serverId: string, name: string) {
  const res = await apiFetch(`/api/v1/servers/${encodeURIComponent(serverId)}:rename`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ name }),
  });
  const data = await readJson<{ name: string }>(res);
  return data.name;
}

export async function apiDetectMinecraftVersionFromJarPath(serverJarPath: string) {
  const res = await apiFetch(`/api/v1/minecraft:detect-version`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ serverJarPath }),
  });
  const data = await readJson<{ detected: boolean; version?: string; candidates?: string[] }>(res);
  return data;
}

export type ApiModrinthSearchHit = {
  projectId: string;
  slug: string;
  title: string;
  author: string;
  description: string;
  projectType: string;
  iconUrl: string;
  categories: string[];
  versions: string[];
  clientSide: string;
  serverSide: string;
  downloads: number;
  dateModifiedUtc?: string | null;
};

export async function apiModrinthSearch(args: {
  query?: string;
  projectType?: "mod" | "modpack" | "resourcepack" | string;
  category?: string;
  loader?: string;
  mcVersion?: string;
  offset?: number;
  limit?: number;
}) {
  const qs = new URLSearchParams();
  if (args.query) qs.set("query", args.query);
  if (args.projectType) qs.set("projectType", args.projectType);
  if (args.category) qs.set("category", args.category);
  if (args.loader) qs.set("loader", args.loader);
  if (args.mcVersion) qs.set("mcVersion", args.mcVersion);
  if (typeof args.offset === "number") qs.set("offset", String(args.offset));
  if (typeof args.limit === "number") qs.set("limit", String(args.limit));
  const res = await apiFetch(`/api/v1/modrinth/search?${qs.toString()}`);
  return readJson<{ totalHits: number; offset: number; limit: number; hits: ApiModrinthSearchHit[] }>(res);
}

export type ApiModrinthProjectVersion = {
  id: string;
  projectId: string;
  name: string;
  versionNumber: string;
  versionType: string;
  status: string;
  featured: boolean;
  loaders: string[];
  gameVersions: string[];
  files: Array<{
    filename: string;
    url: string;
    size: number;
    primary: boolean;
    fileType?: string | null;
    hashes: Record<string, string>;
  }>;
};

export async function apiModrinthProjectVersions(idOrSlug: string, args?: { loader?: string; mcVersion?: string }) {
  const qs = new URLSearchParams();
  if (args?.loader) qs.set("loader", args.loader);
  if (args?.mcVersion) qs.set("mcVersion", args.mcVersion);
  qs.set("includeChangelog", "false");
  const res = await apiFetch(`/api/v1/modrinth/projects/${encodeURIComponent(idOrSlug)}/versions?${qs.toString()}`);
  const data = await readJson<{ versions: ApiModrinthProjectVersion[] }>(res);
  return data.versions ?? [];
}

export type ApiInstalledMod = {
  fileName: string;
  enabled: boolean;
  size: number;
  lastWriteTimeUtc: string;
  sha1?: string | null;
  displayName: string;
  iconUrl?: string | null;
  projectId?: string | null;
  projectSlug?: string | null;
  versionId?: string | null;
  versionNumber?: string | null;
  isManual: boolean;
  update: {
    available: boolean;
    versionId?: string | null;
    versionNumber?: string | null;
  };
};

export async function apiListMods(serverId: string) {
  const res = await apiFetch(`/api/v1/servers/${encodeURIComponent(serverId)}/mods`);
  return readJson<{ items: ApiInstalledMod[]; modrinthError?: string | null }>(res);
}

export async function apiInstallModrinthVersion(serverId: string, versionId: string) {
  const res = await apiFetch(`/api/v1/servers/${encodeURIComponent(serverId)}/mods:install-modrinth-version`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ versionId }),
  });
  return readJson<{ installed: { fileName: string; enabled: boolean; versionId: string; projectId: string; versionNumber: string; removedFileNames?: string[] } }>(res);
}

async function apiModsFileAction(serverId: string, action: "enable" | "disable" | "remove" | "update", fileName: string) {
  const res = await apiFetch(`/api/v1/servers/${encodeURIComponent(serverId)}/mods:${action}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ fileName }),
  });
  return readJson<unknown>(res);
}

export const apiEnableMod = (serverId: string, fileName: string) => apiModsFileAction(serverId, "enable", fileName);
export const apiDisableMod = (serverId: string, fileName: string) => apiModsFileAction(serverId, "disable", fileName);
export const apiRemoveMod = (serverId: string, fileName: string) => apiModsFileAction(serverId, "remove", fileName);
export const apiUpdateMod = (serverId: string, fileName: string) => apiModsFileAction(serverId, "update", fileName);

export async function apiImportMrpack(serverId: string, file: File) {
  const form = new FormData();
  form.append("file", file, file.name || "modpack.mrpack");
  const res = await fetch(`${apiBase()}/api/v1/servers/${encodeURIComponent(serverId)}/mods:import-mrpack`, {
    method: "POST",
    headers: { Accept: "application/json" },
    body: form,
  });
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const body = await readJson<ApiError>(res);
      if (body?.error?.message) message = body.error.message;
    } catch {
      // ignore
    }
    throw new Error(message);
  }
  return readJson<{ imported: { name: string; versionId: string; downloadedFiles: number; overrideFiles: number } }>(res);
}

export function apiMrpackExportUrl(serverId: string) {
  return `${apiBase()}/api/v1/servers/${encodeURIComponent(serverId)}/mods:export-mrpack`;
}

export type ApiContentKind = "resourcepacks" | "datapacks";

export type ApiInstalledContentItem = {
  fileName: string;
  size: number;
  lastWriteTimeUtc: string;
  isDirectory: boolean;
  displayName: string;
  iconUrl?: string | null;
  projectId?: string | null;
  projectSlug?: string | null;
  versionId?: string | null;
  versionNumber?: string | null;
  isManual: boolean;
};

export async function apiListContent(serverId: string, kind: ApiContentKind) {
  const res = await apiFetch(`/api/v1/servers/${encodeURIComponent(serverId)}/content/${encodeURIComponent(kind)}`);
  return readJson<{ items: ApiInstalledContentItem[]; modrinthError?: string | null }>(res);
}

export async function apiInstallModrinthContentVersion(serverId: string, kind: ApiContentKind, versionId: string) {
  const res = await apiFetch(`/api/v1/servers/${encodeURIComponent(serverId)}/content:install-modrinth-version`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ kind, versionId }),
  });
  return readJson<{ installed: { kind: string; fileName: string } }>(res);
}

export async function apiRemoveContent(serverId: string, kind: ApiContentKind, fileName: string) {
  const res = await apiFetch(`/api/v1/servers/${encodeURIComponent(serverId)}/content:remove`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ kind, fileName }),
  });
  return readJson<{ removed: boolean; fileName: string }>(res);
}
