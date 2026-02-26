import MinecraftServerCard from "@/spawner-components/InstanceInfoCard";
import { useServerStore } from "@/stores/serverStore";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { RotateCcw, Trash2 } from "lucide-react";
import * as React from "react";

export default function ArchivePage() {
  const loaded = useServerStore((s) => s.loaded);
  const error = useServerStore((s) => s.error);
  const servers = useServerStore((s) => s.servers);
  const unarchiveServer = useServerStore((s) => s.unarchiveServer);
  const deleteServer = useServerStore((s) => s.deleteServer);

  const archived = servers.filter((s) => Boolean(s.archived));

  const [deleteOpen, setDeleteOpen] = React.useState(false);
  const [deleteTarget, setDeleteTarget] = React.useState<{ id: string; name: string } | null>(null);

  async function doDelete() {
    if (!deleteTarget) return;
    await deleteServer(deleteTarget.id);
    setDeleteOpen(false);
    setDeleteTarget(null);
  }

  return (
    <div className="mt-4 mx-auto w-full">
      <h1 className="mb-6 text-3xl font-semibold">Archive</h1>

      {error ? (
        <div className="mb-6 rounded-md border border-border bg-muted/30 p-3 text-sm">
          Failed to load servers: <span className="font-mono">{error}</span>
        </div>
      ) : null}

      {loaded && archived.length === 0 && !error ? (
        <div className="text-sm text-muted-foreground">No archived instances.</div>
      ) : null}

      <div className="grid gap-6 grid-cols-[repeat(auto-fit,minmax(min(100%,420px),1fr))]">
        {archived.map((s) => (
          <div key={s.id} className="relative">
            <MinecraftServerCard key={s.id} {...s} linkTo={`/servers/${s.id}`} onToggle={() => {}} archived />

            <div className="mt-2 flex gap-2">
              <Button variant="secondary" onClick={() => void unarchiveServer(s.id)}>
                <RotateCcw className="mr-2 h-4 w-4" />
                Restore
              </Button>
              <Button
                variant="destructive"
                onClick={() => {
                  setDeleteTarget({ id: s.id, name: s.name });
                  setDeleteOpen(true);
                }}
              >
                <Trash2 className="mr-2 h-4 w-4" />
                Delete
              </Button>
            </div>
          </div>
        ))}
      </div>

      <Dialog
        open={deleteOpen}
        onOpenChange={(open) => {
          setDeleteOpen(open);
          if (!open) setDeleteTarget(null);
        }}
      >
        <DialogContent className="w-[min(520px,95vw)] max-w-[95vw]">
          <DialogHeader>
            <DialogTitle>Delete instance</DialogTitle>
          </DialogHeader>

          <div className="text-sm text-muted-foreground">
            Permanently delete <span className="font-mono">{deleteTarget?.name ?? "this instance"}</span>? This removes the instance folder.
          </div>

          <DialogFooter className="gap-2 sm:gap-0">
            <Button
              variant="secondary"
              onClick={() => {
                setDeleteOpen(false);
                setDeleteTarget(null);
              }}
            >
              Cancel
            </Button>
            <Button variant="destructive" onClick={() => void doDelete()} disabled={!deleteTarget}>
              <Trash2 className="mr-2 h-4 w-4" />
              Delete
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
