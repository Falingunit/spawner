import * as React from "react";
import { useParams } from "react-router-dom";

import { useServerStore } from "@/stores/serverStore";
import { createDefaultProperties, type ServerPropertiesState } from "@/lib/serverProperties";
import { ServerPropertiesForm } from "@/spawner-components/ServerPropertiesForm";
import MinecraftServerCard from "@/spawner-components/InstanceInfoCard";
import type { WhitelistEntry } from "@/types/whitelist";

import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import { FileExplorer } from "@/spawner-components/FileExplorer";
import { ContentTab } from "@/spawner-components/ContentTab";
import { Switch } from "@/components/ui/switch";
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";

import { RefreshCw, Search, Loader2, Save, Settings, ServerCog, ListChecks, SquareTerminal, Logs, Folder, Upload, Pencil, Check, X, Boxes, RotateCw } from "lucide-react";
import { apiGetLogFileTail, apiListLogFiles, apiListInstanceTypes, apiResolvePlayer, apiSetInstanceType, apiRenameInstance, type ApiInstanceType, type ApiLogFile } from "@/lib/api";
import type { LaunchSettings } from "@/lib/api";
import { Trash2 } from "lucide-react";
import { apiBaseUrlForBrowser } from "@/spawner-components/fileApiHelpers";

const EMPTY_LINES: string[] = [];

function normalizeUuidInput(raw: string) {
  return raw.trim().replace(/-/g, "").toLowerCase();
}

function isValidUuid32(raw: string) {
  const s = normalizeUuidInput(raw);
  return s.length === 32 && /^[0-9a-f]+$/.test(s);
}

function isValidUsername(raw: string) {
  return /^[A-Za-z0-9_]{3,16}$/.test(raw.trim());
}

function toBool(v: unknown) {
  if (typeof v === "boolean") return v;
  if (typeof v === "string") return v.trim().toLowerCase() === "true";
  if (typeof v === "number") return v !== 0;
  return false;
}

type IconCropRect = { x: number; y: number; size: number };

function getRotatedIconDims(width: number, height: number, rotation: number) {
  const r = ((rotation % 360) + 360) % 360;
  return r === 90 || r === 270 ? { width: height, height: width } : { width, height };
}

function clampIconCrop(crop: IconCropRect, width: number, height: number): IconCropRect {
  const maxSize = Math.max(1, Math.min(width, height));
  const size = Math.min(Math.max(1, Math.round(crop.size)), maxSize);
  const x = Math.min(Math.max(0, Math.round(crop.x)), Math.max(0, width - size));
  const y = Math.min(Math.max(0, Math.round(crop.y)), Math.max(0, height - size));
  return { x, y, size };
}

function defaultCenteredIconCrop(width: number, height: number): IconCropRect {
  const size = Math.max(1, Math.min(width, height));
  return {
    size,
    x: Math.max(0, Math.floor((width - size) / 2)),
    y: Math.max(0, Math.floor((height - size) / 2)),
  };
}

function renderRotatedImageCanvas(img: HTMLImageElement, rotation: number) {
  const normalized = ((rotation % 360) + 360) % 360;
  const { width: rw, height: rh } = getRotatedIconDims(img.naturalWidth, img.naturalHeight, normalized);
  const canvas = document.createElement("canvas");
  canvas.width = rw;
  canvas.height = rh;
  const ctx = canvas.getContext("2d");
  if (!ctx) throw new Error("Canvas 2D is not available.");

  ctx.save();
  if (normalized === 0) {
    ctx.drawImage(img, 0, 0);
  } else if (normalized === 90) {
    ctx.translate(rw, 0);
    ctx.rotate(Math.PI / 2);
    ctx.drawImage(img, 0, 0);
  } else if (normalized === 180) {
    ctx.translate(rw, rh);
    ctx.rotate(Math.PI);
    ctx.drawImage(img, 0, 0);
  } else if (normalized === 270) {
    ctx.translate(0, rh);
    ctx.rotate(-Math.PI / 2);
    ctx.drawImage(img, 0, 0);
  }
  ctx.restore();
  return canvas;
}

export default function ServerPage() {
  const { id } = useParams<{ id: string }>();
  const loaded = useServerStore((s) => s.loaded);
  const storeError = useServerStore((s) => s.error);
  const servers = useServerStore((s) => s.servers);
  const toggleServer = useServerStore((s) => s.toggleServer);
  const forceStopServer = useServerStore((s) => s.forceStopServer);
  const archiveServer = useServerStore((s) => s.archiveServer);
  const deleteServer = useServerStore((s) => s.deleteServer);
  const bumpServerIcon = useServerStore((s) => s.bumpServerIcon);
  const updateServer = useServerStore((s) => s.updateServer);
  const loadProperties = useServerStore((s) => s.loadProperties);
  const savePropertiesAction = useServerStore((s) => s.saveProperties);
  const loadConsole = useServerStore((s) => s.loadConsole);
  const subscribeConsole = useServerStore((s) => s.subscribeConsole);
  const unsubscribeConsole = useServerStore((s) => s.unsubscribeConsole);
  const sendConsoleCommandAction = useServerStore((s) => s.sendConsoleCommand);
  const loadLogs = useServerStore((s) => s.loadLogs);
  const subscribeLogs = useServerStore((s) => s.subscribeLogs);
  const unsubscribeLogs = useServerStore((s) => s.unsubscribeLogs);
  const loadWhitelist = useServerStore((s) => s.loadWhitelist);
  const saveWhitelistAction = useServerStore((s) => s.saveWhitelist);
  const subscribeWhitelist = useServerStore((s) => s.subscribeWhitelist);
  const unsubscribeWhitelist = useServerStore((s) => s.unsubscribeWhitelist);
  const loadLaunchSettings = useServerStore((s) => s.loadLaunchSettings);
  const saveLaunchSettingsAction = useServerStore((s) => s.saveLaunchSettings);

  const server = React.useMemo(
    () => servers.find((s) => s.id === id),
    [servers, id],
  );

  const serverId = server?.id ?? "";

  const storedProperties = useServerStore(
    React.useCallback(
      (s) => (serverId ? s.propertiesById[serverId] : undefined),
      [serverId],
    ),
  );

  const consoleLines = useServerStore(
    React.useCallback(
      (s) => (serverId ? (s.consoleById[serverId] ?? EMPTY_LINES) : EMPTY_LINES),
      [serverId],
    ),
  );

  const consoleLoading = useServerStore(
    React.useCallback((s) => (serverId ? Boolean(s.consoleLoadingById[serverId]) : false), [serverId]),
  );

  const logsLines = useServerStore(
    React.useCallback((s) => (serverId ? (s.logsById[serverId] ?? EMPTY_LINES) : EMPTY_LINES), [serverId]),
  );

  const logsLoading = useServerStore(
    React.useCallback((s) => (serverId ? Boolean(s.logsLoadingById[serverId]) : false), [serverId]),
  );

  const propertiesLoading = useServerStore(
    React.useCallback((s) => (serverId ? Boolean(s.propertiesLoadingById[serverId]) : false), [serverId]),
  );

  const propertiesMissing = useServerStore(
    React.useCallback((s) => (serverId ? Boolean(s.propertiesMissingById[serverId]) : false), [serverId]),
  );

  const storedWhitelist = useServerStore(
    React.useCallback((s) => (serverId ? s.whitelistById[serverId] : undefined), [serverId]),
  );

  const whitelistLoading = useServerStore(
    React.useCallback((s) => (serverId ? Boolean(s.whitelistLoadingById[serverId]) : false), [serverId]),
  );

  const storedLaunch = useServerStore(
    React.useCallback((s) => (serverId ? s.launchById[serverId] : undefined), [serverId]),
  );

  const launchLoading = useServerStore(
    React.useCallback((s) => (serverId ? Boolean(s.launchLoadingById[serverId]) : false), [serverId]),
  );

  const [consoleInput, setConsoleInput] = React.useState("");
  const [properties, setProperties] = React.useState<ServerPropertiesState>(() => createDefaultProperties());
  const [propertiesQuery, setPropertiesQuery] = React.useState("");
  const [propsSaving, setPropsSaving] = React.useState(false);
  const [propsError, setPropsError] = React.useState<string | null>(null);
  const [cmdSending, setCmdSending] = React.useState(false);
  const [cmdError, setCmdError] = React.useState<string | null>(null);
  const [deleteOpen, setDeleteOpen] = React.useState(false);
  const [deleteMode, setDeleteMode] = React.useState<"menu" | "confirmDelete">("menu");

  const [iconOpen, setIconOpen] = React.useState(false);
  const [iconFile, setIconFile] = React.useState<File | null>(null);
  const [iconSaving, setIconSaving] = React.useState(false);
  const [iconError, setIconError] = React.useState<string | null>(null);
  const [iconSourcePreviewUrl, setIconSourcePreviewUrl] = React.useState<string | null>(null);
  const [iconWorkspacePreviewUrl, setIconWorkspacePreviewUrl] = React.useState<string | null>(null);
  const [iconOutputPreviewUrl, setIconOutputPreviewUrl] = React.useState<string | null>(null);
  const [iconImageEl, setIconImageEl] = React.useState<HTMLImageElement | null>(null);
  const [iconRotation, setIconRotation] = React.useState(0);
  const [iconCrop, setIconCrop] = React.useState<IconCropRect>({ x: 0, y: 0, size: 64 });

  const [instanceTypes, setInstanceTypes] = React.useState<ApiInstanceType[]>([]);
  const [instanceTypeSaving, setInstanceTypeSaving] = React.useState(false);
  const [instanceTypeError, setInstanceTypeError] = React.useState<string | null>(null);

  const [logsQuery, setLogsQuery] = React.useState("");
  const [logFiles, setLogFiles] = React.useState<ApiLogFile[]>([]);
  const [logsSource, setLogsSource] = React.useState<"live" | "file">("live");
  const [selectedLogFile, setSelectedLogFile] = React.useState<string>("latest.log");
  const [logFileLines, setLogFileLines] = React.useState<string[]>(EMPTY_LINES);
  const [logFileLoading, setLogFileLoading] = React.useState(false);
  const [logFileError, setLogFileError] = React.useState<string | null>(null);
  type WhitelistRow = { key: string; entry: WhitelistEntry };
  const [whitelistRows, setWhitelistRows] = React.useState<WhitelistRow[]>([]);
  const [wlSaving, setWlSaving] = React.useState(false);
  const [wlError, setWlError] = React.useState<string | null>(null);
  const [wlEnabledSaving, setWlEnabledSaving] = React.useState(false);
  const [wlEnabledError, setWlEnabledError] = React.useState<string | null>(null);
  const [wlResolvingByKey, setWlResolvingByKey] = React.useState<Record<string, boolean>>({});
  const [wlResolveErrorByKey, setWlResolveErrorByKey] = React.useState<Record<string, string | null>>({});

  const [launch, setLaunch] = React.useState<LaunchSettings>(() => ({
    javaPath: "",
    javaArgs: "",
    serverJarName: "server.jar",
  }));
  const [launchSaving, setLaunchSaving] = React.useState(false);
  const [launchError, setLaunchError] = React.useState<string | null>(null);
  const [renamingName, setRenamingName] = React.useState(false);
  const [renameNameValue, setRenameNameValue] = React.useState("");
  const [renameNameSaving, setRenameNameSaving] = React.useState(false);
  const [renameNameError, setRenameNameError] = React.useState<string | null>(null);

  React.useEffect(() => {
    if (!serverId) return;
    let cancelled = false;
    (async () => {
      try {
        const t = await apiListInstanceTypes();
        if (!cancelled) setInstanceTypes(t);
      } catch {
        // ignore; we can still show basic values
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [serverId]);

  const wlTimersRef = React.useRef<Map<string, number>>(new Map());
  const wlTokensRef = React.useRef<Map<string, number>>(new Map());
  const cmdHistoryRef = React.useRef<string[]>([]);
  const cmdHistoryIdxRef = React.useRef(-1);
  const cmdDraftRef = React.useRef("");

  const filteredLogs = React.useMemo(() => {
    const q = logsQuery.trim().toLowerCase();
    const base = logsSource === "live" ? logsLines : logFileLines;
    if (!q) return base;
    return base.filter((l) => l.toLowerCase().includes(q));
  }, [logFileLines, logsLines, logsQuery, logsSource]);

  const refreshLogFileTail = React.useCallback(
    async (name: string) => {
      if (!name) return;
      setLogFileError(null);
      setLogFileLoading(true);
      try {
        const lines = await apiGetLogFileTail(serverId, name, 2000);
        setLogFileLines(lines);
      } catch (e) {
        setLogFileLines(EMPTY_LINES);
        setLogFileError(e instanceof Error ? e.message : "Failed to load log file");
      } finally {
        setLogFileLoading(false);
      }
    },
    [serverId],
  );

  React.useEffect(() => {
    if (!serverId) return;
    void loadProperties(serverId);
    void loadConsole(serverId);
    void loadLogs(serverId);
    void loadWhitelist(serverId);
    void loadLaunchSettings(serverId);
    subscribeConsole(serverId);
    subscribeLogs(serverId);
    subscribeWhitelist(serverId);
    return () => {
      unsubscribeConsole(serverId);
      unsubscribeLogs(serverId);
      unsubscribeWhitelist(serverId);
    };
  }, [
    loadConsole,
    loadLogs,
    loadLaunchSettings,
    loadProperties,
    loadWhitelist,
    serverId,
    subscribeConsole,
    subscribeLogs,
    subscribeWhitelist,
    unsubscribeConsole,
    unsubscribeLogs,
    unsubscribeWhitelist,
  ]);

  React.useEffect(() => {
    // Console command history should not leak between servers.
    cmdHistoryRef.current = [];
    cmdHistoryIdxRef.current = -1;
    cmdDraftRef.current = "";
  }, [serverId]);

  React.useEffect(() => {
    if (!storedProperties) return;
    setProperties((prev) => {
      // If user has no local edits (heuristic), keep in sync with server.
      // We treat "prev equals defaults" as not-yet-loaded.
      if (Object.keys(prev).length === 0) return storedProperties.state;
      return storedProperties.state;
    });
  }, [storedProperties]);

  React.useEffect(() => {
    if (!storedWhitelist) return;
    const entries = storedWhitelist.entries ?? [];
    setWhitelistRows(
      entries.map((e) => ({
        key: (e.uuid ?? "").trim() || (e.name ?? "").trim() || crypto.randomUUID(),
        entry: { uuid: e.uuid ?? "", name: e.name ?? "" },
      })),
    );
  }, [storedWhitelist]);

  React.useEffect(() => {
    if (!storedLaunch) return;
    setLaunch(storedLaunch.settings);
  }, [storedLaunch]);

  React.useEffect(() => {
    if (!iconFile) {
      setIconImageEl(null);
      setIconSourcePreviewUrl((prev) => {
        if (prev) URL.revokeObjectURL(prev);
        return null;
      });
      setIconWorkspacePreviewUrl(null);
      setIconOutputPreviewUrl(null);
      setIconRotation(0);
      setIconCrop({ x: 0, y: 0, size: 64 });
      return;
    }

    const url = URL.createObjectURL(iconFile);
    setIconSourcePreviewUrl((prev) => {
      if (prev) URL.revokeObjectURL(prev);
      return url;
    });

    const img = new Image();
    img.onload = () => {
      setIconImageEl(img);
      setIconRotation(0);
      setIconCrop(defaultCenteredIconCrop(img.naturalWidth, img.naturalHeight));
    };
    img.onerror = () => {
      setIconImageEl(null);
      setIconError("Failed to read image");
    };
    img.src = url;

    return () => {
      URL.revokeObjectURL(url);
    };
  }, [iconFile]);

  const iconRotatedDims = React.useMemo(() => {
    if (!iconImageEl) return null;
    return getRotatedIconDims(iconImageEl.naturalWidth, iconImageEl.naturalHeight, iconRotation);
  }, [iconImageEl, iconRotation]);

  React.useEffect(() => {
    if (!iconRotatedDims) return;
    setIconCrop((prev) => clampIconCrop(prev, iconRotatedDims.width, iconRotatedDims.height));
  }, [iconRotatedDims]);

  React.useEffect(() => {
    if (!iconImageEl || !iconRotatedDims) {
      setIconWorkspacePreviewUrl(null);
      setIconOutputPreviewUrl(null);
      return;
    }

    try {
      const rotatedCanvas = renderRotatedImageCanvas(iconImageEl, iconRotation);
      const safeCrop = clampIconCrop(iconCrop, rotatedCanvas.width, rotatedCanvas.height);

      const out = document.createElement("canvas");
      out.width = 64;
      out.height = 64;
      const outCtx = out.getContext("2d");
      if (!outCtx) throw new Error("Canvas 2D is not available.");
      outCtx.imageSmoothingEnabled = true;
      outCtx.clearRect(0, 0, 64, 64);
      outCtx.drawImage(rotatedCanvas, safeCrop.x, safeCrop.y, safeCrop.size, safeCrop.size, 0, 0, 64, 64);

      setIconWorkspacePreviewUrl(rotatedCanvas.toDataURL("image/png"));
      setIconOutputPreviewUrl(out.toDataURL("image/png"));
    } catch (e) {
      setIconWorkspacePreviewUrl(null);
      setIconOutputPreviewUrl(null);
      setIconError(e instanceof Error ? e.message : "Failed to render icon preview");
    }
  }, [iconCrop, iconImageEl, iconRotatedDims, iconRotation]);

  React.useEffect(() => {
    setRenameNameValue(server?.name ?? "");
    setRenameNameError(null);
    setRenamingName(false);
    setRenameNameSaving(false);
  }, [server?.id, server?.name]);

  React.useEffect(() => {
    const timers = wlTimersRef.current;
    const tokens = wlTokensRef.current;
    return () => {
      for (const h of timers.values()) window.clearTimeout(h);
      timers.clear();
      tokens.clear();
    };
  }, []);

  React.useEffect(() => {
    if (!serverId) return;
    let cancelled = false;

    (async () => {
      try {
        const files = await apiListLogFiles(serverId);
        if (cancelled) return;
        setLogFiles(files);

        const hasLatest = files.some((f) => f.name.toLowerCase() === "latest.log");
        if (hasLatest) {
          setSelectedLogFile("latest.log");
          setLogsSource("file");
          setLogFileError(null);
        } else {
          setLogsSource("live");
        }
      } catch {
        if (cancelled) return;
        setLogFiles([]);
        setLogsSource("live");
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [serverId]);

  React.useEffect(() => {
    if (!serverId) return;
    if (logsSource !== "file") return;
    if (!selectedLogFile) return;
    void refreshLogFileTail(selectedLogFile);
  }, [refreshLogFileTail, logsSource, selectedLogFile, serverId]);

  if (!loaded) {
    return (
      <div className="mx-auto w-full max-w-screen-2xl px-6 py-6">
        <div className="text-sm text-muted-foreground">Loading server…</div>
      </div>
    );
  }

  if (!server) {
    return (
      <div className="mx-auto w-full max-w-screen-2xl px-6 py-6">
        {storeError ? (
          <div className="mb-3 rounded-md border border-border bg-muted/30 p-3 text-sm">
            Failed to load servers: <span className="font-mono">{storeError}</span>
          </div>
        ) : null}
        <div className="text-sm text-muted-foreground">Server not found.</div>
      </div>
    );
  }

  const consoleDisabled = server.status === "offline";

  async function refreshProperties() {
    setPropsError(null);
    try {
      await loadProperties(serverId);
      const next = useServerStore.getState().propertiesById[serverId];
      if (next) setProperties(next.state);
    } catch (e) {
      setPropsError(e instanceof Error ? e.message : "Failed to refresh properties");
    }
  }

  async function saveProperties() {
    if (propsSaving) return;
    setPropsSaving(true);
    setPropsError(null);
    try {
      await savePropertiesAction(serverId, properties);
      const next = useServerStore.getState().propertiesById[serverId];
      if (next) setProperties(next.state);
    } catch (e) {
      setPropsError(e instanceof Error ? e.message : "Failed to save properties");
    } finally {
      setPropsSaving(false);
    }
  }

  async function sendConsoleCommand() {
    if (consoleDisabled) return;
    const cmd = consoleInput.trim();
    if (!cmd) return;
    setCmdError(null);
    setCmdSending(true);
    try {
      const history = cmdHistoryRef.current;
      if (history.length === 0 || history[history.length - 1] !== cmd) history.push(cmd);
      cmdHistoryIdxRef.current = -1;
      cmdDraftRef.current = "";

      await sendConsoleCommandAction(serverId, cmd);
      setConsoleInput("");
    } catch (e) {
      setCmdError(e instanceof Error ? e.message : "Failed to send command");
    } finally {
      setCmdSending(false);
    }
  }

  async function refreshLogs() {
    if (logsSource === "live") {
      await loadLogs(serverId);
      return;
    }

    await refreshLogFileTail(selectedLogFile);
  }

  function addWhitelistRow() {
    setWlError(null);
    const key = crypto.randomUUID();
    setWhitelistRows((prev) => [...prev, { key, entry: { name: "", uuid: "" } }]);
  }

  function removeWhitelistRow(idx: number) {
    setWlError(null);
    setWhitelistRows((prev) => {
      const row = prev[idx];
      if (row) {
        const t = wlTimersRef.current.get(row.key);
        if (t != null) window.clearTimeout(t);
        wlTimersRef.current.delete(row.key);
      }
      return prev.filter((_, i) => i !== idx);
    });
  }

  async function saveWhitelist() {
    if (wlSaving) return;
    setWlSaving(true);
    setWlError(null);
    try {
      const entries = whitelistRows.map((r) => ({
        name: (r.entry.name ?? "").trim() || null,
        uuid: (r.entry.uuid ?? "").trim() || null,
      }));
      await saveWhitelistAction(serverId, entries);
      const next = useServerStore.getState().whitelistById[serverId];
      if (next) {
        const loadedEntries = next.entries ?? [];
        setWhitelistRows(
          loadedEntries.map((e) => ({
            key: (e.uuid ?? "").trim() || (e.name ?? "").trim() || crypto.randomUUID(),
            entry: { uuid: e.uuid ?? "", name: e.name ?? "" },
          })),
        );
      }
    } catch (e) {
      setWlError(e instanceof Error ? e.message : "Failed to save whitelist");
    } finally {
      setWlSaving(false);
    }
  }

  const whitelistEnabled = toBool(properties["white-list"]);

  async function setWhitelistEnabled(next: boolean) {
    if (wlEnabledSaving) return;
    const prev = whitelistEnabled;

    setWlEnabledSaving(true);
    setWlEnabledError(null);
    const nextProps = { ...properties, ["white-list"]: next };
    setProperties(nextProps);

    try {
      await savePropertiesAction(serverId, nextProps);
      const saved = useServerStore.getState().propertiesById[serverId];
      if (saved) setProperties(saved.state);
    } catch (e) {
      setProperties((p) => ({ ...p, ["white-list"]: prev }));
      setWlEnabledError(e instanceof Error ? e.message : "Failed to update whitelist setting");
    } finally {
      setWlEnabledSaving(false);
    }
  }

  async function saveLaunchSettings() {
    if (launchSaving) return;
    setLaunchSaving(true);
    setLaunchError(null);
    try {
      await saveLaunchSettingsAction(serverId, launch);
      const next = useServerStore.getState().launchById[serverId];
      if (next) setLaunch(next.settings);
    } catch (e) {
      setLaunchError(e instanceof Error ? e.message : "Failed to save launch settings");
    } finally {
      setLaunchSaving(false);
    }
  }

  async function doArchive() {
    try {
      await archiveServer(serverId);
      setDeleteOpen(false);
      setDeleteMode("menu");
    } catch {
      // store error shows
    }
  }

  async function doDeleteConfirmed() {
    try {
      await deleteServer(serverId);
      setDeleteOpen(false);
      setDeleteMode("menu");
    } catch {
      // store error shows
    }
  }

  async function uploadServerIcon(file: Blob) {
    // 1) delete previous backup, 2) move current icon to .old (best-effort), 3) upload new icon as server-icon.png
    const base = apiBaseUrlForBrowser();

    const delOld = await fetch(
      `${base}/api/v1/servers/${encodeURIComponent(serverId)}/files?${new URLSearchParams({ path: "server-icon.png.old" }).toString()}`,
      { method: "DELETE" },
    );
    if (!delOld.ok && delOld.status !== 404) {
      const t = await delOld.text();
      throw new Error(t || `${delOld.status} ${delOld.statusText}`);
    }

    const move = await fetch(`${base}/api/v1/servers/${encodeURIComponent(serverId)}/files/move`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Accept: "application/json" },
      body: JSON.stringify({ src: "server-icon.png", dst: "server-icon.png.old" }),
    });
    if (!move.ok && move.status !== 404) {
      // Move endpoint returns 404 if no existing icon; ignore that.
      let msg = "";
      try {
        const json = (await move.json()) as { error?: { message?: string } };
        msg = json.error?.message ?? "";
      } catch {
        // ignore
      }
      if (!msg) msg = await move.text();
      throw new Error(msg || `${move.status} ${move.statusText}`);
    }

    const form = new FormData();
    form.append("files", file, "server-icon.png");
    const upload = await fetch(`${base}/api/v1/servers/${encodeURIComponent(serverId)}/files/upload`, { method: "POST", body: form });
    if (!upload.ok) {
      const t = await upload.text();
      throw new Error(t || `${upload.status} ${upload.statusText}`);
    }
  }

  async function doSaveIcon() {
    if (!serverId) return;
    if (!iconImageEl) return;
    setIconError(null);
    setIconSaving(true);
    try {
      const rotatedCanvas = renderRotatedImageCanvas(iconImageEl, iconRotation);
      const safeCrop = clampIconCrop(iconCrop, rotatedCanvas.width, rotatedCanvas.height);
      const out = document.createElement("canvas");
      out.width = 64;
      out.height = 64;
      const outCtx = out.getContext("2d");
      if (!outCtx) throw new Error("Canvas 2D is not available.");
      outCtx.clearRect(0, 0, 64, 64);
      outCtx.drawImage(rotatedCanvas, safeCrop.x, safeCrop.y, safeCrop.size, safeCrop.size, 0, 0, 64, 64);
      const blob = await new Promise<Blob>((resolve, reject) => {
        out.toBlob((b) => (b ? resolve(b) : reject(new Error("Failed to encode icon PNG"))), "image/png");
      });

      await uploadServerIcon(blob);
      bumpServerIcon(serverId);
      setIconOpen(false);
      setIconFile(null);
    } catch (e) {
      setIconError(e instanceof Error ? e.message : "Failed to upload icon");
    } finally {
      setIconSaving(false);
    }
  }

  async function saveInstanceType(nextType: string) {
    if (!serverId) return;
    if (instanceTypeSaving) return;
    setInstanceTypeSaving(true);
    setInstanceTypeError(null);
    try {
      await apiSetInstanceType(serverId, nextType);
      updateServer(serverId, { type: nextType });
    } catch (e) {
      setInstanceTypeError(e instanceof Error ? e.message : "Failed to update instance type");
    } finally {
      setInstanceTypeSaving(false);
    }
  }

  async function saveInstanceName() {
    if (!serverId) return;
    if (!server) return;
    if (renameNameSaving) return;

    const nextName = renameNameValue.trim();
    if (!nextName) {
      setRenameNameError("Name is required");
      return;
    }
    if (nextName === server.name) {
      setRenamingName(false);
      setRenameNameError(null);
      return;
    }

    setRenameNameSaving(true);
    setRenameNameError(null);
    try {
      const savedName = await apiRenameInstance(serverId, nextName);
      updateServer(serverId, { name: savedName });
      setRenameNameValue(savedName);
      setRenamingName(false);
    } catch (e) {
      setRenameNameError(e instanceof Error ? e.message : "Failed to rename instance");
    } finally {
      setRenameNameSaving(false);
    }
  }

  function scheduleWhitelistResolve(rowKey: string, kind: "name" | "uuid", value: string) {
    const prev = wlTimersRef.current.get(rowKey);
    if (prev != null) window.clearTimeout(prev);

    const token = (wlTokensRef.current.get(rowKey) ?? 0) + 1;
    wlTokensRef.current.set(rowKey, token);

    setWlResolvingByKey((m) => ({ ...m, [rowKey]: true }));
    setWlResolveErrorByKey((m) => ({ ...m, [rowKey]: null }));

    const handle = window.setTimeout(async () => {
      try {
        const res =
          kind === "name"
            ? await apiResolvePlayer({ name: value.trim() })
            : await apiResolvePlayer({ uuid: value.trim() });

        // Ignore stale responses.
        if ((wlTokensRef.current.get(rowKey) ?? 0) !== token) return;

        setWhitelistRows((prevRows) =>
          prevRows.map((r) => {
            if (r.key !== rowKey) return r;

            const nextName = res.name ?? r.entry.name ?? "";
            const nextUuid = res.uuid ?? r.entry.uuid ?? "";
            return { ...r, entry: { ...r.entry, name: nextName, uuid: nextUuid } };
          }),
        );
      } catch (e) {
        if ((wlTokensRef.current.get(rowKey) ?? 0) !== token) return;
        setWlResolveErrorByKey((m) => ({
          ...m,
          [rowKey]: e instanceof Error ? e.message : "Failed to resolve player",
        }));
      } finally {
        if ((wlTokensRef.current.get(rowKey) ?? 0) === token) {
          setWlResolvingByKey((m) => ({ ...m, [rowKey]: false }));
        }
      }
    }, 600);

    wlTimersRef.current.set(rowKey, handle);
  }

  return (
    <div className="mx-auto w-full max-w-screen-2xl px-6 py-6">
      {storeError ? (
        <div className="mb-4 rounded-md border border-border bg-muted/30 p-3 text-sm">
          <span className="font-medium">Error:</span> <span className="font-mono">{storeError}</span>
        </div>
      ) : null}
      <div className="min-w-0">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="min-w-0">
            {renamingName ? (
              <div className="flex flex-wrap items-center gap-2">
                <Input
                  value={renameNameValue}
                  onChange={(e) => setRenameNameValue(e.target.value)}
                  className="h-9 w-[min(420px,75vw)]"
                  disabled={renameNameSaving}
                  spellCheck={false}
                  autoCorrect="off"
                  autoCapitalize="off"
                  onKeyDown={(e) => {
                    if (e.key === "Enter") {
                      e.preventDefault();
                      void saveInstanceName();
                    }
                    if (e.key === "Escape") {
                      e.preventDefault();
                      setRenameNameValue(server.name);
                      setRenameNameError(null);
                      setRenamingName(false);
                    }
                  }}
                />
                <Button size="sm" onClick={() => void saveInstanceName()} disabled={renameNameSaving}>
                  {renameNameSaving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Check className="h-4 w-4" />}
                </Button>
                <Button
                  size="sm"
                  variant="secondary"
                  onClick={() => {
                    setRenameNameValue(server.name);
                    setRenameNameError(null);
                    setRenamingName(false);
                  }}
                  disabled={renameNameSaving}
                >
                  <X className="h-4 w-4" />
                </Button>
              </div>
            ) : (
              <div className="flex items-center gap-2">
                <h1 className="truncate text-3xl font-semibold">{server.name}</h1>
                <Button
                  type="button"
                  size="icon"
                  variant="ghost"
                  className="h-8 w-8 shrink-0"
                  onClick={() => {
                    setRenameNameValue(server.name);
                    setRenameNameError(null);
                    setRenamingName(true);
                  }}
                  title="Edit instance name"
                  aria-label="Edit instance name"
                >
                  <Pencil className="h-4 w-4" />
                </Button>
              </div>
            )}
            {renameNameError ? (
              <div className="mt-2 text-sm text-destructive">{renameNameError}</div>
            ) : null}
          </div>
          <Button
            variant="destructive"
            onClick={() => {
              setDeleteMode("menu");
              setDeleteOpen(true);
            }}
            disabled={server.status !== "offline"}
            title={server.status !== "offline" ? "Stop the server before deleting/archiving" : "Archive or delete"}
          >
            <Trash2 className="mr-2 h-4 w-4" />
            Delete
          </Button>
        </div>
      </div>
      <Dialog
        open={deleteOpen}
        onOpenChange={(open) => {
          setDeleteOpen(open);
          if (!open) setDeleteMode("menu");
        }}
      >
        <DialogContent className="w-[min(520px,95vw)] max-w-[95vw]">
          <DialogHeader>
            <DialogTitle>{deleteMode === "confirmDelete" ? "Delete instance" : "Remove instance"}</DialogTitle>
          </DialogHeader>

          {deleteMode === "confirmDelete" ? (
            <>
              <div className="text-sm text-muted-foreground">
                Permanently delete <span className="font-mono">{server.name}</span>? This removes the instance folder.
              </div>

              <DialogFooter className="gap-2 sm:gap-0">
                <Button variant="secondary" onClick={() => setDeleteMode("menu")}>
                  Back
                </Button>
                <Button variant="destructive" onClick={() => void doDeleteConfirmed()}>
                  <Trash2 className="mr-2 h-4 w-4" />
                  Delete
                </Button>
              </DialogFooter>
            </>
          ) : (
            <div className="grid gap-3">
              <Button variant="secondary" onClick={() => void doArchive()}>
                Archive instance
              </Button>
              <Button variant="destructive" onClick={() => setDeleteMode("confirmDelete")}>
                Delete instance
              </Button>
            </div>
          )}

          {deleteMode !== "confirmDelete" ? <DialogFooter /> : null}
        </DialogContent>
      </Dialog>

      <div className="mt-6">
        <MinecraftServerCard
          {...server}
          onToggle={() => toggleServer(serverId)}
          onForceStop={() => forceStopServer(serverId)}
          onEditIcon={() => setIconOpen(true)}
        />
      </div>

      <Dialog
        open={iconOpen}
        onOpenChange={(open) => {
          setIconOpen(open);
          if (!open) {
            setIconFile(null);
            setIconError(null);
            setIconImageEl(null);
            setIconWorkspacePreviewUrl(null);
            setIconOutputPreviewUrl(null);
          }
        }}
      >
        <DialogContent className="w-[min(640px,95vw)] max-w-[95vw]">
          <DialogHeader>
            <DialogTitle>Edit server icon</DialogTitle>
          </DialogHeader>

          {iconError ? (
            <div className="rounded-md border border-border bg-muted/30 p-3 text-sm">
              <span className="font-mono">{iconError}</span>
            </div>
          ) : null}

          <div className="grid gap-2">
            <Input
              type="file"
              accept="image/*"
              onChange={(e) => {
                setIconError(null);
                setIconFile(e.target.files?.[0] ?? null);
              }}
              spellCheck={false}
              autoCorrect="off"
              autoCapitalize="off"
            />
            <div className="text-xs text-muted-foreground">
              Uploading will replace <span className="font-mono">server-icon.png</span> in the instance root and keep a backup as{" "}
              <span className="font-mono">server-icon.png.old</span>.
            </div>
          </div>

          {iconImageEl && iconRotatedDims ? (
            <div className="grid gap-4">
              <div className="flex flex-wrap items-center gap-2">
                <Button
                  type="button"
                  variant="secondary"
                  onClick={() => setIconRotation((r) => (r + 90) % 360)}
                  disabled={iconSaving}
                >
                  <RotateCw className="mr-2 h-4 w-4" />
                  Rotate 90°
                </Button>
                <div className="text-xs text-muted-foreground">
                  Source: {iconImageEl.naturalWidth}x{iconImageEl.naturalHeight} • Working: {iconRotatedDims.width}x{iconRotatedDims.height} • Output: 64x64
                </div>
              </div>

              <div className="grid gap-4 md:grid-cols-[minmax(0,1fr)_180px]">
                <div className="grid gap-2">
                  <div className="text-sm font-medium">Crop (square)</div>
                  <div className="relative overflow-hidden rounded-md border border-border bg-muted/10 p-2">
                    <div className="relative mx-auto w-full max-w-[320px]">
                      <img
                        src={iconWorkspacePreviewUrl ?? iconSourcePreviewUrl ?? "/spawner.png"}
                        alt="Icon workspace preview"
                        className="h-auto w-full rounded-sm border border-border bg-background object-contain"
                      />
                      <div
                        className="pointer-events-none absolute border-2 border-emerald-500 shadow-[0_0_0_9999px_rgba(0,0,0,0.35)]"
                        style={{
                          left: `${(iconCrop.x / iconRotatedDims.width) * 100}%`,
                          top: `${(iconCrop.y / iconRotatedDims.height) * 100}%`,
                          width: `${(iconCrop.size / iconRotatedDims.width) * 100}%`,
                          height: `${(iconCrop.size / iconRotatedDims.height) * 100}%`,
                        }}
                      />
                    </div>
                  </div>

                  <div className="grid gap-3">
                    <label className="grid gap-1 text-xs text-muted-foreground">
                      Size: {iconCrop.size}px
                      <input
                        type="range"
                        min={1}
                        max={Math.max(1, Math.min(iconRotatedDims.width, iconRotatedDims.height))}
                        value={iconCrop.size}
                        onChange={(e) =>
                          setIconCrop((prev) =>
                            clampIconCrop({ ...prev, size: Number(e.target.value) || prev.size }, iconRotatedDims.width, iconRotatedDims.height),
                          )
                        }
                        disabled={iconSaving}
                      />
                    </label>
                    <label className="grid gap-1 text-xs text-muted-foreground">
                      X: {iconCrop.x}px
                      <input
                        type="range"
                        min={0}
                        max={Math.max(0, iconRotatedDims.width - iconCrop.size)}
                        value={Math.min(iconCrop.x, Math.max(0, iconRotatedDims.width - iconCrop.size))}
                        onChange={(e) =>
                          setIconCrop((prev) =>
                            clampIconCrop({ ...prev, x: Number(e.target.value) || 0 }, iconRotatedDims.width, iconRotatedDims.height),
                          )
                        }
                        disabled={iconSaving}
                      />
                    </label>
                    <label className="grid gap-1 text-xs text-muted-foreground">
                      Y: {iconCrop.y}px
                      <input
                        type="range"
                        min={0}
                        max={Math.max(0, iconRotatedDims.height - iconCrop.size)}
                        value={Math.min(iconCrop.y, Math.max(0, iconRotatedDims.height - iconCrop.size))}
                        onChange={(e) =>
                          setIconCrop((prev) =>
                            clampIconCrop({ ...prev, y: Number(e.target.value) || 0 }, iconRotatedDims.width, iconRotatedDims.height),
                          )
                        }
                        disabled={iconSaving}
                      />
                    </label>
                  </div>
                </div>

                <div className="grid gap-2">
                  <div className="text-sm font-medium">64x64 Preview</div>
                  <div className="flex items-center justify-center rounded-md border border-border bg-muted/10 p-4">
                    <img
                      src={iconOutputPreviewUrl ?? "/spawner.png"}
                      alt="64x64 icon preview"
                      className="h-32 w-32 rounded-sm border border-border bg-background object-contain"
                      style={{ imageRendering: "pixelated" }}
                    />
                  </div>
                  <div className="text-xs text-muted-foreground">
                    Saved icon is automatically exported as a 64x64 PNG.
                  </div>
                </div>
              </div>
            </div>
          ) : null}

          <DialogFooter className="gap-2 sm:gap-0">
            <Button variant="secondary" onClick={() => setIconOpen(false)} disabled={iconSaving}>
              Cancel
            </Button>
            <Button onClick={() => void doSaveIcon()} disabled={!iconImageEl || iconSaving}>
              {iconSaving ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Upload className="mr-2 h-4 w-4" />}
              Save
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <div className="mt-6">
        <Tabs defaultValue="properties">
          <TabsList className="w-full justify-start">
            <TabsTrigger value="properties">
              <Settings className="mr-2 h-4 w-4" />
              Server Properties
            </TabsTrigger>
            <TabsTrigger value="launch">
              <ServerCog className="mr-2 h-4 w-4" />
              Launch Settings
            </TabsTrigger>
            <TabsTrigger value="whitelist">
              <ListChecks className="mr-2 h-4 w-4" />
              Whitelist
            </TabsTrigger>
            <TabsTrigger value="console">
              <SquareTerminal className="mr-2 h-4 w-4" />
              Console
            </TabsTrigger>
            <TabsTrigger value="logs">
              <Logs className="mr-2 h-4 w-4" />
              Logs
            </TabsTrigger>
            <TabsTrigger value="files">
              <Folder className="mr-2 h-4 w-4" />
              Files
            </TabsTrigger>
            <TabsTrigger value="content">
              <Boxes className="mr-2 h-4 w-4" />
              Content
            </TabsTrigger>
          </TabsList>

          <TabsContent value="properties" className="mt-4 min-h-[calc(100vh-var(--topbar-h)-18rem)]">
            <Card className="overflow-hidden">
              <CardHeader className="pb-3">
                <div className="flex flex-wrap items-center justify-between gap-3">
                  <CardTitle className="text-base">Server Properties</CardTitle>

                  <div className="flex flex-wrap items-center gap-2">
                    <Button
                      variant="secondary"
                      onClick={() => void refreshProperties()}
                      disabled={propsSaving || propertiesLoading}
                    >
                      <RefreshCw className="mr-2 h-4 w-4" />
                      Refresh
                    </Button>

                    <Button onClick={() => void saveProperties()} disabled={propsSaving || propertiesLoading || propertiesMissing}>
                      {propsSaving ? (
                        <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                      ) : (
                        <Save className="mr-2 h-4 w-4" />
                      )}
                      Save
                    </Button>
                  </div>
                </div>

                <div className="mt-3">
                  <div className="relative">
                    <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
                    <Input
                      value={propertiesQuery}
                      onChange={(e) => setPropertiesQuery(e.target.value)}
                      placeholder="Search server.properties..."
                      className="pl-9"
                      disabled={propertiesMissing}
                      spellCheck={false}
                      autoCorrect="off"
                      autoCapitalize="off"
                    />
                  </div>
                </div>

                {propsError ? (
                  <div className="mt-3 rounded-md border border-border bg-muted/30 p-3 text-sm">
                    Failed to update properties: <span className="font-mono">{propsError}</span>
                  </div>
                ) : null}
              </CardHeader>

              <CardContent className="pt-0">
                <div
                  className="server-props-scroll max-h-[calc(100vh-var(--topbar-h)-18rem)] overflow-y-auto overflow-x-hidden rounded-md border border-border bg-muted/20 p-4"
                >
                  {propertiesMissing ? (
                    <div className="flex min-h-[40vh] items-center justify-center text-center">
                      <div className="max-w-md text-sm text-muted-foreground">
                        <div className="font-medium text-foreground">server.properties not found</div>
                        <div className="mt-1">
                          Start the server once to generate it, or upload/create a <span className="font-mono">server.properties</span> file in the Files tab.
                        </div>
                      </div>
                    </div>
                  ) : (
                    <ServerPropertiesForm value={properties} onChange={setProperties} query={propertiesQuery} />
                  )}
                </div>
              </CardContent>
            </Card>
          </TabsContent>

          <TabsContent value="launch" className="mt-4 min-h-[calc(100vh-var(--topbar-h)-18rem)]">
            <Card className="overflow-hidden">
              <CardHeader className="pb-3">
                <div className="flex flex-wrap items-center justify-between gap-3">
                  <CardTitle className="text-base">Launch Settings</CardTitle>
                  <Button
                    onClick={() => void saveLaunchSettings()}
                    disabled={launchSaving || launchLoading || server.status !== "offline"}
                  >
                    {launchSaving ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Save className="mr-2 h-4 w-4" />}
                    Save
                  </Button>
                </div>

                {server.status !== "offline" ? (
                  <div className="mt-3 rounded-md border border-border bg-muted/30 p-3 text-sm">
                    Stop the server to change launch settings (these apply on next start).
                  </div>
                ) : null}

                {launchError ? (
                  <div className="mt-3 rounded-md border border-border bg-muted/30 p-3 text-sm">
                    Failed to save: <span className="font-mono">{launchError}</span>
                  </div>
                ) : null}
              </CardHeader>

	              <CardContent className="space-y-4 pt-0">
	                <div className="grid gap-2">
	                  <div className="text-sm font-medium">Instance Type</div>
	                  <Select
	                    value={server.type}
	                    onValueChange={(v) => void saveInstanceType(v)}
	                    disabled={instanceTypeSaving || server.status !== "offline"}
	                  >
	                    <SelectTrigger>
	                      <SelectValue placeholder="Select type" />
	                    </SelectTrigger>
	                    <SelectContent>
	                      {(instanceTypes.length ? instanceTypes : [{ id: "vanilla", label: "Vanilla", implemented: true }]).map((t) => (
	                        <SelectItem key={t.id} value={t.id}>
	                          {t.label}
	                        </SelectItem>
	                      ))}
	                    </SelectContent>
	                  </Select>
	                  <div className="text-xs text-muted-foreground">This only changes how the instance is categorized.</div>
	                  {instanceTypeError ? (
	                    <div className="rounded-md border border-border bg-muted/30 p-3 text-sm">
	                      <span className="font-mono">{instanceTypeError}</span>
	                    </div>
	                  ) : null}
	                </div>

	                <div className="grid gap-2">
	                  <div className="text-sm font-medium">Java Path</div>
                  <Input
                    value={launch.javaPath}
                    onChange={(e) => setLaunch((p) => ({ ...p, javaPath: e.target.value }))}
                    placeholder="Path to the Java executable (or just 'java')"
                    disabled={server.status !== "offline"}
                    spellCheck={false}
                    autoCorrect="off"
                    autoCapitalize="off"
                  />
                </div>

                <div className="grid gap-2">
                  <div className="text-sm font-medium">Java Args</div>
                  <Textarea
                    value={launch.javaArgs}
                    onChange={(e) => setLaunch((p) => ({ ...p, javaArgs: e.target.value }))}
                    placeholder="<java args>"
                    disabled={server.status !== "offline"}
                    spellCheck={false}
                    autoCorrect="off"
                    autoCapitalize="off"
                  />
                </div>

                <div className="grid gap-2">
                  <div className="text-sm font-medium">Server Jar Name</div>
                  <Input
                    value={launch.serverJarName}
                    onChange={(e) => setLaunch((p) => ({ ...p, serverJarName: e.target.value }))}
                    placeholder="server.jar"
                    disabled={server.status !== "offline"}
                    spellCheck={false}
                    autoCorrect="off"
                    autoCapitalize="off"
                  />
                  <div className="text-xs text-muted-foreground">Relative to the server directory.</div>
                </div>
              </CardContent>
            </Card>
          </TabsContent>

          <TabsContent value="whitelist" className="mt-4 min-h-[calc(100vh-var(--topbar-h)-18rem)]">
            <Card>
              <CardHeader className="pb-3">
                <div className="flex flex-wrap items-center justify-between gap-3">
                  <div className="flex flex-wrap items-center gap-4">
                    <CardTitle className="text-base">Whitelist</CardTitle>
                    <div className="flex items-center gap-2">
                      <Switch
                        checked={whitelistEnabled}
                        onCheckedChange={(v) => void setWhitelistEnabled(Boolean(v))}
                        disabled={wlEnabledSaving || propertiesLoading || propsSaving}
                      />
                      <div className="text-sm text-muted-foreground">Enabled</div>
                    </div>
                  </div>

                  <div className="flex flex-wrap items-center gap-2">
                    <Button variant="secondary" onClick={addWhitelistRow} disabled={wlSaving || whitelistLoading}>
                      Add
                    </Button>
                    <Button onClick={() => void saveWhitelist()} disabled={wlSaving || whitelistLoading}>
                      {wlSaving ? <Loader2 className="h-4 w-4 animate-spin" /> : "Save"}
                    </Button>
                  </div>
                </div>

                {wlError ? (
                  <div className="mt-3 rounded-md border border-border bg-muted/30 p-3 text-sm">
                    Failed to update whitelist: <span className="font-mono">{wlError}</span>
                  </div>
                ) : null}

                {wlEnabledError ? (
                  <div className="mt-3 rounded-md border border-border bg-muted/30 p-3 text-sm">
                    Failed to update whitelist setting: <span className="font-mono">{wlEnabledError}</span>
                  </div>
                ) : null}
              </CardHeader>

              <CardContent className="pt-0">
                <div className="rounded-md border border-border bg-muted/20 p-3">
                  {whitelistLoading ? (
                    <div className="text-sm text-muted-foreground">Loading...</div>
                  ) : whitelistRows.length === 0 ? (
                    <div className="text-sm text-muted-foreground">No players whitelisted.</div>
                  ) : (
                    <div className="space-y-2">
                      {whitelistRows.map((row, idx) => {
                        const e = row.entry;
                        const idForHead = (e.uuid ?? "").trim() || (e.name ?? "").trim() || "MHF_Steve";
                        const headId = encodeURIComponent(idForHead.replace(/-/g, ""));
                        const headUrl = `https://mc-heads.net/head/${headId}/right/64`;
                        const resolving = Boolean(wlResolvingByKey[row.key]);
                        const rowErr = wlResolveErrorByKey[row.key];

                        return (
                          <div key={row.key} className="rounded-md border border-border bg-background/40 p-3">
                            <div className="grid gap-3 md:grid-cols-[auto_1fr_1fr_auto] items-center">
                              <img
                                src={headUrl}
                                alt=""
                                className="h-12 w-12 rounded-md border border-border bg-muted/30 object-cover"
                                onError={(ev) => {
                                  ev.currentTarget.src = "https://mc-heads.net/head/MHF_Steve/right/64";
                                }}
                              />

                              <Input
                                placeholder="Player name"
                                value={e.name ?? ""}
                                onChange={(ev) => {
                                  const nextName = ev.target.value;
                                  setWhitelistRows((prev) =>
                                    prev.map((r) => (r.key === row.key ? { ...r, entry: { ...r.entry, name: nextName } } : r)),
                                  );
                                  if (isValidUsername(nextName)) scheduleWhitelistResolve(row.key, "name", nextName);
                                }}
                              />

                              <Input
                                placeholder="UUID"
                                value={e.uuid ?? ""}
                                onChange={(ev) => {
                                  const nextUuid = ev.target.value;
                                  setWhitelistRows((prev) =>
                                    prev.map((r) => (r.key === row.key ? { ...r, entry: { ...r.entry, uuid: nextUuid } } : r)),
                                  );
                                  if (isValidUuid32(nextUuid)) scheduleWhitelistResolve(row.key, "uuid", nextUuid);
                                }}
                              />

                              <Button variant="secondary" onClick={() => removeWhitelistRow(idx)}>
                                Remove
                              </Button>
                            </div>

                            <div className="mt-2 flex items-center justify-between text-xs text-muted-foreground">
                              <div>
                                {resolving ? "Resolving…" : rowErr ? <span className="text-destructive">{rowErr}</span> : null}
                              </div>
                              <div className="font-mono">{(e.uuid ?? "").trim() ? normalizeUuidInput(e.uuid ?? "") : ""}</div>
                            </div>
                          </div>
                        );
                      })}
                    </div>
                  )}
                </div>
              </CardContent>
            </Card>
          </TabsContent>

          <TabsContent value="console" className="mt-4 min-h-[calc(100vh-var(--topbar-h)-18rem)]">
            <Card>
              <CardHeader className="pb-3">
                <CardTitle className="text-base">Console</CardTitle>
              </CardHeader>
              <CardContent className="space-y-3">
                <ConsoleBox lines={consoleLines} loading={consoleLoading} />

                <div className="flex gap-2">
                  <Input
                    value={consoleInput}
                    onChange={(e) => {
                      const v = e.target.value;
                      // If user edits while browsing history, exit history mode.
                      cmdHistoryIdxRef.current = -1;
                      cmdDraftRef.current = v;
                      setConsoleInput(v);
                    }}
                    placeholder={consoleDisabled ? "Server is offline" : "Type a command..."}
                    spellCheck={false}
                    autoCorrect="off"
                    autoCapitalize="off"
                    disabled={consoleDisabled || cmdSending}
                    onKeyDown={(e) => {
                      if (consoleDisabled) return;
                      if (e.key === "ArrowUp" || e.key === "ArrowDown") {
                        const history = cmdHistoryRef.current;
                        if (history.length === 0) return;

                        e.preventDefault();

                        const dir = e.key === "ArrowUp" ? -1 : 1;
                        let idx = cmdHistoryIdxRef.current;

                        if (idx === -1) {
                          // Save current input as draft before entering history mode.
                          cmdDraftRef.current = consoleInput;
                          idx = dir < 0 ? history.length - 1 : -1;
                        } else {
                          idx = idx + dir;
                        }

                        if (idx < 0) idx = 0;
                        if (idx >= history.length) {
                          cmdHistoryIdxRef.current = -1;
                          setConsoleInput(cmdDraftRef.current);
                          return;
                        }

                        cmdHistoryIdxRef.current = idx;
                        setConsoleInput(history[idx] ?? "");
                        return;
                      }

                      if (e.key === "Enter") void sendConsoleCommand();
                    }}
                  />
                  <Button onClick={() => void sendConsoleCommand()} disabled={cmdSending || consoleDisabled}>
                    {cmdSending ? <Loader2 className="h-4 w-4 animate-spin" /> : "Send"}
                  </Button>
                </div>

                {cmdError ? (
                  <div className="text-xs text-destructive">{cmdError}</div>
                ) : null}
              </CardContent>
            </Card>
          </TabsContent>

          <TabsContent value="logs" className="mt-4 min-h-[calc(100vh-var(--topbar-h)-18rem)]">
            <Card>
              <CardHeader className="pb-3">
                <div className="flex flex-wrap items-center justify-between gap-3">
                  <CardTitle className="text-base">Logs</CardTitle>

                  <div className="flex flex-wrap items-center gap-2">
                    <Button
                      variant="secondary"
                      onClick={() => void refreshLogs()}
                      disabled={logsLoading}
                    >
                      <RefreshCw className="mr-2 h-4 w-4" />
                      Refresh
                    </Button>
                  </div>
                </div>

                <div className="mt-3 grid gap-3 md:grid-cols-[220px_1fr]">
                  <Select
                      value={logsSource === "live" ? "__live__" : selectedLogFile || "__live__"}
                      onValueChange={(v) => {
                        if (v === "__live__") {
                          setLogsSource("live");
                          setLogFileError(null);
                          return;
                        }
                        setLogsSource("file");
                        setSelectedLogFile(v);
                        setLogFileError(null);
                      }}
                    >
                      <SelectTrigger className="w-55">
                        <SelectValue placeholder="Select log..." />
                      </SelectTrigger>
                      <SelectContent>
                        <SelectItem value="__live__">Live (stdout/stderr)</SelectItem>
                        {logFiles.map((f) => (
                          <SelectItem key={f.name} value={f.name}>
                            {f.name}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  <div className="relative">
                    <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
                    <Input
                      value={logsQuery}
                      onChange={(e) => setLogsQuery(e.target.value)}
                      placeholder="Search logs"
                      className="pl-9"
                    />
                  </div>
                </div>
              </CardHeader>

              <CardContent className="space-y-3">
                {logsSource === "file" && logFileError ? (
                  <div className="rounded-md border border-border bg-muted/30 p-3 text-sm">
                    Failed to load log file: <span className="font-mono">{logFileError}</span>
                  </div>
                ) : null}
                <ConsoleBox
                  lines={filteredLogs}
                  loading={logsSource === "live" ? logsLoading : logFileLoading}
                  wrap={false}
                />
              </CardContent>
            </Card>
          </TabsContent>

          <TabsContent value="files" className="mt-4 min-h-[calc(100vh-var(--topbar-h)-18rem)]">
            <Card>
              <CardHeader className="pb-3">
                <CardTitle className="text-base">Files</CardTitle>
              </CardHeader>
              <CardContent>
                <FileExplorer serverId={serverId} />
              </CardContent>
            </Card>
          </TabsContent>

          <TabsContent value="content" className="mt-4 min-h-[calc(100vh-var(--topbar-h)-18rem)]">
            <ContentTab serverId={serverId} serverVersion={server.version} serverStatus={server.status} serverType={server.type} />
          </TabsContent>
        </Tabs>
      </div>
    </div>
  );
}

function ConsoleBox({
  lines,
  loading,
  wrap = true,
}: {
  lines: string[];
  loading: boolean;
  wrap?: boolean;
}) {
  const scrollRef = React.useRef<HTMLDivElement | null>(null);
  const stickToBottomRef = React.useRef(true);

  React.useLayoutEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    if (loading) return;
    if (!stickToBottomRef.current) return;
    el.scrollTop = el.scrollHeight;
  }, [lines.length, loading]);

  return (
    <div className="rounded-md border border-border bg-muted/30 p-3">
      <div
        ref={scrollRef}
        onScroll={() => {
          const el = scrollRef.current;
          if (!el) return;
          const dist = el.scrollHeight - el.scrollTop - el.clientHeight;
          stickToBottomRef.current = dist < 24;
        }}
        className={`server-props-scroll max-h-105 overflow-auto font-mono text-xs leading-5 ${
          wrap ? "whitespace-pre-wrap" : "whitespace-pre"
        }`}
      >
        {loading ? (
          <div className="text-muted-foreground">Loading...</div>
        ) : lines.length === 0 ? (
          <div className="text-muted-foreground">No output yet.</div>
        ) : (
          lines.map((line, i) => <ConsoleLine key={i} line={line} />)
        )}
      </div>
    </div>
  );
}

function ConsoleLine({ line }: { line: string }) {
  const isWarn = /\bWARN\b/i.test(line);
  const isError = /\bERROR\b/i.test(line);

  const cls = isError
    ? "text-red-500"
    : isWarn
      ? "text-amber-500"
      : "text-foreground";

  return <div className={cls}>{line}</div>;
}
