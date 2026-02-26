import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { DatapacksTab } from "@/spawner-components/DatapacksTab";
import { ModsTab } from "@/spawner-components/ModsTab";

export function ContentTab({
  serverId,
  serverVersion,
  serverStatus,
  serverType,
}: {
  serverId: string;
  serverVersion: string;
  serverStatus: string;
  serverType: string;
}) {
  const showMods = serverType !== "vanilla";
  const defaultTab = showMods ? "mods" : "datapacks";

  return (
    <Tabs defaultValue={defaultTab}>
      <TabsList className="w-full justify-start">
        {showMods ? <TabsTrigger value="mods">Mods</TabsTrigger> : null}
        <TabsTrigger value="datapacks">Data Packs</TabsTrigger>
      </TabsList>

      {showMods ? (
        <TabsContent value="mods" className="mt-4">
          <ModsTab serverId={serverId} serverVersion={serverVersion} serverStatus={serverStatus} />
        </TabsContent>
      ) : null}

      <TabsContent value="datapacks" className="mt-4">
        <DatapacksTab serverId={serverId} serverVersion={serverVersion} serverStatus={serverStatus} />
      </TabsContent>
    </Tabs>
  );
}
