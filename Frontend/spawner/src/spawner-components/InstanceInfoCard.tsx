import * as React from "react";
import { Link } from "react-router-dom";
import { Card, CardContent, CardHeader } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import {
  Play,
  Square,
  Pencil,
  OctagonX,
  Users,
  Package,
  Layers,
  Box,
  Wrench,
  Loader2,
  Network,
  Check,
} from "lucide-react";
import type { Server } from "@/types/server";

const TYPE_ICONS: Record<string, { icon: React.ElementType; label: string }> = {
  vanilla: { icon: Box, label: "Vanilla" },
  fabric: { icon: Layers, label: "Fabric" },
  custom: { icon: Wrench, label: "Custom" },
  forge: { icon: Wrench, label: "Forge" },
  neoforge: { icon: Wrench, label: "NeoForge" },
  quilt: { icon: Layers, label: "Quilt" },
  paper: { icon: Package, label: "Paper" },
  spigot: { icon: Package, label: "Spigot" },
};

type MinecraftServerCardProps = Server & {
  onToggle: () => Promise<void> | void;
  onForceStop?: () => Promise<void> | void;
  linkTo?: string;
  onEditIcon?: () => Promise<void> | void;
};

const MC_COLORS: Record<string, string> = {
  "0": "#000000",
  "1": "#0000AA",
  "2": "#00AA00",
  "3": "#00AAAA",
  "4": "#AA0000",
  "5": "#AA00AA",
  "6": "#FFAA00",
  "7": "#AAAAAA",
  "8": "#555555",
  "9": "#5555FF",
  a: "#55FF55",
  b: "#55FFFF",
  c: "#FF5555",
  d: "#FF55FF",
  e: "#FFFF55",
  f: "#FFFFFF",
};

function renderMotd(motd: string) {
  const lines = motd.replace(/\r\n/g, "\n").split("\n");
  return lines.map((line, i) => (
    <div key={i} className="leading-[1.15]">
      {parseMcFormat(line)}
    </div>
  ));
}

function parseMcFormat(input: string) {
  // Some sources accidentally UTF-8-decode "§" as "Â§" (two chars). Normalize it.
  const normalized = input.replace(/Â§/g, "§");
  let color = MC_COLORS.f;
  let bold = false;
  let italic = false;
  let underline = false;
  let strike = false;

  const out: React.ReactNode[] = [];
  let buf = "";

  const flush = (key: string) => {
    if (!buf) return;
    out.push(
      <span
        key={key}
        style={{
          color,
          fontWeight: bold ? 700 : 400,
          fontStyle: italic ? "italic" : "normal",
          textDecoration: [
            underline ? "underline" : "",
            strike ? "line-through" : "",
          ]
            .filter(Boolean)
            .join(" "),
          whiteSpace: "pre-wrap",
        }}
      >
        {buf}
      </span>,
    );
    buf = "";
  };

  for (let idx = 0; idx < normalized.length; idx++) {
    const ch = normalized[idx];
    if (ch === "§" && idx + 1 < normalized.length) {
      flush(`t-${idx}`);
      const code = normalized[idx + 1].toLowerCase();
      idx++;

      if (code in MC_COLORS) {
        color = MC_COLORS[code];
        bold = italic = underline = strike = false;
        continue;
      }
      if (code === "l") bold = true;
      else if (code === "o") italic = true;
      else if (code === "n") underline = true;
      else if (code === "m") strike = true;
      else if (code === "r") {
        color = MC_COLORS.f;
        bold = italic = underline = strike = false;
      }
      continue;
    }
    buf += ch;
  }
  flush("t-end");
  return out;
}

function formatBytes(n: number) {
  if (!Number.isFinite(n) || n <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = n;
  let idx = 0;
  while (value >= 1024 && idx < units.length - 1) {
    value /= 1024;
    idx += 1;
  }
  const fixed = value >= 100 || idx === 0 ? 0 : value >= 10 ? 1 : 2;
  return `${value.toFixed(fixed)} ${units[idx]}`;
}

export default function MinecraftServerCard(props: MinecraftServerCardProps) {
  const {
    name,
    iconUrl,
    version,
    type,
    status,
    archived,
    motd,
    playersOnline,
    playersMax,
    port,
    onToggle,
    onForceStop,
    linkTo,
    onEditIcon,
  } = props;

  const [loading, setLoading] = React.useState(false);
  const [forceLoading, setForceLoading] = React.useState(false);
  const [copied, setCopied] = React.useState(false);
  const copyTimeoutRef = React.useRef<number | null>(null);

  React.useEffect(() => {
    return () => {
      if (copyTimeoutRef.current != null) {
        window.clearTimeout(copyTimeoutRef.current);
      }
    };
  }, []);

  const isOnline = status === "online";
  const isStarting = status === "starting";
  const isStopping = status === "stopping";
  const isDownloading = status === "downloading";
  const isArchived = Boolean(archived);
  const isBusy = loading || forceLoading || isStarting || isStopping || isDownloading;
  const statusLabel = isOnline
    ? "Online"
    : isStarting
      ? "Starting"
      : isStopping
        ? "Stopping"
        : isDownloading
          ? "Downloading"
        : isArchived
          ? "Archived"
          : "Offline";
  const statusClass = isOnline
    ? "text-emerald-500"
    : isStarting
      ? "text-amber-500"
      : isStopping
        ? "text-amber-500"
        : isDownloading
          ? "text-amber-500"
      : "text-muted-foreground";
  const toggleLabel = isOnline
    ? "Stop server"
    : isStarting
      ? "Server starting"
      : isStopping
        ? "Server stopping"
        : isDownloading
          ? "Downloading"
        : isArchived
          ? "Archived"
      : "Start server";

  const typeKey = type.toLowerCase();
  const typeMeta = TYPE_ICONS[typeKey];

  const copyPort = React.useCallback(async () => {
    try {
      if (!navigator.clipboard?.writeText) return;
      await navigator.clipboard.writeText(String(port));
      setCopied(true);

      if (copyTimeoutRef.current != null) {
        window.clearTimeout(copyTimeoutRef.current);
      }
      copyTimeoutRef.current = window.setTimeout(() => setCopied(false), 1200);
    } catch {
      // ignore clipboard errors (e.g. insecure context)
    }
  }, [port]);

  async function handleToggle() {
    if (isBusy) return;
    if (isArchived) return;
    setLoading(true);
    try {
      await onToggle();
    } finally {
      setLoading(false);
    }
  }

  async function handleForceStop() {
    if (!onForceStop) return;
    if (forceLoading) return;
    setForceLoading(true);
    try {
      await onForceStop();
    } finally {
      setForceLoading(false);
    }
  }

  const safeOnline = Number.isFinite(playersOnline)
    ? Math.max(0, playersOnline)
    : 0;
  const safeMax = Number.isFinite(playersMax) ? Math.max(0, playersMax) : 0;

  const initPercent = typeof props.init?.percent === "number" ? props.init?.percent : null;
  const showInit = isDownloading && (initPercent != null || props.init?.message);
  const initHasKnownTotal = typeof props.init?.totalBytes === "number" && props.init.totalBytes > 0;
  const initBytesLabel =
    typeof props.init?.bytesReceived === "number" && props.init.bytesReceived > 0
      ? initHasKnownTotal
        ? `${formatBytes(props.init.bytesReceived)} / ${formatBytes(props.init?.totalBytes ?? 0)}`
        : `${formatBytes(props.init.bytesReceived)} downloaded`
      : null;

  return (
    <>
      <Card className="w-full">
        <CardHeader className="pb-3">
          <div className="flex items-center gap-3">
            <div className="relative h-10 w-10">
              <img
                src={iconUrl}
                alt=""
                className="h-10 w-10 rounded-md border border-border object-cover"
                onError={(e) => {
                  e.currentTarget.src = "/spawner.png";
                }}
              />

              {onEditIcon ? (
                <button
                  type="button"
                  className={[
                    "group absolute inset-0 grid place-items-center rounded-md",
                    "bg-black/0 transition-colors",
                    "hover:bg-black/45",
                    "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background",
                  ].join(" ")}
                  aria-label="Edit server icon"
                  title="Edit icon"
                  onClick={() => void onEditIcon()}
                >
                  <Pencil className="h-4 w-4 text-white opacity-0 transition-opacity group-hover:opacity-100" />
                </button>
              ) : null}
            </div>

            <div className="min-w-0 flex-1">
              <div className="truncate text-lg font-semibold">
                {linkTo ? (
                  <Link
                    to={linkTo}
                    className="hover:underline focus:outline-none focus:underline"
                    aria-label={`Open ${name}`}
                  >
                    {name}
                  </Link>
                ) : (
                  name
                )}
              </div>
              <div className="mt-1 flex flex-wrap items-center gap-2">
                {typeMeta ? (
                  <Badge variant="secondary" className="flex items-center gap-1">
                    {React.createElement(typeMeta.icon, {
                      className: "h-3.5 w-3.5",
                    })}
                    <span>{typeMeta.label}</span>
                  </Badge>
                ) : (
                  <Badge variant="secondary">{type}</Badge>
                )}
                <Badge variant="outline">{version}</Badge>
                <span className={`text-xs font-medium ${statusClass}`}>
                  {statusLabel}
                </span>
              </div>
            </div>

            {/* Right side: button + player count underneath */}
            <div className="flex flex-col items-end gap-1">
              <div className="flex items-center gap-2">
                <Button
                  variant={isOnline ? "destructive" : "default"}
                  size="icon"
                  onClick={handleToggle}
                  disabled={isBusy || isArchived}
                  aria-label={toggleLabel}
                >
                  {isBusy ? (
                    <Loader2 className="h-5 w-5 animate-spin" />
                  ) : isOnline ? (
                    <Square className="h-5 w-5" />
                  ) : (
                    <Play className="h-5 w-5" />
                  )}
                </Button>

                {onForceStop && (isStarting || isStopping) ? (
                  <Button
                    variant="destructive"
                    size="icon"
                    onClick={handleForceStop}
                    disabled={forceLoading}
                    aria-label="Force stop server"
                    title="Force stop"
                  >
                    {forceLoading ? <Loader2 className="h-5 w-5 animate-spin" /> : <OctagonX className="h-5 w-5" />}
                  </Button>
                ) : null}
              </div>

              {/* player count (reserved height) */}
              <div
                className={`flex items-center gap-1 text-xs text-muted-foreground`}
              >
                <Users className="h-3.5 w-3.5" />
                <span>
                  {isOnline ? (
                    <span>
                      {safeOnline}/{safeMax}
                    </span>
                  ) : (
                    <span>- /{safeMax}</span>
                  )}
                </span>
              </div>

              {showInit ? (
                <div className="w-full max-w-[140px] space-y-1">
                  <div className="h-1.5 w-full overflow-hidden rounded-full bg-muted">
                    {initPercent != null ? (
                      <div
                        className="h-full bg-amber-500 transition-[width]"
                        style={{ width: `${Math.max(1, Math.min(100, initPercent))}%` }}
                      />
                    ) : (
                      <div className="h-full w-1/3 animate-pulse rounded-full bg-amber-500/90" />
                    )}
                  </div>
                  <div className="truncate text-[10px] text-muted-foreground">
                    {initPercent != null
                      ? `${initPercent}%`
                      : initBytesLabel ?? props.init?.message ?? "Downloading"}
                  </div>
                </div>
              ) : null}

              {/* port (always visible, stable height) */}
              <button
                type="button"
                onClick={() => void copyPort()}
                className="cursor-pointer
    flex items-center gap-1 text-xs text-muted-foreground
    hover:text-foreground transition-colors
    focus:outline-none
  "
                aria-label="Copy server port"
              >
                {copied ? (
                  <Check className="h-3.5 w-3.5 text-emerald-500" />
                ) : (
                  <Network className="h-3.5 w-3.5" />
                )}
                <span>{port}</span>
              </button>
            </div>
          </div>
        </CardHeader>

        <CardContent>
          <div
            className="rounded-md border border-border p-3 [--motd-darken:0.0] dark:[--motd-darken:0.6]"
            style={{
              backgroundImage: `url("/deepslate.png")`,
              backgroundRepeat: "repeat",
              backgroundSize: "32px 32px",
              imageRendering: "pixelated",
              backgroundColor: "rgba(0,0,0,var(--motd-darken))",
              backgroundBlendMode: "multiply",
            }}
          >
            <div
              className="text-[14px]"
              style={{
                fontFamily:
                  "Minecraftia, ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace",
                textShadow: "0 1px 0 rgba(0,0,0,0.8)",
              }}
            >
              {renderMotd(motd ?? "")}
            </div>
          </div>
        </CardContent>
      </Card>
    </>
  );
}
