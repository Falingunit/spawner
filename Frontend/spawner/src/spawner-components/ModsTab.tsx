import * as React from "react";

import {
  apiDisableMod,
  apiEnableMod,
  apiImportMrpack,
  apiInstallModrinthVersion,
  apiListMods,
  apiModrinthProjectVersions,
  apiModrinthSearch,
  apiMrpackExportUrl,
  apiRemoveMod,
  apiUpdateMod,
  type ApiInstalledMod,
  type ApiModrinthSearchHit,
  type ApiModrinthProjectVersion,
} from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { Dialog, DialogContent, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Search, Loader2, Download, Upload, RefreshCw, Trash2, ArrowUpCircle, Server, List, Plus, Repeat2 } from "lucide-react";

function fmtBytes(n: number) {
  if (!Number.isFinite(n) || n <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB"];
  let v = n;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i += 1; }
  return `${v >= 10 || i === 0 ? v.toFixed(0) : v.toFixed(1)} ${units[i]}`;
}

export function ModsTab({
  serverId,
  serverVersion,
  serverStatus,
}: {
  serverId: string;
  serverVersion: string;
  serverStatus: string;
}) {
  const [mods, setMods] = React.useState<ApiInstalledMod[]>([]);
  const [loading, setLoading] = React.useState(true);
  const [error, setError] = React.useState<string | null>(null);
  const [modrinthError, setModrinthError] = React.useState<string | null>(null);
  const [busyByFile, setBusyByFile] = React.useState<Record<string, boolean>>({});
  const [searchQuery, setSearchQuery] = React.useState("");
  const [searching, setSearching] = React.useState(false);
  const [searchResults, setSearchResults] = React.useState<ApiModrinthSearchHit[]>([]);
  const [searchError, setSearchError] = React.useState<string | null>(null);
  const [versionsByProject, setVersionsByProject] = React.useState<Record<string, ApiModrinthProjectVersion[]>>({});
  const [loadingVersionsByProject, setLoadingVersionsByProject] = React.useState<Record<string, boolean>>({});
  const [installingVersionId, setInstallingVersionId] = React.useState<string | null>(null);
  const [installingLatestProjectId, setInstallingLatestProjectId] = React.useState<string | null>(null);
  const [mrpackImporting, setMrpackImporting] = React.useState(false);
  const [mrpackMessage, setMrpackMessage] = React.useState<string | null>(null);
  const [installDialogOpen, setInstallDialogOpen] = React.useState(false);
  const [showDisabledOnly, setShowDisabledOnly] = React.useState(false);
  const [updatingAll, setUpdatingAll] = React.useState(false);

  const offline = serverStatus === "offline";
  const modsByProjectId = React.useMemo(() => {
    const map = new Map<string, ApiInstalledMod[]>();
    for (const m of mods) {
      if (!m.projectId) continue;
      const arr = map.get(m.projectId) ?? [];
      arr.push(m);
      map.set(m.projectId, arr);
    }
    return map;
  }, [mods]);

  function upsertLocalMod(next: Partial<ApiInstalledMod> & { fileName: string }) {
    setMods((prev) => {
      const idx = prev.findIndex((m) => m.fileName === next.fileName);
      if (idx < 0) {
        const created: ApiInstalledMod = {
          fileName: next.fileName,
          enabled: next.enabled ?? true,
          size: next.size ?? 0,
          lastWriteTimeUtc: next.lastWriteTimeUtc ?? new Date().toISOString(),
          sha1: next.sha1 ?? null,
          displayName: next.displayName ?? next.fileName,
          iconUrl: next.iconUrl ?? null,
          projectId: next.projectId ?? null,
          projectSlug: next.projectSlug ?? null,
          versionId: next.versionId ?? null,
          versionNumber: next.versionNumber ?? null,
          isManual: next.isManual ?? !next.projectId,
          update: next.update ?? { available: false },
        };
        return [created, ...prev];
      }
      const merged = { ...prev[idx], ...next, update: next.update ? { ...prev[idx].update, ...next.update } : prev[idx].update };
      const out = prev.slice();
      out[idx] = merged;
      return out;
    });
  }

  function removeLocalMod(fileName: string) {
    setMods((prev) => prev.filter((m) => m.fileName !== fileName));
  }

  const refreshMods = React.useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await apiListMods(serverId);
      setMods(res.items ?? []);
      setModrinthError(res.modrinthError ?? null);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to load mods");
    } finally {
      setLoading(false);
    }
  }, [serverId]);

  React.useEffect(() => {
    void refreshMods();
  }, [refreshMods]);

  async function runFileAction(fileName: string, action: "enable" | "disable" | "remove" | "update") {
    if (busyByFile[fileName]) return;
    setBusyByFile((m) => ({ ...m, [fileName]: true }));
    setError(null);
    try {
      if (action === "enable") {
        await apiEnableMod(serverId, fileName);
        upsertLocalMod({ fileName, enabled: true });
      }
      if (action === "disable") {
        await apiDisableMod(serverId, fileName);
        upsertLocalMod({ fileName, enabled: false });
      }
      if (action === "remove") {
        await apiRemoveMod(serverId, fileName);
        removeLocalMod(fileName);
      }
      if (action === "update") {
        const res = (await apiUpdateMod(serverId, fileName)) as {
          updated?: boolean;
          fileName?: string;
          enabled?: boolean;
          versionId?: string;
          versionNumber?: string;
          projectId?: string;
        };
        if (res?.updated && res.fileName) {
          if (res.fileName !== fileName) removeLocalMod(fileName);
          upsertLocalMod({
            fileName: res.fileName,
            enabled: res.enabled ?? true,
            versionId: res.versionId ?? null,
            versionNumber: res.versionNumber ?? null,
            projectId: res.projectId ?? null,
            update: { available: false },
          });
        }
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : `Failed to ${action} mod`);
    } finally {
      setBusyByFile((m) => ({ ...m, [fileName]: false }));
    }
  }

  async function doSearch() {
    setSearching(true);
    setSearchError(null);
    try {
      const res = await apiModrinthSearch({
        query: searchQuery.trim(),
        projectType: "mod",
        loader: "fabric",
        mcVersion: serverVersion,
        limit: 20,
      });
      setSearchResults(res.hits ?? []);
    } catch (e) {
      setSearchError(e instanceof Error ? e.message : "Search failed");
    } finally {
      setSearching(false);
    }
  }

  async function loadVersions(project: ApiModrinthSearchHit) {
    if (versionsByProject[project.projectId] || loadingVersionsByProject[project.projectId]) return;
    setLoadingVersionsByProject((m) => ({ ...m, [project.projectId]: true }));
    try {
      const versions = await apiModrinthProjectVersions(project.slug || project.projectId, { loader: "fabric", mcVersion: serverVersion });
      setVersionsByProject((m) => ({ ...m, [project.projectId]: versions }));
    } catch (e) {
      setSearchError(e instanceof Error ? e.message : "Failed to load versions");
    } finally {
      setLoadingVersionsByProject((m) => ({ ...m, [project.projectId]: false }));
    }
  }

  async function installVersion(versionId: string) {
    setInstallingVersionId(versionId);
    setSearchError(null);
    try {
      const res = await apiInstallModrinthVersion(serverId, versionId);
      const installed = res.installed;
      if (installed) {
        for (const removedName of installed.removedFileNames ?? []) {
          removeLocalMod(removedName);
        }
        upsertLocalMod({
          fileName: installed.fileName,
          enabled: installed.enabled,
          projectId: installed.projectId,
          versionId: installed.versionId,
          versionNumber: installed.versionNumber,
          isManual: false,
          update: { available: false },
        });
      }
    } catch (e) {
      setSearchError(e instanceof Error ? e.message : "Install failed");
    } finally {
      setInstallingVersionId(null);
    }
  }

  async function installLatestForProject(project: ApiModrinthSearchHit) {
    if (!offline) return;
    if (installingLatestProjectId === project.projectId) return;
    setInstallingLatestProjectId(project.projectId);
    setSearchError(null);
    try {
      const cached = versionsByProject[project.projectId];
      const versions =
        cached && cached.length > 0
          ? cached
          : await apiModrinthProjectVersions(project.slug || project.projectId, { loader: "fabric", mcVersion: serverVersion });

      if (!cached) {
        setVersionsByProject((m) => ({ ...m, [project.projectId]: versions }));
      }

      const latest = versions[0];
      if (!latest) throw new Error("No compatible versions found for this mod");
      const res = await apiInstallModrinthVersion(serverId, latest.id);
      const installed = res.installed;
      if (installed) {
        for (const removedName of installed.removedFileNames ?? []) {
          removeLocalMod(removedName);
        }
        upsertLocalMod({
          fileName: installed.fileName,
          enabled: installed.enabled,
          projectId: installed.projectId,
          versionId: installed.versionId,
          versionNumber: installed.versionNumber,
          isManual: false,
          update: { available: false },
        });
      }
    } catch (e) {
      setSearchError(e instanceof Error ? e.message : "Install failed");
    } finally {
      setInstallingLatestProjectId(null);
    }
  }

  async function onMrpackSelected(file: File | null) {
    if (!file) return;
    setMrpackImporting(true);
    setMrpackMessage(null);
    setError(null);
    try {
      const res = await apiImportMrpack(serverId, file);
      const x = res.imported;
      setMrpackMessage(`Imported ${x.name || file.name}: ${x.downloadedFiles} files + ${x.overrideFiles} overrides`);
      await refreshMods();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to import .mrpack");
    } finally {
      setMrpackImporting(false);
    }
  }

  const manualCount = mods.filter((m) => m.isManual).length;
  const updatableMods = mods.filter((m) => m.update?.available);
  const visibleMods = showDisabledOnly ? mods.filter((m) => !m.enabled) : mods;

  async function updateAllMods() {
    if (!offline) return;
    if (updatingAll) return;
    if (updatableMods.length === 0) return;
    setUpdatingAll(true);
    setError(null);
    try {
      for (const mod of updatableMods) {
        await runFileAction(mod.fileName, "update");
      }
    } finally {
      setUpdatingAll(false);
    }
  }

  return (
    <Card>
      <CardHeader className="pb-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <CardTitle className="text-base">Mods</CardTitle>
          <div className="flex flex-wrap items-center gap-2">
            <Button variant="secondary" onClick={() => void refreshMods()} disabled={loading}>
              <RefreshCw className="mr-2 h-4 w-4" />
              Refresh
            </Button>
            <Button variant="secondary" onClick={() => void updateAllMods()} disabled={!offline || updatingAll || updatableMods.length === 0}>
              {updatingAll ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <ArrowUpCircle className="mr-2 h-4 w-4" />}
              Update All{updatableMods.length > 0 ? ` (${updatableMods.length})` : ""}
            </Button>
            <label className="inline-flex">
              <input
                type="file"
                accept=".mrpack,application/zip"
                className="hidden"
                onChange={(e) => void onMrpackSelected(e.target.files?.[0] ?? null)}
                disabled={!offline || mrpackImporting}
              />
              <span className="inline-flex">
                <Button asChild disabled={!offline || mrpackImporting}>
                  <span>{mrpackImporting ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Upload className="mr-2 h-4 w-4" />}Import .mrpack</span>
                </Button>
              </span>
            </label>
            <Button asChild variant="secondary">
              <a href={apiMrpackExportUrl(serverId)} download>
                <Download className="mr-2 h-4 w-4" />
                Export .mrpack
              </a>
            </Button>
            <Button onClick={() => setInstallDialogOpen(true)}>
              <Search className="mr-2 h-4 w-4" />
              Install Content
            </Button>
          </div>
        </div>
        {!offline ? (
          <div className="rounded-md border border-border bg-muted/30 p-3 text-sm">Stop the server to install, update, remove, or toggle mods.</div>
        ) : null}
        {error ? <div className="rounded-md border border-border bg-muted/30 p-3 text-sm"><span className="font-mono">{error}</span></div> : null}
        {modrinthError ? <div className="rounded-md border border-border bg-muted/20 p-3 text-xs text-muted-foreground">Modrinth metadata unavailable: {modrinthError}</div> : null}
        {mrpackMessage ? <div className="rounded-md border border-border bg-muted/20 p-3 text-sm">{mrpackMessage}</div> : null}
      </CardHeader>
      <CardContent className="space-y-6">
        <div>
          <div className="mb-2 flex flex-wrap items-center gap-2 text-sm">
            <span className="font-medium">Installed Mods</span>
            <Badge variant="outline">{mods.length}</Badge>
            {manualCount > 0 ? <Badge variant="secondary">{manualCount} manual</Badge> : null}
            <label className="ml-auto flex items-center gap-2 text-xs text-muted-foreground">
              <Switch checked={showDisabledOnly} onCheckedChange={(v) => setShowDisabledOnly(Boolean(v))} />
              Disabled only
            </label>
          </div>
          {loading ? (
            <div className="grid gap-2">
              {Array.from({ length: 4 }).map((_, i) => (
                <div key={i} className="rounded-md border border-border bg-background/50 p-3">
                  <div className="flex items-center gap-3">
                    <Skeleton className="h-10 w-10" />
                    <div className="min-w-0 flex-1 space-y-2">
                      <Skeleton className="h-4 w-40" />
                      <Skeleton className="h-3 w-64" />
                    </div>
                    <div className="flex gap-2">
                      <Skeleton className="h-8 w-20" />
                      <Skeleton className="h-8 w-20" />
                    </div>
                  </div>
                </div>
              ))}
            </div>
          ) : visibleMods.length === 0 ? (
            <div className="rounded-md border border-dashed border-border p-4 text-sm text-muted-foreground">
              {mods.length === 0
                ? "No mods installed. Search Modrinth above, import a `.mrpack`, or manually place `.jar` files in the instance `mods` folder."
                : "No disabled mods."}
            </div>
          ) : (
            <div className="grid gap-2">
              {visibleMods.map((m) => {
                const busy = !!busyByFile[m.fileName];
                return (
                  <div key={`${m.enabled ? "on" : "off"}:${m.fileName}`} className="rounded-md border border-border bg-background/50 p-3">
                    <div className="flex flex-wrap items-center justify-between gap-3">
                      <div className="flex min-w-0 items-center gap-3">
                        <img src={m.iconUrl || "/spawner.png"} alt="" className="h-10 w-10 rounded object-cover" onError={(e) => { e.currentTarget.src = "/spawner.png"; }} />
                        <div className="min-w-0">
                          <div className="flex flex-wrap items-center gap-2">
                            <div className="truncate font-medium">{m.displayName || m.fileName}</div>
                            {m.isManual ? <Badge variant="secondary">Manual</Badge> : <Badge variant="outline">Modrinth</Badge>}
                            {m.update?.available ? <Badge>Update {m.update.versionNumber || ""}</Badge> : null}
                          </div>
                          <div className="truncate text-xs text-muted-foreground">
                            {m.fileName} • {fmtBytes(m.size)} {m.versionNumber ? `• ${m.versionNumber}` : ""}
                          </div>
                        </div>
                      </div>
                      <div className="flex flex-wrap items-center gap-2">
                        {m.update?.available ? (
                          <Button size="sm" variant="secondary" disabled={!offline || busy} onClick={() => void runFileAction(m.fileName, "update")}>
                            {busy ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <ArrowUpCircle className="mr-2 h-4 w-4" />}
                            Update
                          </Button>
                        ) : null}
                        <div className="flex items-center gap-2 rounded-md border border-border px-2 py-1">
                          {busy ? <Loader2 className="h-4 w-4 animate-spin text-muted-foreground" /> : null}
                          <Switch
                            checked={m.enabled}
                            disabled={!offline || busy}
                            onCheckedChange={(next) => void runFileAction(m.fileName, next ? "enable" : "disable")}
                            aria-label={`${m.enabled ? "Disable" : "Enable"} ${m.displayName || m.fileName}`}
                            title={m.enabled ? "Enabled" : "Disabled"}
                          />
                        </div>
                        <Button size="sm" variant="destructive" disabled={!offline || busy} onClick={() => void runFileAction(m.fileName, "remove")}>
                          {busy ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Trash2 className="mr-2 h-4 w-4" />}
                          Delete
                        </Button>
                      </div>
                    </div>
                  </div>
                );
              })}
            </div>
          )}
        </div>
      </CardContent>

      <Dialog open={installDialogOpen} onOpenChange={setInstallDialogOpen}>
        <DialogContent className="w-[min(900px,95vw)] max-w-[95vw]">
          <DialogHeader>
            <DialogTitle>Install Content (Mods / Modrinth)</DialogTitle>
          </DialogHeader>
          <div className="grid gap-3">
            <div className="flex flex-wrap gap-2">
              <Input
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.target.value)}
                placeholder={`Search Fabric mods for ${serverVersion}`}
                className="min-w-[260px] flex-1"
                onKeyDown={(e) => { if (e.key === "Enter") void doSearch(); }}
              />
              <Button onClick={() => void doSearch()} disabled={searching}>
                {searching ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Search className="mr-2 h-4 w-4" />}
                Search
              </Button>
            </div>
            {searchError ? <div className="text-sm text-destructive">{searchError}</div> : null}
            <div className="app-scrollbar max-h-[60vh] overflow-auto space-y-2 pr-1">
              {searching && searchResults.length === 0 ? (
                <div className="grid gap-2">
                  {Array.from({ length: 4 }).map((_, i) => (
                    <div key={i} className="rounded-md border border-border bg-background/50 p-3">
                      <div className="flex gap-3">
                        <Skeleton className="h-10 w-10 shrink-0" />
                        <div className="min-w-0 flex-1 space-y-2">
                          <Skeleton className="h-4 w-40" />
                          <Skeleton className="h-3 w-full" />
                          <Skeleton className="h-3 w-2/3" />
                        </div>
                        <div className="flex gap-2">
                          <Skeleton className="h-8 w-16" />
                          <Skeleton className="h-8 w-16" />
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
              ) : null}
              {searchResults.map((r) => {
                const versions = versionsByProject[r.projectId] ?? [];
                const loadingVersions = !!loadingVersionsByProject[r.projectId];
                const installedForProject = modsByProjectId.get(r.projectId) ?? [];
                const exactLatestInstalled = versions.length > 0
                  ? installedForProject.some((m) => m.versionId === versions[0]?.id)
                  : installedForProject.length > 0 && installedForProject.every((m) => !m.update?.available);
                const installedAny = installedForProject.length > 0;
                const latestBtnLabel = exactLatestInstalled ? "Installed" : installedAny ? "Update" : "Install";
                return (
                  <div key={r.projectId} className="rounded-md border border-border bg-background/50 p-3">
                    <div className="flex flex-wrap items-start justify-between gap-3">
                      <div className="flex min-w-0 gap-3">
                        <img src={r.iconUrl || "/spawner.png"} alt="" className="h-10 w-10 rounded object-cover" onError={(e) => { e.currentTarget.src = "/spawner.png"; }} />
                        <div className="min-w-0">
                          <div className="truncate font-medium">{r.title || r.slug || r.projectId}</div>
                          {r.author ? <div className="text-xs text-muted-foreground">by {r.author}</div> : null}
                          <div className="line-clamp-2 text-xs text-muted-foreground">{r.description}</div>
                          <div className="mt-1 flex flex-wrap gap-1">
                            <Badge variant="outline"><Server className="mr-1 h-3 w-3" />{r.serverSide || "unknown"}</Badge>
                            {r.categories?.slice(0, 3).map((c) => <Badge key={c} variant="secondary">{c}</Badge>)}
                          </div>
                        </div>
                      </div>
                      <div className="ml-auto flex shrink-0 items-center gap-2">
                        <Button variant="secondary" size="sm" onClick={() => void loadVersions(r)} disabled={loadingVersions}>
                          {loadingVersions ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <List className="mr-2 h-4 w-4" />}
                          Versions
                        </Button>
                        <Button size="sm" onClick={() => void installLatestForProject(r)} disabled={!offline || installingLatestProjectId === r.projectId || exactLatestInstalled}>
                          {installingLatestProjectId === r.projectId ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : null}
                          {!installingLatestProjectId && (latestBtnLabel === "Update" ? <ArrowUpCircle className="mr-2 h-4 w-4" /> : latestBtnLabel === "Install" ? <Plus className="mr-2 h-4 w-4" /> : null)}
                          {latestBtnLabel}
                        </Button>
                      </div>
                    </div>
                    {versions.length > 0 ? (
                      <div className="mt-3 grid gap-2">
                        {versions.slice(0, 6).map((v) => {
                          const exactInstalled = installedForProject.some((m) => m.versionId === v.id);
                          const hasOtherInstalledVersion = !exactInstalled && installedForProject.length > 0;
                          return (
                          <div key={v.id} className="flex flex-wrap items-center justify-between gap-2 rounded border border-border/60 p-2 text-sm">
                            <div className="min-w-0">
                              <div className="truncate">{v.name || v.versionNumber}</div>
                              <div className="text-xs text-muted-foreground">{v.versionNumber} • {v.versionType || "release"}</div>
                            </div>
                            <Button size="sm" variant={exactInstalled ? "secondary" : "default"} disabled={!offline || installingVersionId === v.id || exactInstalled} onClick={() => void installVersion(v.id)}>
                              {installingVersionId === v.id ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : null}
                              {!installingVersionId && !exactInstalled ? (hasOtherInstalledVersion ? <Repeat2 className="mr-2 h-4 w-4" /> : <Plus className="mr-2 h-4 w-4" />) : null}
                              {exactInstalled ? "Installed" : hasOtherInstalledVersion ? "Switch" : "Install"}
                            </Button>
                          </div>
                        )})}
                      </div>
                    ) : null}
                  </div>
                );
              })}
            </div>
          </div>
        </DialogContent>
      </Dialog>
    </Card>
  );
}
