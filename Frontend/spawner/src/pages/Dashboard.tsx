import * as React from "react";

import MinecraftServerCard from "@/spawner-components/InstanceInfoCard";
import { CreateInstanceDialog } from "@/spawner-components/CreateInstanceDialog";
import { useServerStore } from "@/stores/serverStore";
import { useShallow } from "zustand/react/shallow";

export default function Dashboard() {
  const loaded = useServerStore((s) => s.loaded);
  const error = useServerStore((s) => s.error);
  const toggleServer = useServerStore((s) => s.toggleServer);
  const forceStopServer = useServerStore((s) => s.forceStopServer);
  const refreshServers = useServerStore((s) => s.refreshServers);
  const activeServerIds = useServerStore(
    useShallow((s) => s.servers.filter((server) => !server.archived).map((server) => server.id)),
  );

  return (
    <div className="mt-4 mx-auto w-full">
      <div className="mb-6 flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-3xl font-semibold">Dashboard</h1>
          <h2 className="mt-2 text-2xl">Available Instances</h2>
        </div>
        <CreateInstanceDialog onCreated={() => refreshServers()} />
      </div>

      {error ? (
        <div className="mb-6 rounded-md border border-border bg-muted/30 p-3 text-sm">
          Failed to load servers: <span className="font-mono">{error}</span>
        </div>
      ) : null}

      {loaded && activeServerIds.length === 0 && !error ? (
        <div className="mb-6 rounded-md border border-dashed border-border bg-muted/10 p-6 text-sm text-muted-foreground">
          No instances yet. Create one to get started.
        </div>
      ) : null}

      <div className="grid gap-6 grid-cols-[repeat(auto-fit,minmax(min(100%,420px),1fr))]">
        {activeServerIds.map((serverId) => (
          <DashboardServerCard
            key={serverId}
            serverId={serverId}
            onToggle={toggleServer}
            onForceStop={forceStopServer}
          />
        ))}
      </div>
    </div>
  );
}

const DashboardServerCard = React.memo(function DashboardServerCard({
  serverId,
  onToggle,
  onForceStop,
}: {
  serverId: string;
  onToggle: (id: string) => Promise<void>;
  onForceStop: (id: string) => Promise<void>;
}) {
  const server = useServerStore(
    React.useCallback((s) => s.servers.find((serverEntry) => serverEntry.id === serverId), [serverId]),
  );

  if (!server || server.archived) return null;

  return (
    <MinecraftServerCard
      {...server}
      linkTo={`/servers/${server.id}`}
      onToggle={() => onToggle(server.id)}
      onForceStop={() => onForceStop(server.id)}
    />
  );
});
