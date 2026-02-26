import * as React from "react";

import {
  apiInstallModrinthContentVersion,
  apiListContent,
  apiModrinthProjectVersions,
  apiModrinthSearch,
  apiRemoveContent,
  type ApiInstalledContentItem,
  type ApiModrinthProjectVersion,
  type ApiModrinthSearchHit,
} from "@/lib/api";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { Dialog, DialogContent, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Search, Loader2, Trash2, RefreshCw, FileArchive, List, Plus, ArrowUpCircle } from "lucide-react";

function fmtBytes(n: number) {
  if (!Number.isFinite(n) || n <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB"];
  let v = n;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i += 1; }
  return `${v >= 10 || i === 0 ? v.toFixed(0) : v.toFixed(1)} ${units[i]}`;
}

export function DatapacksTab({
  serverId,
  serverVersion,
  serverStatus,
}: {
  serverId: string;
  serverVersion: string;
  serverStatus: string;
}) {
  const [items, setItems] = React.useState<ApiInstalledContentItem[]>([]);
  const [loading, setLoading] = React.useState(true);
  const [error, setError] = React.useState<string | null>(null);
  const [modrinthError, setModrinthError] = React.useState<string | null>(null);
  const [searchQuery, setSearchQuery] = React.useState("");
  const [searching, setSearching] = React.useState(false);
  const [searchResults, setSearchResults] = React.useState<ApiModrinthSearchHit[]>([]);
  const [searchError, setSearchError] = React.useState<string | null>(null);
  const [versionsByProject, setVersionsByProject] = React.useState<Record<string, ApiModrinthProjectVersion[]>>({});
  const [loadingVersionsByProject, setLoadingVersionsByProject] = React.useState<Record<string, boolean>>({});
  const [busyKey, setBusyKey] = React.useState<string | null>(null);
  const [installDialogOpen, setInstallDialogOpen] = React.useState(false);

  const offline = serverStatus === "offline";
  const itemsByProjectId = React.useMemo(() => {
    const m = new Map<string, ApiInstalledContentItem[]>();
    for (const item of items) {
      if (!item.projectId) continue;
      const arr = m.get(item.projectId) ?? [];
      arr.push(item);
      m.set(item.projectId, arr);
    }
    return m;
  }, [items]);

  function upsertLocalContent(fileName: string, patch?: Partial<ApiInstalledContentItem>) {
    setItems((prev) => {
      const idx = prev.findIndex((x) => x.fileName === fileName);
      if (idx < 0) {
        const created: ApiInstalledContentItem = {
          fileName,
          size: patch?.size ?? 0,
          lastWriteTimeUtc: patch?.lastWriteTimeUtc ?? new Date().toISOString(),
          isDirectory: patch?.isDirectory ?? false,
          displayName: patch?.displayName ?? fileName,
          iconUrl: patch?.iconUrl ?? null,
          projectId: patch?.projectId ?? null,
          projectSlug: patch?.projectSlug ?? null,
          versionId: patch?.versionId ?? null,
          versionNumber: patch?.versionNumber ?? null,
          isManual: patch?.isManual ?? !patch?.projectId,
        };
        return [created, ...prev];
      }
      const next = prev.slice();
      next[idx] = { ...prev[idx], ...patch, fileName };
      return next;
    });
  }

  const refresh = React.useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await apiListContent(serverId, "datapacks");
      setItems(res.items ?? []);
      setModrinthError(res.modrinthError ?? null);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to load data packs");
    } finally {
      setLoading(false);
    }
  }, [serverId]);

  React.useEffect(() => {
    void refresh();
  }, [refresh]);

  async function doSearch() {
    setSearching(true);
    setSearchError(null);
    try {
      const res = await apiModrinthSearch({
        query: searchQuery.trim(),
        projectType: "datapack",
        category: "datapack",
        loader: "datapack",
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
      const versions = await apiModrinthProjectVersions(project.slug || project.projectId, { mcVersion: serverVersion, loader: "datapack" });
      setVersionsByProject((m) => ({ ...m, [project.projectId]: versions }));
    } catch (e) {
      setSearchError(e instanceof Error ? e.message : "Failed to load versions");
    } finally {
      setLoadingVersionsByProject((m) => ({ ...m, [project.projectId]: false }));
    }
  }

  async function installLatest(project: ApiModrinthSearchHit) {
    if (!offline) return;
    setBusyKey(`install:${project.projectId}`);
    try {
      const cached = versionsByProject[project.projectId];
      const versions = cached?.length
        ? cached
        : await apiModrinthProjectVersions(project.slug || project.projectId, { mcVersion: serverVersion, loader: "datapack" });
      if (!cached) setVersionsByProject((m) => ({ ...m, [project.projectId]: versions }));
      const latest = versions[0];
      if (!latest) throw new Error("No compatible versions found");
      const res = await apiInstallModrinthContentVersion(serverId, "datapacks", latest.id);
      const installed = res.installed;
      if (installed?.fileName) {
        upsertLocalContent(installed.fileName, { isManual: false });
      }
    } catch (e) {
      setSearchError(e instanceof Error ? e.message : "Install failed");
    } finally {
      setBusyKey(null);
    }
  }

  async function installVersion(versionId: string) {
    if (!offline) return;
    setBusyKey(`version:${versionId}`);
    try {
      const res = await apiInstallModrinthContentVersion(serverId, "datapacks", versionId);
      const installed = res.installed;
      if (installed?.fileName) {
        upsertLocalContent(installed.fileName, { isManual: false });
      }
    } catch (e) {
      setSearchError(e instanceof Error ? e.message : "Install failed");
    } finally {
      setBusyKey(null);
    }
  }

  async function removeItem(fileName: string) {
    if (!offline) return;
    setBusyKey(`remove:${fileName}`);
    try {
      await apiRemoveContent(serverId, "datapacks", fileName);
      setItems((prev) => prev.filter((x) => x.fileName !== fileName));
    } catch (e) {
      setError(e instanceof Error ? e.message : "Delete failed");
    } finally {
      setBusyKey(null);
    }
  }

  return (
    <Card>
      <CardHeader className="pb-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <CardTitle className="flex items-center gap-2 text-base">
            <FileArchive className="h-4 w-4" />
            Data Packs
          </CardTitle>
          <div className="flex flex-wrap items-center gap-2">
            <Button variant="secondary" onClick={() => void refresh()} disabled={loading}>
              {loading ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <RefreshCw className="mr-2 h-4 w-4" />}
              Refresh
            </Button>
            <Button onClick={() => setInstallDialogOpen(true)}>
              <Search className="mr-2 h-4 w-4" />
              Install Content
            </Button>
          </div>
        </div>
        {!offline ? <div className="rounded-md border border-border bg-muted/30 p-3 text-sm">Stop the server to install or remove content.</div> : null}
        {error ? <div className="rounded-md border border-border bg-muted/30 p-3 text-sm"><span className="font-mono">{error}</span></div> : null}
        {modrinthError ? <div className="rounded-md border border-border bg-muted/20 p-3 text-xs text-muted-foreground">Modrinth metadata unavailable: {modrinthError}</div> : null}
      </CardHeader>
      <CardContent className="space-y-6">
        <div>
          <div className="mb-2 flex items-center gap-2 text-sm">
            <span className="font-medium">Installed Data Packs</span>
            <Badge variant="outline">{items.length}</Badge>
          </div>
          {loading ? (
            <div className="grid gap-2">
              {Array.from({ length: 4 }).map((_, i) => (
                <div key={i} className="rounded-md border border-border bg-background/50 p-3">
                  <div className="flex items-center gap-3">
                    <Skeleton className="h-10 w-10" />
                    <div className="min-w-0 flex-1 space-y-2">
                      <Skeleton className="h-4 w-40" />
                      <Skeleton className="h-3 w-56" />
                    </div>
                    <Skeleton className="h-8 w-20" />
                  </div>
                </div>
              ))}
            </div>
          ) : items.length === 0 ? (
            <div className="rounded-md border border-dashed border-border p-4 text-sm text-muted-foreground">
              No data packs found in the instance folder yet.
            </div>
          ) : (
            <div className="grid gap-2">
              {items.map((item) => (
                <div key={item.fileName} className="flex flex-wrap items-center justify-between gap-3 rounded-md border border-border bg-background/50 p-3">
                  <div className="flex min-w-0 items-center gap-3">
                    <img src={item.iconUrl || "/spawner.png"} alt="" className="h-10 w-10 rounded object-cover" onError={(e) => { e.currentTarget.src = "/spawner.png"; }} />
                    <div className="min-w-0">
                      <div className="flex flex-wrap items-center gap-2">
                        <div className="truncate font-medium">{item.displayName || item.fileName}</div>
                        {item.isManual ? <Badge variant="secondary">Manual</Badge> : <Badge variant="outline">Modrinth</Badge>}
                        {item.isDirectory ? <Badge variant="outline">Folder</Badge> : null}
                      </div>
                      <div className="truncate text-xs text-muted-foreground">
                        {item.fileName} {!item.isDirectory ? `• ${fmtBytes(item.size)}` : ""} {item.versionNumber ? `• ${item.versionNumber}` : ""}
                      </div>
                    </div>
                  </div>
                  <Button variant="destructive" size="sm" disabled={!offline || busyKey === `remove:${item.fileName}`} onClick={() => void removeItem(item.fileName)}>
                    {busyKey === `remove:${item.fileName}` ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Trash2 className="mr-2 h-4 w-4" />}
                    Delete
                  </Button>
                </div>
              ))}
            </div>
          )}
        </div>
      </CardContent>

      <Dialog open={installDialogOpen} onOpenChange={setInstallDialogOpen}>
        <DialogContent className="w-[min(900px,95vw)] max-w-[95vw]">
          <DialogHeader>
            <DialogTitle>Install Content (Data Packs / Modrinth)</DialogTitle>
          </DialogHeader>
          <div className="grid gap-3">
            <div className="flex flex-wrap gap-2">
              <Input
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.target.value)}
                placeholder="Search data packs on Modrinth"
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
                          <Skeleton className="h-4 w-36" />
                          <Skeleton className="h-3 w-full" />
                        </div>
                        <div className="flex gap-2">
                          <Skeleton className="h-8 w-16" />
                          <Skeleton className="h-8 w-24" />
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
              ) : null}
              {searchResults.map((r) => {
                const versions = versionsByProject[r.projectId] ?? [];
                const loadingVersions = !!loadingVersionsByProject[r.projectId];
                const installedForProject = itemsByProjectId.get(r.projectId) ?? [];
                const latestLoadedInstalled = versions.length > 0 && installedForProject.some((x) => x.versionId === versions[0]?.id);
                const latestLabel = latestLoadedInstalled ? "Installed" : (installedForProject.length > 0 ? "Update" : "Install Content");
                return (
                  <div key={r.projectId} className="rounded-md border border-border bg-background/50 p-3">
                    <div className="flex flex-wrap items-start justify-between gap-2">
                      <div className="flex min-w-0 gap-3">
                        <img src={r.iconUrl || "/spawner.png"} alt="" className="h-10 w-10 rounded object-cover" onError={(e) => { e.currentTarget.src = "/spawner.png"; }} />
                        <div className="min-w-0">
                          <div className="truncate font-medium">{r.title || r.slug || r.projectId}</div>
                          {r.author ? <div className="text-xs text-muted-foreground">by {r.author}</div> : null}
                          <div className="line-clamp-2 text-xs text-muted-foreground">{r.description}</div>
                        </div>
                      </div>
                      <div className="ml-auto flex shrink-0 items-center gap-2">
                        <Button variant="secondary" size="sm" onClick={() => void loadVersions(r)} disabled={loadingVersions}>
                          {loadingVersions ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <List className="mr-2 h-4 w-4" />}
                          Versions
                        </Button>
                        <Button size="sm" onClick={() => void installLatest(r)} disabled={!offline || busyKey === `install:${r.projectId}` || latestLoadedInstalled}>
                          {busyKey === `install:${r.projectId}` ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : null}
                          {!busyKey && (latestLabel === "Update" ? <ArrowUpCircle className="mr-2 h-4 w-4" /> : latestLabel !== "Installed" ? <Plus className="mr-2 h-4 w-4" /> : null)}
                          {latestLabel}
                        </Button>
                      </div>
                    </div>
                    {versions.length > 0 ? (
                      <div className="mt-3 grid gap-2">
                        {versions.slice(0, 6).map((v) => {
                          const exactInstalled = installedForProject.some((x) => x.versionId === v.id);
                          return (
                            <div key={v.id} className="flex flex-wrap items-center justify-between gap-2 rounded border border-border/60 p-2 text-sm">
                              <div className="min-w-0">
                                <div className="truncate">{v.name || v.versionNumber}</div>
                                <div className="text-xs text-muted-foreground">{v.versionNumber}</div>
                              </div>
                              <Button size="sm" variant={exactInstalled ? "secondary" : "default"} disabled={!offline || busyKey === `version:${v.id}` || exactInstalled} onClick={() => void installVersion(v.id)}>
                                {busyKey === `version:${v.id}` ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : null}
                                {!busyKey && !exactInstalled ? <Plus className="mr-2 h-4 w-4" /> : null}
                                {exactInstalled ? "Installed" : "Install"}
                              </Button>
                            </div>
                          );
                        })}
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
