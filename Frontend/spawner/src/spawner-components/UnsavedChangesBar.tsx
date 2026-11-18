import { Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";

export function UnsavedChangesBar({ saving, saveDisabled, blocked, error, onSave, onDiscard }: {
  saving: boolean;
  saveDisabled?: boolean;
  blocked?: boolean;
  error?: string | null;
  onSave: () => void;
  onDiscard: () => void;
}) {
  return (
    <div role="region" aria-label="Unsaved changes" className={`flex w-full flex-wrap items-center gap-3 rounded-lg border bg-background p-4 shadow-xl ${blocked ? "border-destructive ring-2 ring-destructive/30" : "border-border"}`}>
      <div className="min-w-0 flex-1 text-sm" role="status">
        {blocked ? "Save or discard your changes before leaving." : "You have unsaved changes."}
      </div>
      <Button variant="ghost" disabled={saving} onClick={onDiscard}>Discard</Button>
      <Button disabled={saving || saveDisabled} onClick={onSave}>
        {saving ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : null}
        {saving ? "Saving..." : "Save Changes"}
      </Button>
      {error ? <div role="alert" className="w-full break-words text-sm text-destructive">{error}</div> : null}
    </div>
  );
}
