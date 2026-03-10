import * as React from "react";

import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";

const ModsTab = React.lazy(async () => {
  const mod = await import("@/spawner-components/ModsTab");
  return { default: mod.ModsTab };
});

const DatapacksTab = React.lazy(async () => {
  const mod = await import("@/spawner-components/DatapacksTab");
  return { default: mod.DatapacksTab };
});

type ContentTabKey = "mods" | "datapacks";

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
  const defaultTab: ContentTabKey = showMods ? "mods" : "datapacks";
  const [activeTab, setActiveTab] = React.useState<ContentTabKey>(defaultTab);
  const [loadedTabs, setLoadedTabs] = React.useState<Set<ContentTabKey>>(() => new Set([defaultTab]));

  React.useEffect(() => {
    setActiveTab(defaultTab);
    setLoadedTabs(new Set([defaultTab]));
  }, [defaultTab]);

  React.useEffect(() => {
    setLoadedTabs((prev) => {
      if (prev.has(activeTab)) return prev;
      const next = new Set(prev);
      next.add(activeTab);
      return next;
    });
  }, [activeTab]);

  return (
    <Tabs value={activeTab} onValueChange={(value) => setActiveTab(value as ContentTabKey)}>
      <TabsList className="w-full justify-start">
        {showMods ? <TabsTrigger value="mods">Mods</TabsTrigger> : null}
        <TabsTrigger value="datapacks">Data Packs</TabsTrigger>
      </TabsList>

      {showMods && loadedTabs.has("mods") ? (
        <TabsContent value="mods" forceMount className={activeTab === "mods" ? "mt-4" : "hidden"}>
          <React.Suspense fallback={<div className="text-sm text-muted-foreground">Loading mods...</div>}>
            <ModsTab serverId={serverId} serverVersion={serverVersion} serverStatus={serverStatus} />
          </React.Suspense>
        </TabsContent>
      ) : null}

      {loadedTabs.has("datapacks") ? (
        <TabsContent value="datapacks" forceMount className={activeTab === "datapacks" ? "mt-4" : "hidden"}>
          <React.Suspense fallback={<div className="text-sm text-muted-foreground">Loading data packs...</div>}>
            <DatapacksTab serverId={serverId} serverVersion={serverVersion} serverStatus={serverStatus} />
          </React.Suspense>
        </TabsContent>
      ) : null}
    </Tabs>
  );
}
