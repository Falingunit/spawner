import * as React from "react";
import { Link, useLocation } from "react-router-dom";

import {
  Sidebar,
  SidebarContent,
  SidebarGroup,
  SidebarHeader,
  SidebarMenu,
  SidebarMenuAction,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarRail,
} from "@/components/ui/sidebar";
import { Archive, LayoutDashboard, Users, Network, Check } from "lucide-react";

import { useServerStore } from "@/stores/serverStore";
import type { ServerStatus } from "@/types/server";
import { useShallow } from "zustand/react/shallow";

const STATUS_COLOR: Record<ServerStatus, string> = {
  online: "bg-emerald-500",
  starting: "bg-amber-400",
  stopping: "bg-amber-400",
  downloading: "bg-amber-400",
  offline: "bg-muted-foreground",
};

export function AppSidebar() {
  const loaded = useServerStore((s) => s.loaded);
  const archivedCount = useServerStore((s) => s.servers.filter((server) => server.archived).length);
  const onlineIds = useServerStore(
    useShallow((s) => s.servers.filter((server) => !server.archived && server.status !== "offline").map((server) => server.id)),
  );
  const offlineIds = useServerStore(
    useShallow((s) => s.servers.filter((server) => !server.archived && server.status === "offline").map((server) => server.id)),
  );

  return (
    <Sidebar collapsible="icon">
      <SidebarHeader className="px-2 py-2">
        <Link
          to="/"
          className={[
            "flex h-11 items-center rounded-md px-3",
            "group-data-[collapsible=icon]:justify-center group-data-[collapsible=icon]:px-0",
          ].join(" ")}
          aria-label="Spawner"
          title="Spawner"
        >
          <img src="/spawner.png" alt="" className="h-7 w-7 object-contain" />
          <span className="ml-2 text-sm font-semibold leading-none group-data-[collapsible=icon]:hidden">
            Spawner
          </span>
        </Link>
      </SidebarHeader>

      <SidebarContent className="overflow-y-auto">
        <SidebarGroup>
          <SidebarMenu>
            <SidebarMenuItem className="relative">
              <MenuLink
                to="/"
                tooltip="Dashboard"
                icon={<LayoutDashboard className="h-5 w-5" />}
                label="Dashboard"
              />
            </SidebarMenuItem>
            <SidebarMenuItem className="relative">
              <MenuLink
                to="/archive"
                tooltip="Archive"
                icon={<Archive className="h-5 w-5" />}
                label={archivedCount > 0 ? `Archive (${archivedCount})` : "Archive"}
              />
            </SidebarMenuItem>
          </SidebarMenu>

          <SidebarDivider />

          <SidebarMenu>
            {onlineIds.length === 0 ? (
              <SidebarEmptyText text={loaded ? "No active instances" : "Loading instances..."} />
            ) : (
              onlineIds.map((serverId) => (
                <ServerMenuItem key={serverId} serverId={serverId} />
              ))
            )}
          </SidebarMenu>

          <SidebarDivider />

          <SidebarMenu>
            {offlineIds.length === 0 ? (
              <SidebarEmptyText text={loaded ? "No stopped instances" : "Loading instances..."} />
            ) : (
              offlineIds.map((serverId) => (
                <ServerMenuItem key={serverId} serverId={serverId} />
              ))
            )}
          </SidebarMenu>
        </SidebarGroup>
      </SidebarContent>

      <SidebarRail />
    </Sidebar>
  );
}

function SidebarEmptyText({ text }: { text: string }) {
  return (
    <SidebarMenuItem className="px-3 py-2 text-xs text-muted-foreground group-data-[collapsible=icon]:hidden">
      {text}
    </SidebarMenuItem>
  );
}

function MenuLink({
  to,
  tooltip,
  icon,
  label,
}: {
  to: string;
  tooltip: string;
  icon: React.ReactNode;
  label: string;
}) {
  const { pathname } = useLocation();
  const active = pathname === to;

  return (
    <SidebarMenuButton
      asChild
      isActive={active}
      tooltip={tooltip}
      size="lg"
      className="h-11 px-3 py-2 group-data-[collapsible=icon]:justify-center group-data-[collapsible=icon]:px-0"
    >
      <Link to={to} className="flex w-full items-center gap-3">
        <span className="shrink-0">{icon}</span>
        <span className="group-data-[collapsible=icon]:hidden">{label}</span>
      </Link>
    </SidebarMenuButton>
  );
}

function ServerMenuItem({ serverId }: { serverId: string }) {
  const server = useServerStore(
    React.useCallback((s) => s.servers.find((serverEntry) => serverEntry.id === serverId), [serverId]),
  );
  const location = useLocation();
  const active = location.pathname === `/servers/${serverId}`;
  const [copied, setCopied] = React.useState(false);

  if (!server) return null;
  const serverPort = server.port;

  async function copyPort(e: React.MouseEvent) {
    e.preventDefault();
    e.stopPropagation();
    try {
      await navigator.clipboard.writeText(String(serverPort));
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1200);
    } catch {
      // ignore
    }
  }

  const playersLeft = server.status === "online" ? String(server.playersOnline) : "—";

  return (
    <SidebarMenuItem className="relative">
      <SidebarMenuButton
        asChild
        isActive={active}
        tooltip={server.name}
        size="lg"
        className="h-11 px-3 py-2 pr-10 group-data-[collapsible=icon]:justify-center group-data-[collapsible=icon]:px-0 group-data-[collapsible=icon]:pr-0"
      >
        <Link to={`/servers/${server.id}`} className="flex w-full items-center gap-3">
          <img
            src={server.iconUrl}
            alt=""
            className="h-6 w-6 rounded-sm object-cover"
            onError={(e) => {
              e.currentTarget.src = "/spawner.png";
            }}
          />

          <div className="min-w-0 flex-1 group-data-[collapsible=icon]:hidden">
            <div className="flex items-center gap-2">
              <span className="truncate text-sm">{server.name}</span>
              <span className={`h-2 w-2 rounded-full ${STATUS_COLOR[server.status]}`} />
            </div>

            <div className="mt-0.5 flex items-center gap-1 text-xs text-muted-foreground">
              <Users className="h-3 w-3" />
              {playersLeft}/{server.playersMax}
            </div>
          </div>
        </Link>
      </SidebarMenuButton>

      <SidebarMenuAction
        onClick={copyPort}
        className="group-data-[collapsible=icon]:hidden"
        title="Copy port"
        aria-label="Copy port"
      >
        {copied ? (
          <Check className="h-4 w-4 text-emerald-500" />
                ) : (
                  <Network className="h-4 w-4" />
                )}
              </SidebarMenuAction>
    </SidebarMenuItem>
  );
}

function SidebarDivider() {
  return (
    <div className="my-2 h-px bg-border mx-2 group-data-[collapsible=icon]:mx-auto group-data-[collapsible=icon]:w-5" />
  );
}
