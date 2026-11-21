import { lazy, Suspense, useEffect, useState } from "react";
import { Route, Routes } from "react-router-dom";

import Layout from "./spawner-components/Layout";
import { startServersSync, stopServersSync } from "@/lib/serversSync";

import Login from './pages/Login';
import { getSession, logout, type Session } from '@/lib/auth';
import { Button } from '@/components/ui/button';

const Dashboard = lazy(() => import("./pages/Dashboard"));
const ArchivePage = lazy(() => import("./pages/Archive"));
const ServerPage = lazy(() => import("./pages/ServerPage"));

export default function App() {
  const [session, setSession] = useState<Session | null>(null);
  const [error, setError] = useState('');
  useEffect(() => {
    let active = true;
    const check = () => { void getSession().then(value => {
      if (active) setSession(previous => {
        if (previous?.authenticated && !value.authenticated) window.location.reload();
        return value;
      });
    }).catch(err => { if (active) setError(String(err.message)); }); };
    const expired = () => window.location.reload();
    check();
    const timer = window.setInterval(check, 60_000);
    window.addEventListener('spawner-session-expired', expired);
    return () => { active = false; clearInterval(timer); window.removeEventListener('spawner-session-expired', expired); };
  }, []);
  if (!session) return <main className="min-h-svh flex flex-col items-center justify-center gap-4"><p role="status">{error || 'Connecting to Spawner…'}</p>{error && <Button onClick={() => window.location.reload()}>Try again</Button>}</main>;
  if (!session.authenticated) return <Login onLogin={setSession} />;
  return <AuthenticatedApp username={session.username ?? ''} />;
}

function AuthenticatedApp({ username }: { username: string }) {
  const [error, setError] = useState('');
  useEffect(() => {
    startServersSync();
    return () => stopServersSync();
  }, []);

  return (
    <Layout>
      <div className="flex items-center justify-end gap-3 border-b px-6 py-3">
        {error && <span role="alert" className="text-sm text-destructive">{error}</span>}
        <span className="text-sm text-muted-foreground">{username}</span>
        <Button variant="outline" size="sm" onClick={() => { void logout().catch(err => setError(err.message)); }}>Sign out</Button>
      </div>
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
