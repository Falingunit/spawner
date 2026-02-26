import * as React from "react";

import { ArrowUp, Download, FilePlus2, FileText, Folder, FolderPlus, Image as ImageIcon, RefreshCw, Scissors, Search, Trash2, Upload } from "lucide-react";

import { Breadcrumb, BreadcrumbItem, BreadcrumbLink, BreadcrumbList, BreadcrumbPage, BreadcrumbSeparator } from "@/components/ui/breadcrumb";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Separator } from "@/components/ui/separator";
import { Textarea } from "@/components/ui/textarea";
import { apiBaseUrlForBrowser, apiFetchBlob } from "@/spawner-components/fileApiHelpers";
import { useServerStore } from "@/stores/serverStore";

type Entry = {
  name: string;
  path: string;
  isDir: boolean;
  size: number;
  lastWriteTimeUtc: string;
};

type ListResp = { path: string; entries: Entry[] };

function joinPath(a: string, b: string) {
  const aa = a.replace(/^\/+|\/+$/g, "");
  const bb = b.replace(/^\/+|\/+$/g, "");
  if (!aa) return bb;
  if (!bb) return aa;
  return `${aa}/${bb}`;
}

function parentPath(p: string) {
  const s = p.replace(/^\/+|\/+$/g, "");
  if (!s) return "";
  const idx = s.lastIndexOf("/");
  return idx < 0 ? "" : s.slice(0, idx);
}

function baseName(p: string) {
  const s = p.replace(/^\/+|\/+$/g, "");
  if (!s) return "";
  const idx = s.lastIndexOf("/");
  return idx < 0 ? s : s.slice(idx + 1);
}

function isRootServerIconPath(relPath: string) {
  return relPath.replace(/^\/+|\/+$/g, "").toLowerCase() === "server-icon.png";
}

function ext(name: string) {
  const idx = name.lastIndexOf(".");
  return idx < 0 ? "" : name.slice(idx + 1).toLowerCase();
}

const IMAGE_EXT = new Set(["png", "jpg", "jpeg", "gif", "webp", "svg", "bmp", "ico"]);

function fmtBytes(size: number) {
  if (!Number.isFinite(size) || size <= 0) return "";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let v = size;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  const digits = i === 0 ? 0 : i === 1 ? 1 : 2;
  return `${v.toFixed(digits)} ${units[i]}`;
}

function fmtDate(iso: string) {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "";
  return d.toLocaleString();
}

function entryIcon(e: Entry) {
  if (e.isDir) return <Folder className="h-4 w-4" />;
  if (IMAGE_EXT.has(ext(e.name))) return <ImageIcon className="h-4 w-4" />;
  return <FileText className="h-4 w-4" />;
}

export function FileExplorer({ serverId }: { serverId: string }) {
  const rootRef = React.useRef<HTMLDivElement | null>(null);
  const [cwd, setCwd] = React.useState<string>("");
  const [entries, setEntries] = React.useState<Entry[]>([]);
  const [loading, setLoading] = React.useState(false);
  const [err, setErr] = React.useState<string | null>(null);

  const [query, setQuery] = React.useState("");
  const [selected, setSelected] = React.useState<Entry | null>(null);
  const [open, setOpen] = React.useState<Entry | null>(null);
  const [openModal, setOpenModal] = React.useState(false);

  const [deleteConfirmOpen, setDeleteConfirmOpen] = React.useState(false);
  const [deleteTarget, setDeleteTarget] = React.useState<Entry | null>(null);

  const [renamingPath, setRenamingPath] = React.useState<string | null>(null);
  const [renameValue, setRenameValue] = React.useState("");
  const renameInputRef = React.useRef<HTMLInputElement | null>(null);

  const [clipboard, setClipboard] = React.useState<{ mode: "cut" | "copy"; items: Array<Pick<Entry, "path" | "name" | "isDir">> } | null>(null);

  const [ctxMenu, setCtxMenu] = React.useState<{ x: number; y: number; entry: Entry | null } | null>(null);

  const [dragOver, setDragOver] = React.useState(false);
  const fileInputRef = React.useRef<HTMLInputElement | null>(null);

  // editor/viewer state
  const [textContent, setTextContent] = React.useState("");
  const [textDirty, setTextDirty] = React.useState(false);
  const [textLoading, setTextLoading] = React.useState(false);
  const [textError, setTextError] = React.useState<string | null>(null);
  const skipNextTextLoadRef = React.useRef(false);

  const openIsImage = Boolean(open && !open.isDir && IMAGE_EXT.has(ext(open.name)));
  const openRawUrl =
    open && !open.isDir
      ? `${apiBaseUrlForBrowser()}/api/v1/servers/${encodeURIComponent(serverId)}/files/raw?${new URLSearchParams({
          path: open.path,
        }).toString()}`
      : "";

  async function fetchList(path: string) {
    const qs = new URLSearchParams();
    if (path) qs.set("path", path);
    const res = await fetch(`${apiBaseUrlForBrowser()}/api/v1/servers/${encodeURIComponent(serverId)}/files/list?${qs.toString()}`);
    if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
    const data = (await res.json()) as ListResp;
    return { path: data.path === "." ? "" : (data.path ?? ""), entries: data.entries ?? [] } satisfies ListResp;
  }

  async function refresh(nextCwd = cwd) {
    setErr(null);
    setLoading(true);
    try {
      const data = await fetchList(nextCwd);
      setCwd(data.path);
      setEntries(data.entries);
      return data;
    } catch (e) {
      setErr(e instanceof Error ? e.message : "Failed to list files");
    } finally {
      setLoading(false);
    }
    return null;
  }

  async function navigate(next: string) {
    setCwd(next);
    await refresh(next);
  }

  React.useEffect(() => {
    void refresh("");
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [serverId]);

  React.useEffect(() => {
    setSelected(null);
    setOpen(null);
    setOpenModal(false);
    setTextContent("");
    setTextDirty(false);
    setTextError(null);
    setRenamingPath(null);
    setRenameValue("");
    setCtxMenu(null);
  }, [cwd]);

  React.useEffect(() => {
    if (!renamingPath) return;
    const t = window.setTimeout(() => {
      renameInputRef.current?.focus();
      renameInputRef.current?.select();
    }, 0);
    return () => window.clearTimeout(t);
  }, [renamingPath]);

  React.useEffect(() => {
    if (!ctxMenu) return;
    const onDown = () => setCtxMenu(null);
    const onEsc = (e: KeyboardEvent) => {
      if (e.key === "Escape") setCtxMenu(null);
    };
    document.addEventListener("mousedown", onDown);
    window.addEventListener("keydown", onEsc);
    return () => {
      document.removeEventListener("mousedown", onDown);
      window.removeEventListener("keydown", onEsc);
    };
  }, [ctxMenu]);

  async function loadAsText(e: Entry) {
    if (e.isDir) return;
    if (IMAGE_EXT.has(ext(e.name))) return;

    setTextDirty(false);
    setTextError(null);
    setTextLoading(true);
    try {
      const qs = new URLSearchParams({ path: e.path });
      const res = await fetch(`${apiBaseUrlForBrowser()}/api/v1/servers/${encodeURIComponent(serverId)}/files/text?${qs.toString()}`);
      if (!res.ok) {
        let msg = "";
        try {
          const json = (await res.json()) as { error?: { message?: string } };
          msg = json.error?.message ?? "";
        } catch {
          // ignore
        }
        if (!msg) msg = await res.text();
        throw new Error(msg || `${res.status} ${res.statusText}`);
      }
      const data = (await res.json()) as { content?: string };
      setTextContent(data.content ?? "");
    } catch (ex) {
      setTextContent("");
      setTextError(ex instanceof Error ? ex.message : "Failed to open as text");
    } finally {
      setTextLoading(false);
    }
  }

  React.useEffect(() => {
    if (!open) return;
    setTextDirty(false);
    setTextError(null);
    if (openIsImage || open.isDir) {
      setTextContent("");
      return;
    }

    if (skipNextTextLoadRef.current) {
      skipNextTextLoadRef.current = false;
      return;
    }

    setTextContent("");
    void loadAsText(open);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open?.path]);

  async function openEntry(e: Entry) {
    if (e.isDir) return navigate(e.path === "." ? "" : e.path);
    setSelected(e);

    if (IMAGE_EXT.has(ext(e.name))) {
      setOpen(e);
      setOpenModal(true);
      return;
    }

    // Only open non-image files if they can be loaded as text.
    setErr(null);
    setTextError(null);
    setTextLoading(true);
    try {
      const qs = new URLSearchParams({ path: e.path });
      const res = await fetch(`${apiBaseUrlForBrowser()}/api/v1/servers/${encodeURIComponent(serverId)}/files/text?${qs.toString()}`);
      if (!res.ok) {
        let msg = "";
        try {
          const json = (await res.json()) as { error?: { message?: string } };
          msg = json.error?.message ?? "";
        } catch {
          // ignore
        }
        if (!msg) msg = await res.text();
        throw new Error(msg || `${res.status} ${res.statusText}`);
      }
      const data = (await res.json()) as { content?: string };
      setTextContent(data.content ?? "");
      setTextDirty(false);
      skipNextTextLoadRef.current = true;
      setOpen(e);
      setOpenModal(true);
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : "Unsupported file type");
    } finally {
      setTextLoading(false);
    }
  }

  async function saveText() {
    if (!open || open.isDir) return;
    setTextError(null);
    setTextLoading(true);
    try {
      const qs = new URLSearchParams({ path: open.path });
      const res = await fetch(`${apiBaseUrlForBrowser()}/api/v1/servers/${encodeURIComponent(serverId)}/files/text?${qs.toString()}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json", Accept: "application/json" },
        body: JSON.stringify({ content: textContent }),
      });
      if (!res.ok) {
        const msg = await res.text();
        throw new Error(msg || `${res.status} ${res.statusText}`);
      }
      setTextDirty(false);
      await refresh(cwd);
    } catch (ex) {
      setTextError(ex instanceof Error ? ex.message : "Failed to save");
    } finally {
      setTextLoading(false);
    }
  }

  async function createTextFile(name: string) {
    const trimmed = name.trim();
    if (!trimmed) return;
    setErr(null);
    try {
      const rel = joinPath(cwd, trimmed);
      const qs = new URLSearchParams({ path: rel });
      const res = await fetch(`${apiBaseUrlForBrowser()}/api/v1/servers/${encodeURIComponent(serverId)}/files/create-text?${qs.toString()}`, {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "application/json" },
        body: JSON.stringify({ content: "" }),
      });
      if (!res.ok) {
        const msg = await res.text();
        throw new Error(msg || `${res.status} ${res.statusText}`);
      }
      await refresh(cwd);
      setSelected({ name: trimmed.split("/").pop() ?? trimmed, path: rel, isDir: false, size: 0, lastWriteTimeUtc: new Date().toISOString() });
    } catch (e) {
      setErr(e instanceof Error ? e.message : "Failed to create file");
    }
  }

  async function createFolder(name: string) {
    const trimmed = name.trim().replace(/^\/+|\/+$/g, "");
    if (!trimmed) return;
    setErr(null);
    try {
      const rel = joinPath(cwd, trimmed);
      const qs = new URLSearchParams({ path: rel });
      const res = await fetch(`${apiBaseUrlForBrowser()}/api/v1/servers/${encodeURIComponent(serverId)}/files/mkdir?${qs.toString()}`, {
        method: "POST",
      });
      if (!res.ok) {
        const msg = await res.text();
        throw new Error(msg || `${res.status} ${res.statusText}`);
      }
      await refresh(cwd);
    } catch (e) {
      setErr(e instanceof Error ? e.message : "Failed to create folder");
    }
  }

  function makeUniqueNameFromSet(base: string, existingLower: Set<string>) {
    if (!existingLower.has(base.toLowerCase())) return base;
    for (let n = 2; n < 1000; n++) {
      const next = `${base} (${n})`;
      if (!existingLower.has(next.toLowerCase())) return next;
    }
    return `${base} (${Date.now()})`;
  }

  function makeUniqueName(base: string, destEntries: Entry[]) {
    return makeUniqueNameFromSet(base, new Set(destEntries.map((e) => e.name.toLowerCase())));
  }

  async function createDefaultFolder() {
    const name = makeUniqueName("new folder", entries);
    await createFolder(name);
    const data = await refresh(cwd);
    const created = data?.entries?.find((e) => e.name === name) ?? null;
    if (created) {
      setSelected(created);
      setRenamingPath(created.path);
      setRenameValue(created.name);
    }
  }

  async function createDefaultFile() {
    const name = makeUniqueName("new file", entries);
    await createTextFile(name);
    const data = await refresh(cwd);
    const created = data?.entries?.find((e) => e.name === name) ?? null;
    if (created) {
      setSelected(created);
      setRenamingPath(created.path);
      setRenameValue(created.name);
    }
  }

  async function moveOrCopy(kind: "move" | "copy", src: string, dst: string) {
    const res = await fetch(`${apiBaseUrlForBrowser()}/api/v1/servers/${encodeURIComponent(serverId)}/files/${kind}`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Accept: "application/json" },
      body: JSON.stringify({ src, dst }),
    });
    if (res.ok) return;

    let msg = "";
    try {
      const json = (await res.json()) as { error?: { message?: string } };
      msg = json.error?.message ?? "";
    } catch {
      // ignore
    }
    if (!msg) msg = await res.text();
    throw new Error(msg || `${res.status} ${res.statusText}`);
  }

  function startRenaming(entry: Entry) {
    setSelected(entry);
    setRenamingPath(entry.path);
    setRenameValue(entry.name);
    setCtxMenu(null);
  }

  function cancelRenaming() {
    setRenamingPath(null);
    setRenameValue("");
  }

  async function commitRenaming(entry: Entry, nextNameRaw: string) {
    const nextName = nextNameRaw.trim().replace(/^\/+|\/+$/g, "");
    if (!nextName) {
      cancelRenaming();
      return;
    }
    if (nextName === entry.name) {
      cancelRenaming();
      return;
    }

    const parent = parentPath(entry.path);
    const dst = joinPath(parent, nextName);
    setErr(null);
    try {
      await moveOrCopy("move", entry.path, dst);
      if (isRootServerIconPath(entry.path) || isRootServerIconPath(dst)) {
        useServerStore.getState().bumpServerIcon(serverId);
      }
      cancelRenaming();
      const data = await refresh(cwd);
      const moved = data?.entries?.find((e) => e.path === dst) ?? data?.entries?.find((e) => e.name === nextName) ?? null;
      if (moved) setSelected(moved);
    } catch (e) {
      setErr(e instanceof Error ? e.message : "Failed to rename");
    }
  }

  async function deleteEntry(entry: Entry) {
    if (!entry) return;

    setErr(null);
    try {
      const qs = new URLSearchParams({ path: entry.path });
      if (entry.isDir) qs.set("recursive", "true");
      const res = await fetch(`${apiBaseUrlForBrowser()}/api/v1/servers/${encodeURIComponent(serverId)}/files?${qs.toString()}`, { method: "DELETE" });
      if (!res.ok) {
        const msg = await res.text();
        throw new Error(msg || `${res.status} ${res.statusText}`);
      }
      if (isRootServerIconPath(entry.path)) {
        useServerStore.getState().bumpServerIcon(serverId);
      }
      setSelected(null);
      if (open?.path === entry.path) {
        setOpenModal(false);
        setOpen(null);
      }
      await refresh(cwd);
    } catch (e) {
      setErr(e instanceof Error ? e.message : "Failed to delete");
    }
  }

  function cutSelected() {
    if (!selected) return;
    setClipboard({ mode: "cut", items: [{ path: selected.path, name: selected.name, isDir: selected.isDir }] });
    setCtxMenu(null);
  }

  function copySelected() {
    if (!selected) return;
    setClipboard({ mode: "copy", items: [{ path: selected.path, name: selected.name, isDir: selected.isDir }] });
    setCtxMenu(null);
  }

  async function pasteItemsInto(destDir: string, mode: "cut" | "copy", items: Array<Pick<Entry, "path" | "name" | "isDir">>) {
    if (items.length === 0) return;
    setErr(null);

    try {
      const destListing = await fetchList(destDir);
      const nameSet = new Set(destListing.entries.map((e) => e.name.toLowerCase()));

      for (const it of items) {
        const base = baseName(it.path) || it.name;
        let dstName = makeUniqueNameFromSet(base, nameSet);

        for (let attempt = 0; attempt < 50; attempt++) {
          const dst = joinPath(destDir, dstName);
          try {
            await moveOrCopy(mode === "cut" ? "move" : "copy", it.path, dst);
            nameSet.add(dstName.toLowerCase());
            if (
              (mode === "cut" && isRootServerIconPath(it.path)) ||
              (destDir.replace(/^\/+|\/+$/g, "") === "" && dstName.toLowerCase() === "server-icon.png")
            ) {
              useServerStore.getState().bumpServerIcon(serverId);
            }
            break;
          } catch (e) {
            const msg = e instanceof Error ? e.message : "";
            if (msg.toLowerCase().includes("exists") || msg.toLowerCase().includes("conflict")) {
              dstName = `${base} (${attempt + 2})`;
              continue;
            }
            throw e;
          }
        }
      }

      // Refresh current view (even if pasted elsewhere) so moves out of the current folder show up immediately.
      await refresh(cwd);
    } catch (e) {
      setErr(e instanceof Error ? e.message : "Failed to paste");
    }
  }

  async function pasteInto(destDir: string) {
    if (!clipboard) return;
    await pasteItemsInto(destDir, clipboard.mode, clipboard.items);
    if (clipboard.mode === "cut") setClipboard(null);
  }

  async function uploadFilesToDir(destDir: string, files: FileList | null) {
    if (!files || files.length === 0) return;
    setErr(null);
    try {
      const qs = new URLSearchParams();
      if (destDir) qs.set("path", destDir);
      const form = new FormData();
      for (const f of Array.from(files)) form.append("files", f, f.name);

      const res = await fetch(`${apiBaseUrlForBrowser()}/api/v1/servers/${encodeURIComponent(serverId)}/files/upload?${qs.toString()}`, {
        method: "POST",
        body: form,
      });
      if (!res.ok) {
        const msg = await res.text();
        throw new Error(msg || `${res.status} ${res.statusText}`);
      }
      if (destDir.replace(/^\/+|\/+$/g, "") === "") {
        for (const f of Array.from(files)) {
          if (f.name.toLowerCase() === "server-icon.png") {
            useServerStore.getState().bumpServerIcon(serverId);
            break;
          }
        }
      }
      if (fileInputRef.current) fileInputRef.current.value = "";
      await refresh(cwd);
    } catch (e) {
      setErr(e instanceof Error ? e.message : "Failed to upload");
    }
  }

  async function uploadFiles(files: FileList | null) {
    await uploadFilesToDir(cwd, files);
  }

  async function downloadSelected() {
    if (!selected) return;
    try {
      if (selected.isDir) {
        const qs = new URLSearchParams({ path: selected.path });
        const blob = await apiFetchBlob(`/api/v1/servers/${encodeURIComponent(serverId)}/files/zip?${qs.toString()}`);
        const a = document.createElement("a");
        a.href = URL.createObjectURL(blob);
        a.download = `${selected.name || "folder"}.zip`;
        a.click();
        URL.revokeObjectURL(a.href);
        return;
      }

      const qs = new URLSearchParams({ path: selected.path, download: "true" });
      const blob = await apiFetchBlob(`/api/v1/servers/${encodeURIComponent(serverId)}/files/raw?${qs.toString()}`);
      const a = document.createElement("a");
      a.href = URL.createObjectURL(blob);
      a.download = selected.name;
      a.click();
      URL.revokeObjectURL(a.href);
    } catch (e) {
      setErr(e instanceof Error ? e.message : "Failed to download");
    }
  }

  const crumbs = React.useMemo(() => {
    const parts = cwd.replace(/^\/+|\/+$/g, "").split("/").filter(Boolean);
    const out: { name: string; path: string }[] = [{ name: "root", path: "" }];
    let acc = "";
    for (const p of parts) {
      acc = joinPath(acc, p);
      out.push({ name: p, path: acc });
    }
    return out;
  }, [cwd]);

  const filteredEntries = React.useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return entries;
    return entries.filter((e) => e.name.toLowerCase().includes(q));
  }, [entries, query]);

  const selectionDisabled = !selected || loading;

  function handleKeyDown(e: React.KeyboardEvent) {
    const target = e.target as HTMLElement | null;
    const tag = target?.tagName?.toLowerCase() ?? "";
    if (tag === "input" || tag === "textarea") return;

    if (e.key === "Escape") {
      if (ctxMenu) {
        setCtxMenu(null);
        e.preventDefault();
        return;
      }
      if (renamingPath) {
        cancelRenaming();
        e.preventDefault();
        return;
      }
    }

    if (e.key === "F2" && selected) {
      startRenaming(selected);
      e.preventDefault();
      return;
    }

    if (e.key === "Delete" && selected) {
      setDeleteTarget(selected);
      setDeleteConfirmOpen(true);
      e.preventDefault();
      return;
    }

    const k = e.key.toLowerCase();
    if (e.ctrlKey && k === "x") {
      cutSelected();
      e.preventDefault();
      return;
    }
    if (e.ctrlKey && k === "c") {
      copySelected();
      e.preventDefault();
      return;
    }
    if (e.ctrlKey && k === "v") {
      void pasteInto(cwd);
      e.preventDefault();
      return;
    }
  }

  async function handleDropInto(destDir: string, e: React.DragEvent) {
    e.preventDefault();
    setDragOver(false);

    if (e.dataTransfer.files && e.dataTransfer.files.length > 0) {
      // Upload dropped OS files into the destination folder.
      await uploadFilesToDir(destDir, e.dataTransfer.files);
      return;
    }

    const raw = e.dataTransfer.getData("application/x-spawner-items");
    if (!raw) return;

    try {
      const parsed = JSON.parse(raw) as Array<{ path: string; name: string; isDir: boolean }>;
      if (!Array.isArray(parsed) || parsed.length === 0) return;

      // Default move; allow copy if user indicates copy via dropEffect.
      const mode = e.dataTransfer.dropEffect === "copy" ? "copy" : "cut";
      await pasteItemsInto(destDir, mode, parsed);
    } catch {
      // ignore
    }
  }

  React.useEffect(() => {
    const onDragOver = (e: DragEvent) => {
      if (e.dataTransfer?.types?.includes("Files")) {
        e.preventDefault();
        setDragOver(true);
      }
    };

    const onDragLeave = (e: DragEvent) => {
      // leaving the window
      if (e.relatedTarget == null) setDragOver(false);
    };

    const onDrop = (e: DragEvent) => {
      if (e.dataTransfer?.files && e.dataTransfer.files.length > 0) {
        e.preventDefault();
        setDragOver(false);
        void uploadFilesToDir(cwd, e.dataTransfer.files);
      }
    };

    window.addEventListener("dragover", onDragOver);
    window.addEventListener("dragleave", onDragLeave);
    window.addEventListener("drop", onDrop);
    return () => {
      window.removeEventListener("dragover", onDragOver);
      window.removeEventListener("dragleave", onDragLeave);
      window.removeEventListener("drop", onDrop);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cwd, serverId]);

  return (
    <>
      <div
        className="min-w-0"
        ref={rootRef}
        tabIndex={0}
        onKeyDown={handleKeyDown}
        onMouseDown={() => rootRef.current?.focus()}
      >
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div className="flex min-w-0 flex-wrap items-center gap-2">
            <Button
              variant="secondary"
              size="sm"
              onClick={() => void navigate(parentPath(cwd))}
              disabled={loading || !cwd}
              title="Up"
            >
              <ArrowUp className="h-4 w-4" />
            </Button>
            <Button variant="secondary" size="sm" onClick={() => void refresh(cwd)} disabled={loading} title="Refresh">
              <RefreshCw className="h-4 w-4" />
            </Button>

            <Separator orientation="vertical" className="mx-1 h-8" />

            <Button
              variant="secondary"
              size="sm"
              onClick={() => {
                void createDefaultFolder();
              }}
              disabled={loading}
              title="New folder"
            >
              <FolderPlus className="mr-2 h-4 w-4" />
              New folder
            </Button>

            <Button
              variant="secondary"
              size="sm"
              onClick={() => {
                void createDefaultFile();
              }}
              disabled={loading}
              title="New file"
            >
              <FilePlus2 className="mr-2 h-4 w-4" />
              New file
            </Button>

            <Button
              variant="secondary"
              size="sm"
              onClick={() => fileInputRef.current?.click()}
              disabled={loading}
              title="Upload files"
            >
              <Upload className="mr-2 h-4 w-4" />
              Upload
            </Button>

            <input ref={fileInputRef} type="file" multiple className="hidden" onChange={(e) => void uploadFiles(e.target.files)} />

            <Separator orientation="vertical" className="mx-1 h-8" />

            <Button
              variant="secondary"
              size="sm"
              onClick={() => void downloadSelected()}
              disabled={selectionDisabled}
              title="Download"
            >
              <Download className="mr-2 h-4 w-4" />
              Download
            </Button>
            <Button variant="secondary" size="sm" onClick={() => void cutSelected()} disabled={selectionDisabled} title="Cut">
              <Scissors className="mr-2 h-4 w-4" />
              Cut
            </Button>
            <Button variant="secondary" size="sm" onClick={() => void copySelected()} disabled={selectionDisabled} title="Copy">
              <FileText className="mr-2 h-4 w-4" />
              Copy
            </Button>
            <Button variant="secondary" size="sm" onClick={() => void pasteInto(cwd)} disabled={!clipboard || loading} title="Paste">
              Paste
            </Button>
            <Button
              variant="secondary"
              size="sm"
              onClick={() => {
                if (!selected) return;
                setDeleteTarget(selected);
                setDeleteConfirmOpen(true);
              }}
              disabled={selectionDisabled}
              title="Delete"
            >
              <Trash2 className="mr-2 h-4 w-4" />
              Delete
            </Button>
          </div>

          <div className="relative w-full md:w-72">
            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
            <Input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search in folder" className="pl-9" />
          </div>
        </div>

        <div className="mt-2">
          <Breadcrumb>
            <BreadcrumbList>
              {crumbs.map((c, idx) => {
                const isLast = idx === crumbs.length - 1;
                return (
                  <React.Fragment key={c.path || "__root__"}>
                    <BreadcrumbItem>
                      {isLast ? (
                        <BreadcrumbPage className="max-w-[40ch] truncate">{c.name}</BreadcrumbPage>
                      ) : (
                        <BreadcrumbLink asChild className="cursor-pointer max-w-[40ch] truncate">
                          <button type="button" onClick={() => void navigate(c.path)}>
                            {c.name}
                          </button>
                        </BreadcrumbLink>
                      )}
                    </BreadcrumbItem>
                    {!isLast ? <BreadcrumbSeparator /> : null}
                  </React.Fragment>
                );
              })}
            </BreadcrumbList>
          </Breadcrumb>
        </div>

        {err ? (
          <div className="mt-3 rounded-md border border-border bg-muted/30 p-3 text-sm">
            <span className="font-mono">{err}</span>
          </div>
        ) : null}

          <div
            className={["relative mt-3 overflow-hidden rounded-md border border-border bg-background/40", dragOver ? "ring-2 ring-primary/60" : ""].join(" ")}
            onContextMenu={(e) => {
              e.preventDefault();
              setCtxMenu({ x: e.clientX, y: e.clientY, entry: null });
            }}
            onDragOver={(e) => {
              e.preventDefault();
              setDragOver(true);
            }}
            onDragLeave={() => setDragOver(false)}
            onDrop={(e) => {
              void handleDropInto(cwd, e);
            }}
          >
          <div className="grid grid-cols-[1fr_170px_110px] gap-2 border-b border-border bg-muted/30 px-3 py-2 text-xs text-muted-foreground">
            <div>Name</div>
            <div>Modified</div>
            <div className="text-right">Size</div>
          </div>

	        <div className="max-h-[60vh] overflow-auto">
	          {loading ? (
	            <div className="p-3 text-sm text-muted-foreground">Loading...</div>
	          ) : filteredEntries.length === 0 ? (
	            <div className="p-3 text-sm text-muted-foreground">{entries.length === 0 ? "Empty folder." : "No matches."}</div>
	          ) : (
              <div className="divide-y divide-border">
                {filteredEntries.map((e) => {
                  const active = selected?.path === e.path;
                  const renaming = renamingPath === e.path;
                  return (
                    <div
                      key={e.path}
                      className={[
                        "grid grid-cols-[1fr_170px_110px] gap-2 px-3 py-2 text-sm",
                        "cursor-default select-none",
                        "hover:bg-muted/40",
                        active ? "bg-muted/40" : "",
                      ].join(" ")}
                      onClick={() => setSelected(e)}
                      onDoubleClick={() => void openEntry(e)}
                      role="button"
                      tabIndex={0}
                      onKeyDown={(ev) => {
                        if (ev.key === "Enter") void openEntry(e);
                      }}
                      onContextMenu={(ev) => {
                        ev.preventDefault();
                        setSelected(e);
                        setCtxMenu({ x: ev.clientX, y: ev.clientY, entry: e });
                      }}
                      draggable
                      onDragStart={(ev) => {
                        const payload = JSON.stringify([{ path: e.path, name: e.name, isDir: e.isDir }]);
                        ev.dataTransfer.setData("application/x-spawner-items", payload);
                        ev.dataTransfer.effectAllowed = "copyMove";
                      }}
                      onDragOver={(ev) => {
                        if (!e.isDir) return;
                        ev.preventDefault();
                        ev.dataTransfer.dropEffect = ev.ctrlKey ? "copy" : "move";
                      }}
                      onDrop={(ev) => {
                        if (!e.isDir) return;
                        void handleDropInto(e.path, ev);
                      }}
                    >
                      <div className="min-w-0">
                        <div className="flex items-center gap-2">
                          <span className="text-muted-foreground">{entryIcon(e)}</span>
                          {renaming ? (
                            <Input
                              ref={renameInputRef}
                              value={renameValue}
                              onChange={(ev) => setRenameValue(ev.target.value)}
                              spellCheck={false}
                              autoCorrect="off"
                              autoCapitalize="off"
                              className="h-7 font-mono text-xs"
                              onKeyDown={(ev) => {
                                if (ev.key === "Enter") void commitRenaming(e, renameValue);
                                if (ev.key === "Escape") cancelRenaming();
                                ev.stopPropagation();
                              }}
                              onBlur={() => void commitRenaming(e, renameValue)}
                            />
                          ) : (
                            <span className="truncate font-mono">{e.name}</span>
                          )}
                        </div>
                      </div>
                      <div className="truncate text-xs text-muted-foreground">{fmtDate(e.lastWriteTimeUtc)}</div>
                      <div className="text-right text-xs text-muted-foreground">{e.isDir ? "" : fmtBytes(e.size)}</div>
                    </div>
                  );
                })}
              </div>
	          )}
	        </div>

	        <div className="border-t border-border px-3 py-2 text-xs text-muted-foreground">
	          Tip: double-click folders, images, and text files to open.
	        </div>

	        {dragOver ? (
	          <div className="pointer-events-none absolute inset-0 flex items-center justify-center bg-background/70">
	            <div className="rounded-md border border-border bg-background px-4 py-2 text-sm text-muted-foreground">Drop files to upload</div>
	          </div>
	        ) : null}
        </div>
      </div>
      <Dialog
        open={openModal}
        onOpenChange={(v) => {
          setOpenModal(v);
          if (!v) setOpen(null);
        }}
      >
        <DialogContent className="max-h-[85vh] w-[min(1400px,95vw)] max-w-[95vw] overflow-hidden p-0">
          <div className="flex flex-col">
            <DialogHeader className="border-b border-border p-4 pr-12">
              <DialogTitle className="truncate font-mono text-sm">{open?.path ?? ""}</DialogTitle>
            </DialogHeader>

            <div className="max-h-[70vh] overflow-auto p-4">
              {open && open.isDir ? null : openIsImage ? (
                <div className="rounded-md border border-border bg-background/40 p-3">
                  <img src={openRawUrl} alt="" className="max-h-[70vh] max-w-full" />
                </div>
              ) : (
                <>
                  {textError ? (
                    <div className="mb-3 rounded-md border border-border bg-muted/30 p-3 text-sm">
                      <span className="font-mono">{textError}</span>
                    </div>
                  ) : null}
                  <Textarea
                    value={textContent}
                    // Ensure long lines don't wrap and can scroll horizontally.
                    // (Some browsers ignore `wrap="off"`, so we also enforce via CSS.)
                    wrap="off"
                    style={{ whiteSpace: "pre", overflowWrap: "normal" }}
                    spellCheck={false}
                    autoCorrect="off"
                    autoCapitalize="off"
                    onChange={(e) => {
                      setTextContent(e.target.value);
                      setTextDirty(true);
                    }}
                    className="min-h-[55vh] font-mono text-xs !whitespace-pre !overflow-x-auto !overflow-y-auto"
                    disabled={textLoading}
                  />
                </>
              )}
            </div>

            <DialogFooter className="border-t border-border p-4">
              {!openIsImage ? (
                <>
                  <div className="mr-auto text-xs text-muted-foreground">{textDirty ? "Unsaved changes" : "Saved"}</div>
                  <Button onClick={() => void saveText()} disabled={textLoading || !textDirty}>
                    Save
                  </Button>
                </>
              ) : null}
            </DialogFooter>
          </div>
        </DialogContent>
      </Dialog>

      <Dialog
        open={deleteConfirmOpen}
        onOpenChange={(v) => {
          setDeleteConfirmOpen(v);
          if (!v) setDeleteTarget(null);
        }}
      >
        <DialogContent className="w-[min(520px,95vw)] max-w-[95vw]">
          <DialogHeader>
            <DialogTitle>Delete</DialogTitle>
          </DialogHeader>

          <div className="text-sm text-muted-foreground">
            Delete {deleteTarget?.isDir ? "folder" : "file"} <span className="font-mono">{deleteTarget?.name ?? ""}</span>?
          </div>

          <DialogFooter className="gap-2 sm:gap-0">
            <Button
              variant="secondary"
              onClick={() => {
                setDeleteConfirmOpen(false);
                setDeleteTarget(null);
              }}
            >
              Cancel
            </Button>
            <Button
              variant="destructive"
              disabled={!deleteTarget}
              onClick={() => {
                if (!deleteTarget) return;
                void (async () => {
                  await deleteEntry(deleteTarget);
                  setDeleteConfirmOpen(false);
                  setDeleteTarget(null);
                })();
              }}
            >
              <Trash2 className="mr-2 h-4 w-4" />
              Delete
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {ctxMenu ? (
        <div
          className="fixed z-50"
          style={{ left: ctxMenu.x, top: ctxMenu.y }}
          onContextMenu={(e) => e.preventDefault()}
        >
          <div className="w-52 rounded-md border border-border bg-background p-1 shadow-md">
	            {ctxMenu.entry ? (
	              <>
	                <button
	                  type="button"
	                  className="w-full rounded-sm px-2 py-1 text-left text-sm hover:bg-muted/40"
	                  onClick={() => {
	                    const entry = ctxMenu.entry;
	                    if (!entry) return;
	                    setCtxMenu(null);
	                    void openEntry(entry);
	                  }}
	                >
	                  Open
	                </button>
	                <button
	                  type="button"
	                  className="w-full rounded-sm px-2 py-1 text-left text-sm hover:bg-muted/40"
	                  onClick={() => {
	                    const entry = ctxMenu.entry;
	                    if (!entry) return;
	                    startRenaming(entry);
	                  }}
	                >
	                  Rename
	                </button>
                <button
                  type="button"
                  className="w-full rounded-sm px-2 py-1 text-left text-sm hover:bg-muted/40"
                  onClick={() => {
                    setSelected(ctxMenu.entry);
                    cutSelected();
                  }}
                >
                  Cut
                </button>
                <button
                  type="button"
                  className="w-full rounded-sm px-2 py-1 text-left text-sm hover:bg-muted/40"
                  onClick={() => {
                    setSelected(ctxMenu.entry);
                    copySelected();
                  }}
                >
                  Copy
                </button>
                {ctxMenu.entry.isDir ? (
                  <button
                    type="button"
                    className={["w-full rounded-sm px-2 py-1 text-left text-sm", clipboard ? "hover:bg-muted/40" : "opacity-50"].join(" ")}
                    disabled={!clipboard}
                    onClick={() => {
                      setCtxMenu(null);
                      void pasteInto(ctxMenu.entry!.path);
                    }}
                  >
                    Paste into folder
                  </button>
                ) : null}
                <button
                  type="button"
                  className="w-full rounded-sm px-2 py-1 text-left text-sm hover:bg-muted/40"
                  onClick={() => {
                    setCtxMenu(null);
                    setDeleteTarget(ctxMenu.entry);
                    setDeleteConfirmOpen(true);
                  }}
                >
                  Delete
                </button>
              </>
            ) : (
              <>
                <button
                  type="button"
                  className={["w-full rounded-sm px-2 py-1 text-left text-sm", clipboard ? "hover:bg-muted/40" : "opacity-50"].join(" ")}
                  disabled={!clipboard}
                  onClick={() => {
                    setCtxMenu(null);
                    void pasteInto(cwd);
                  }}
                >
                  Paste
                </button>
                <button
                  type="button"
                  className="w-full rounded-sm px-2 py-1 text-left text-sm hover:bg-muted/40"
                  onClick={() => {
                    setCtxMenu(null);
                    void createDefaultFolder();
                  }}
                >
                  New folder
                </button>
                <button
                  type="button"
                  className="w-full rounded-sm px-2 py-1 text-left text-sm hover:bg-muted/40"
                  onClick={() => {
                    setCtxMenu(null);
                    void createDefaultFile();
                  }}
                >
                  New file
                </button>
                <button
                  type="button"
                  className="w-full rounded-sm px-2 py-1 text-left text-sm hover:bg-muted/40"
                  onClick={() => {
                    setCtxMenu(null);
                    void refresh(cwd);
                  }}
                >
                  Refresh
                </button>
              </>
            )}
          </div>
        </div>
      ) : null}
    </>
  );
}
