import MinecraftServerCard from "@/spawner-components/InstanceInfoCard";
import { CreateInstanceDialog } from "@/spawner-components/CreateInstanceDialog";
import { useServerStore } from "@/stores/serverStore";

export default function Dashboard() {
  const loaded = useServerStore((s) => s.loaded);
  const error = useServerStore((s) => s.error);
  const servers = useServerStore((s) => s.servers);
  const toggleServer = useServerStore((s) => s.toggleServer);
  const forceStopServer = useServerStore((s) => s.forceStopServer);
  const refreshServers = useServerStore((s) => s.refreshServers);
  const activeServers = servers.filter((s) => !s.archived);

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

      {loaded && activeServers.length === 0 && !error ? (
        <div className="mb-6 rounded-md border border-dashed border-border bg-muted/10 p-6 text-sm text-muted-foreground">
          No instances yet. Create one to get started.
        </div>
      ) : null}

      <div className="grid gap-6 grid-cols-[repeat(auto-fit,minmax(min(100%,420px),1fr))]">
        {activeServers.map((s) => (
          <MinecraftServerCard
            key={s.id}
            {...s}
            linkTo={`/servers/${s.id}`}
            onToggle={() => toggleServer(s.id)}
            onForceStop={() => forceStopServer(s.id)}
          />
        ))}
      </div>
    </div>
  );
}
