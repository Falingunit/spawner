import * as React from "react";

import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";
import { Separator } from "@/components/ui/separator";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";

import {
  apiCreateServer,
  apiDetectMinecraftVersionFromJarPath,
  apiImportServerFromPath,
  apiListFabricGameVersions,
  apiListFabricLoaderVersions,
  apiListInstanceTypes,
  apiListMinecraftVersions,
  type ApiFabricGameVersion,
  type ApiFabricLoaderVersion,
  type ApiInstanceType,
  type ApiMinecraftVersion,
} from "@/lib/api";
import { Loader2, Plus, Search } from "lucide-react";

function byReleaseTimeDesc(a: ApiMinecraftVersion, b: ApiMinecraftVersion) {
  const ta = Date.parse(a.releaseTimeUtc);
  const tb = Date.parse(b.releaseTimeUtc);
  return (Number.isFinite(tb) ? tb : 0) - (Number.isFinite(ta) ? ta : 0);
}

function compareVersionPart(a: string, b: string) {
  const na = Number(a);
  const nb = Number(b);
  const aIsNum = Number.isFinite(na);
  const bIsNum = Number.isFinite(nb);
  if (aIsNum && bIsNum) return na - nb;
  return a.localeCompare(b, undefined, { numeric: true, sensitivity: "base" });
}

function byFabricVersionDesc(a: string, b: string) {
  const ap = a.split(".");
  const bp = b.split(".");
  const n = Math.max(ap.length, bp.length);
  for (let i = 0; i < n; i += 1) {
    const cmp = compareVersionPart(ap[i] ?? "0", bp[i] ?? "0");
    if (cmp !== 0) return -cmp;
  }
  return 0;
}

export function CreateInstanceDialog({ onCreated }: { onCreated: () => Promise<void> | void }) {
  const [open, setOpen] = React.useState(false);

  const [loadingMeta, setLoadingMeta] = React.useState(false);
  const [metaError, setMetaError] = React.useState<string | null>(null);

  const [types, setTypes] = React.useState<ApiInstanceType[]>([]);
  const [versions, setVersions] = React.useState<ApiMinecraftVersion[]>([]);
  const [fabricGameVersions, setFabricGameVersions] = React.useState<ApiFabricGameVersion[]>([]);
  const [latestRelease, setLatestRelease] = React.useState<string>("");
  const [latestSnapshot, setLatestSnapshot] = React.useState<string>("");

  const [name, setName] = React.useState("");
  const [type, setType] = React.useState("vanilla");
  const [showReleases, setShowReleases] = React.useState(true);
  const [showSnapshots, setShowSnapshots] = React.useState(false);
  const [versionQuery, setVersionQuery] = React.useState("");
  const [version, setVersion] = React.useState("");
  const [fabricLoaderVersions, setFabricLoaderVersions] = React.useState<ApiFabricLoaderVersion[]>([]);
  const [fabricLoaderVersion, setFabricLoaderVersion] = React.useState("");
  const [fabricLoadersLoading, setFabricLoadersLoading] = React.useState(false);
  const [fabricLoadersError, setFabricLoadersError] = React.useState<string | null>(null);
  const fabricLoaderCacheRef = React.useRef<Map<string, ApiFabricLoaderVersion[]>>(new Map());

  const [creating, setCreating] = React.useState(false);
  const [createError, setCreateError] = React.useState<string | null>(null);

  const [customServerJarPath, setCustomServerJarPath] = React.useState<string>("");
  const [customDetecting, setCustomDetecting] = React.useState(false);
  const [customDetectedVersion, setCustomDetectedVersion] = React.useState<string>("");
  const [customDetectError, setCustomDetectError] = React.useState<string | null>(null);
  const [customManualVersion, setCustomManualVersion] = React.useState(false);

  React.useEffect(() => {
    if (!open) return;
    let cancelled = false;

    setLoadingMeta(true);
    setMetaError(null);

    (async () => {
      try {
        const [typesResp, versionsResp, fabricGamesResp] = await Promise.all([
          apiListInstanceTypes(),
          apiListMinecraftVersions(),
          apiListFabricGameVersions(),
        ]);
        if (cancelled) return;

        setTypes(typesResp);
        setVersions((versionsResp.versions ?? []).slice().sort(byReleaseTimeDesc));
        setFabricGameVersions((fabricGamesResp ?? []).slice());
        setLatestRelease(versionsResp.latest?.release ?? "");
        setLatestSnapshot(versionsResp.latest?.snapshot ?? "");

        if (!type) setType(typesResp.find((t) => t.implemented)?.id ?? "vanilla");
        if (!version) {
          if (type === "fabric") setVersion((fabricGamesResp ?? [])[0]?.version ?? "");
          else setVersion(versionsResp.latest?.release ?? "");
        }
      } catch (e) {
        if (cancelled) return;
        setMetaError(e instanceof Error ? e.message : "Failed to load metadata");
      } finally {
        if (!cancelled) setLoadingMeta(false);
      }
    })();

    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  React.useEffect(() => {
    if (!open) return;
    setCustomServerJarPath("");
    setCustomDetecting(false);
    setCustomDetectedVersion("");
    setCustomDetectError(null);
    setCustomManualVersion(false);
    setFabricLoaderVersions([]);
    setFabricLoaderVersion("");
    setFabricLoadersLoading(false);
    setFabricLoadersError(null);
  }, [open]);

  React.useEffect(() => {
    if (!open) return;
    if (type !== "custom") return;

    const p = customServerJarPath.trim();
    setCustomDetectError(null);
    setCustomDetectedVersion("");
    setCustomManualVersion(false);

    if (!p) return;

    let cancelled = false;
    setCustomDetecting(true);

    const timer = window.setTimeout(() => {
      void (async () => {
        try {
          const res = await apiDetectMinecraftVersionFromJarPath(p);
          if (cancelled) return;

          if (res.detected && res.version) {
            setCustomDetectedVersion(res.version);
            setVersion(res.version);
            setCustomManualVersion(false);
          } else {
            setCustomManualVersion(true);
          }
        } catch (e) {
          if (cancelled) return;
          setCustomDetectError(e instanceof Error ? e.message : "Failed to detect version");
          setCustomManualVersion(true);
        } finally {
          if (!cancelled) setCustomDetecting(false);
        }
      })();
    }, 450);

    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [customServerJarPath, open, type]);

  React.useEffect(() => {
    if (!open) return;
    if (type === "fabric") return;
    // Reset the version to something visible if filters exclude the current value.
    const current = versions.find((v) => v.id === version);
    if (!current) return;
    const isRelease = current.type === "release";
    const isSnapshot = current.type === "snapshot";
    if ((isRelease && !showReleases) || (isSnapshot && !showSnapshots)) {
      setVersion(showReleases ? latestRelease : showSnapshots ? latestSnapshot : "");
    }
  }, [latestRelease, latestSnapshot, open, showReleases, showSnapshots, type, version, versions]);

  React.useEffect(() => {
    if (!open) return;
    if (type === "fabric") {
      const hasSelected = fabricGameVersions.some((v) => v.version === version);
      if (!hasSelected) setVersion(fabricGameVersions[0]?.version ?? "");
      return;
    }
    const hasSelected = versions.some((v) => v.id === version);
    if (!hasSelected) setVersion(latestRelease || versions[0]?.id || "");
  }, [fabricGameVersions, latestRelease, open, type, version, versions]);

  React.useEffect(() => {
    if (!open) return;
    if (type !== "fabric") return;
    if (!version) {
      setFabricLoaderVersions([]);
      setFabricLoaderVersion("");
      setFabricLoadersError(null);
      return;
    }

    const cached = fabricLoaderCacheRef.current.get(version);
    if (cached) {
      setFabricLoadersError(null);
      setFabricLoaderVersions(cached);
      setFabricLoaderVersion((prev) => (cached.some((x) => x.version === prev) ? prev : (cached[0]?.version ?? "")));
      return;
    }

    let cancelled = false;
    const ctrl = new AbortController();
    setFabricLoadersLoading(true);
    setFabricLoadersError(null);

    void (async () => {
      try {
        const resp = await apiListFabricLoaderVersions(version, { signal: ctrl.signal });
        if (cancelled) return;
        const next = (resp ?? []).slice().sort((a, b) => byFabricVersionDesc(a.version, b.version));
        fabricLoaderCacheRef.current.set(version, next);
        setFabricLoaderVersions(next);
        setFabricLoaderVersion((prev) => (next.some((x) => x.version === prev) ? prev : (next[0]?.version ?? "")));
      } catch (e) {
        if (cancelled) return;
        if (e instanceof DOMException && e.name === "AbortError") return;
        setFabricLoaderVersions([]);
        setFabricLoaderVersion("");
        setFabricLoadersError(e instanceof Error ? e.message : "Failed to load Fabric loader versions");
      } finally {
        if (!cancelled) setFabricLoadersLoading(false);
      }
    })();

    return () => {
      cancelled = true;
      ctrl.abort();
    };
  }, [open, type, version]);

  const filteredVersions = React.useMemo(() => {
    if (type === "fabric") return [];
    const q = versionQuery.trim().toLowerCase();
    return versions.filter((v) => {
      const isRelease = v.type === "release";
      const isSnapshot = v.type === "snapshot";
      if ((isRelease && !showReleases) || (isSnapshot && !showSnapshots)) return false;
      if (!q) return true;
      return v.id.toLowerCase().includes(q);
    });
  }, [showReleases, showSnapshots, type, versionQuery, versions]);

  const filteredFabricVersions = React.useMemo(() => {
    const q = versionQuery.trim().toLowerCase();
    return fabricGameVersions.filter((v) => {
      if ((v.stable && !showReleases) || (!v.stable && !showSnapshots)) return false;
      if (!q) return true;
      return v.version.toLowerCase().includes(q);
    });
  }, [fabricGameVersions, showReleases, showSnapshots, versionQuery]);

  const latestFabricStable = React.useMemo(
    () => fabricGameVersions.find((v) => v.stable)?.version ?? "",
    [fabricGameVersions],
  );
  const latestFabricUnstable = React.useMemo(
    () => fabricGameVersions.find((v) => !v.stable)?.version ?? "",
    [fabricGameVersions],
  );

  function selectLatestReleaseLike(nextVersion: string) {
    if (!nextVersion) return;
    setShowReleases(true);
    setVersion(nextVersion);
  }

  function selectLatestSnapshotLike(nextVersion: string) {
    if (!nextVersion) return;
    setShowSnapshots(true);
    setVersion(nextVersion);
  }

  const selectedType = React.useMemo(() => types.find((t) => t.id === type), [type, types]);
  const typeImplemented = type === "fabric" ? true : (selectedType?.implemented ?? type === "vanilla");
  const busyOverlayLabel = creating
    ? "Creating instance..."
    : customDetecting
      ? "Detecting Minecraft version..."
      : fabricLoadersLoading
        ? "Loading loader versions..."
        : loadingMeta
          ? "Loading versions..."
          : null;

  async function create() {
    if (creating) return;
    const n = name.trim();
    if (!n) {
      setCreateError("Name is required");
      return;
    }

    if (type === "custom") {
      if (!customServerJarPath.trim()) {
        setCreateError("Server jar path is required");
        return;
      }
      if (!version) {
        setCreateError("Version is required");
        return;
      }

      setCreating(true);
      setCreateError(null);
      try {
        await apiImportServerFromPath({ name: n, version, serverJarPath: customServerJarPath.trim() });
        setOpen(false);
        setName("");
        setVersionQuery("");
        await onCreated();
      } catch (e) {
        setCreateError(e instanceof Error ? e.message : "Failed to import instance");
      } finally {
        setCreating(false);
      }
      return;
    }

    if (!typeImplemented) {
      setCreateError("That instance type is not implemented yet.");
      return;
    }
    if (!version) {
      setCreateError("Version is required");
      return;
    }
    if (type === "fabric" && !fabricLoaderVersion) {
      setCreateError("Fabric loader version is required");
      return;
    }

    setCreating(true);
    setCreateError(null);
    try {
      await apiCreateServer({
        name: n,
        type,
        version,
        fabricLoaderVersion: type === "fabric" ? fabricLoaderVersion : undefined,
      });
      setOpen(false);
      setName("");
      setVersionQuery("");
      await onCreated();
    } catch (e) {
      setCreateError(e instanceof Error ? e.message : "Failed to create instance");
    } finally {
      setCreating(false);
    }
  }

  return (
    <Dialog
      open={open}
      onOpenChange={(v) => {
        setOpen(v);
        if (!v) setCreateError(null);
      }}
    >
      <DialogTrigger asChild>
        <Button>
          <Plus className="mr-2 h-4 w-4" />
          New Instance
        </Button>
      </DialogTrigger>

      <DialogContent className="w-[min(900px,95vw)] max-w-[95vw]">
        <DialogHeader>
          <DialogTitle>Create Instance</DialogTitle>
        </DialogHeader>

        {busyOverlayLabel ? (
          <div className="absolute inset-0 z-20 grid place-items-center rounded-lg bg-background/85 backdrop-blur-[1px]">
            <div className="flex items-center gap-2 rounded-md border border-border bg-background px-3 py-2 text-sm shadow-sm">
              <Loader2 className="h-4 w-4 animate-spin" />
              <span>{busyOverlayLabel}</span>
            </div>
          </div>
        ) : null}

        {metaError ? (
          <div className="rounded-md border border-border bg-muted/30 p-3 text-sm">
            Failed to load: <span className="font-mono">{metaError}</span>
          </div>
        ) : null}

        <div className="grid gap-4">
          <div className="grid gap-2">
            <div className="text-sm font-medium">Name</div>
            <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="My server" spellCheck={false} autoCapitalize="off" autoCorrect="off" />
          </div>

          <div className="grid gap-2">
            <div className="text-sm font-medium">Type</div>
            <div className="flex flex-wrap gap-2">
              {types.length === 0 && loadingMeta ? (
                <div className="text-sm text-muted-foreground">Loading...</div>
              ) : (
                (types.length ? types : [{ id: "vanilla", label: "Vanilla", implemented: true }]).map((t) => (
                  <Button
                    key={t.id}
                    type="button"
                    variant={type === t.id ? "default" : "secondary"}
                    onClick={() => setType(t.id)}
                    disabled={creating || loadingMeta}
                    title={t.implemented ? t.label : `${t.label} (not implemented yet)`}
                  >
                    {t.label}
                  </Button>
                ))
              )}
            </div>
          </div>

          <Separator />

          {type === "custom" ? (
            <div className="grid gap-4">
              <div className="grid gap-2">
                <div className="text-sm font-medium">Server Jar Path (on the backend machine)</div>
                <Input
                  value={customServerJarPath}
                  onChange={(e) => setCustomServerJarPath(e.target.value)}
                  placeholder="Path to server.jar on the backend machine"
                  disabled={creating}
                  spellCheck={false}
                  autoCapitalize="off"
                  autoCorrect="off"
                />
                <div className="text-xs text-muted-foreground">The server folder containing this jar will be copied into Spawner.</div>
                {customDetecting ? <div className="text-xs text-muted-foreground">Detecting version…</div> : null}
                {customDetectedVersion ? (
                  <div className="text-xs text-muted-foreground">
                    Detected version: <span className="font-mono">{customDetectedVersion}</span>
                  </div>
                ) : null}
                {customDetectError ? (
                  <div className="rounded-md border border-border bg-muted/30 p-3 text-sm">
                    <span className="font-mono">{customDetectError}</span>
                  </div>
                ) : null}
              </div>

              {customManualVersion ? (
                <div className="grid gap-2">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <div className="text-sm font-medium">Game Version</div>
                  <div className="flex items-center gap-4">
                    <label className="flex items-center gap-2 text-sm text-muted-foreground">
                      <Switch checked={showReleases} onCheckedChange={setShowReleases} disabled={creating || loadingMeta} />
                      Releases
                    </label>
                    <label className="flex items-center gap-2 text-sm text-muted-foreground">
                      <Switch checked={showSnapshots} onCheckedChange={setShowSnapshots} disabled={creating || loadingMeta} />
                      Snapshots
                    </label>
                  </div>
                </div>

                <div className="flex flex-wrap gap-2">
                  {latestRelease ? (
                    <Button variant="secondary" type="button" onClick={() => setVersion(latestRelease)} disabled={creating || loadingMeta}>
                      Latest release ({latestRelease})
                    </Button>
                  ) : null}
                  {latestSnapshot ? (
                    <Button variant="secondary" type="button" onClick={() => setVersion(latestSnapshot)} disabled={creating || loadingMeta}>
                      Latest snapshot ({latestSnapshot})
                    </Button>
                  ) : null}
                </div>

                <div className="relative">
                  <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
                  <Input
                    value={versionQuery}
                    onChange={(e) => setVersionQuery(e.target.value)}
                    placeholder="Search versions..."
                    className="pl-9"
                    spellCheck={false}
                    autoCapitalize="off"
                    autoCorrect="off"
                  />
                </div>

                <div className="app-scrollbar max-h-64 overflow-auto rounded-md border border-border bg-background/40">
                  {loadingMeta ? (
                    <div className="p-3 text-sm text-muted-foreground">Loading versions...</div>
                  ) : filteredVersions.length === 0 ? (
                    <div className="p-3 text-sm text-muted-foreground">No matching versions.</div>
                  ) : (
                    <div className="divide-y divide-border">
                      {filteredVersions.map((v) => {
                        const active = v.id === version;
                        return (
                          <button
                            key={v.id}
                            type="button"
                            className={[
                              "w-full px-3 py-2 text-left text-sm",
                              "hover:bg-muted/40",
                              active ? "bg-muted/40" : "",
                            ].join(" ")}
                            onClick={() => setVersion(v.id)}
                          >
                            <div className="flex items-center justify-between gap-3">
                              <div className="font-mono">{v.id}</div>
                              <div className="text-xs text-muted-foreground">{v.type}</div>
                            </div>
                          </button>
                        );
                      })}
                    </div>
                  )}
                </div>

                <div className="text-xs text-muted-foreground">
                  Selected: <span className="font-mono">{version || "(none)"}</span>
                </div>
              </div>
              ) : null}
            </div>
          ) : (
            <div className="grid gap-2">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <div className="text-sm font-medium">Game Version</div>
                <div className="flex items-center gap-4">
                  <label className="flex items-center gap-2 text-sm text-muted-foreground">
                    <Switch checked={showReleases} onCheckedChange={setShowReleases} disabled={creating || loadingMeta} />
                    {type === "fabric" ? "Stable" : "Releases"}
                  </label>
                  <label className="flex items-center gap-2 text-sm text-muted-foreground">
                    <Switch checked={showSnapshots} onCheckedChange={setShowSnapshots} disabled={creating || loadingMeta} />
                    {type === "fabric" ? "Unstable" : "Snapshots"}
                  </label>
                </div>
              </div>

              <div className="flex flex-wrap gap-2">
                {type === "fabric" ? (
                  <>
                    {latestFabricStable ? (
                      <Button
                        variant="secondary"
                        type="button"
                        onClick={() => selectLatestReleaseLike(latestFabricStable)}
                        disabled={creating || loadingMeta}
                      >
                        Latest stable ({latestFabricStable})
                      </Button>
                    ) : null}
                    {latestFabricUnstable ? (
                      <Button
                        variant="secondary"
                        type="button"
                        onClick={() => selectLatestSnapshotLike(latestFabricUnstable)}
                        disabled={creating || loadingMeta}
                      >
                        Latest unstable ({latestFabricUnstable})
                      </Button>
                    ) : null}
                  </>
                ) : (
                  <>
                    {latestRelease ? (
                      <Button
                        variant="secondary"
                        type="button"
                        onClick={() => selectLatestReleaseLike(latestRelease)}
                        disabled={creating || loadingMeta}
                      >
                        Latest release ({latestRelease})
                      </Button>
                    ) : null}
                    {latestSnapshot ? (
                      <Button
                        variant="secondary"
                        type="button"
                        onClick={() => selectLatestSnapshotLike(latestSnapshot)}
                        disabled={creating || loadingMeta}
                      >
                        Latest snapshot ({latestSnapshot})
                      </Button>
                    ) : null}
                  </>
                )}
              </div>

              <div className="relative">
                <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
                <Input
                  value={versionQuery}
                  onChange={(e) => setVersionQuery(e.target.value)}
                  placeholder="Search versions..."
                  className="pl-9"
                  spellCheck={false}
                  autoCapitalize="off"
                  autoCorrect="off"
                />
              </div>

              <div className="app-scrollbar max-h-64 overflow-auto rounded-md border border-border bg-background/40">
                {loadingMeta ? (
                  <div className="p-3 text-sm text-muted-foreground">Loading versions...</div>
                ) : type === "fabric" ? (
                  filteredFabricVersions.length === 0 ? (
                    <div className="p-3 text-sm text-muted-foreground">No matching versions.</div>
                  ) : (
                    <div className="divide-y divide-border">
                      {filteredFabricVersions.map((v) => {
                        const active = v.version === version;
                        return (
                          <button
                            key={v.version}
                            type="button"
                            className={[
                              "w-full px-3 py-2 text-left text-sm",
                              "hover:bg-muted/40",
                              active ? "bg-muted/40" : "",
                            ].join(" ")}
                            onClick={() => setVersion(v.version)}
                          >
                            <div className="flex items-center justify-between gap-3">
                              <div className="font-mono">{v.version}</div>
                              <div className="text-xs text-muted-foreground">{v.stable ? "stable" : "unstable"}</div>
                            </div>
                          </button>
                        );
                      })}
                    </div>
                  )
                ) : filteredVersions.length === 0 ? (
                  <div className="p-3 text-sm text-muted-foreground">No matching versions.</div>
                ) : (
                  <div className="divide-y divide-border">
                    {filteredVersions.map((v) => {
                      const active = v.id === version;
                      return (
                        <button
                          key={v.id}
                          type="button"
                          className={[
                            "w-full px-3 py-2 text-left text-sm",
                            "hover:bg-muted/40",
                            active ? "bg-muted/40" : "",
                          ].join(" ")}
                          onClick={() => setVersion(v.id)}
                        >
                          <div className="flex items-center justify-between gap-3">
                            <div className="font-mono">{v.id}</div>
                            <div className="text-xs text-muted-foreground">{v.type}</div>
                          </div>
                        </button>
                      );
                    })}
                  </div>
                )}
              </div>

              <div className="text-xs text-muted-foreground">
                Selected: <span className="font-mono">{version || "(none)"}</span>
              </div>

              {type === "fabric" ? (
                <div className="grid gap-2">
                  <div className="text-sm font-medium">Fabric Loader Version</div>
                  {fabricLoadersError ? (
                    <div className="rounded-md border border-border bg-muted/30 p-3 text-sm">
                      <span className="font-mono">{fabricLoadersError}</span>
                    </div>
                  ) : null}
                  <Select
                    value={fabricLoaderVersion || undefined}
                    onValueChange={setFabricLoaderVersion}
                    disabled={creating || loadingMeta || fabricLoadersLoading || !version}
                  >
                    <SelectTrigger className="w-full">
                      <SelectValue
                        placeholder={
                          fabricLoadersLoading
                            ? "Loading loaders..."
                            : fabricLoaderVersions.length === 0
                              ? "No loader versions available"
                              : "Select loader version"
                        }
                      />
                    </SelectTrigger>
                    <SelectContent>
                      {fabricLoaderVersions.map((x) => (
                        <SelectItem key={x.version} value={x.version}>
                          {x.version}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                  <div className="text-xs text-muted-foreground">
                    Defaulted to latest compatible loader for the selected game version.
                  </div>
                </div>
              ) : null}
            </div>
          )}
        </div>

        {createError ? (
          <div className="rounded-md border border-border bg-muted/30 p-3 text-sm">
            <span className="font-mono">{createError}</span>
          </div>
        ) : null}

        <DialogFooter>
          <Button onClick={() => void create()} disabled={creating || loadingMeta}>
            {creating ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : null}
            Create
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
