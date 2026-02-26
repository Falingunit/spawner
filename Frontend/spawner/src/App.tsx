import { lazy, Suspense, useEffect } from "react";
import { Route, Routes } from "react-router-dom";

import Layout from "./spawner-components/Layout";
import { startServersSync, stopServersSync } from "@/lib/serversSync";

const Dashboard = lazy(() => import("./pages/Dashboard"));
const ArchivePage = lazy(() => import("./pages/Archive"));
const ServerPage = lazy(() => import("./pages/ServerPage"));

export default function App() {
  useEffect(() => {
    startServersSync();
    return () => stopServersSync();
  }, []);

  return (
    <Layout>
      <Suspense fallback={<div className="px-6 py-6 text-sm text-muted-foreground">Loading...</div>}>
        <Routes>
          <Route path="/" element={<Dashboard />} />
          <Route path="/archive" element={<ArchivePage />} />
          <Route path="/servers/:id" element={<ServerPage />} />
        </Routes>
      </Suspense>
    </Layout>
  );
}
