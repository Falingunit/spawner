import { useState, type FormEvent } from 'react';
import { LockKeyhole, LoaderCircle } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { login, type Session } from '@/lib/auth';

export default function Login({ onLogin }: { onLogin: (session: Session) => void }) {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError('');
    try { onLogin(await login(username, password)); }
    catch (err) { setError(err instanceof Error ? err.message : 'Sign-in failed.'); }
    finally { setBusy(false); setPassword(''); }
  }
  return <main className="min-h-svh bg-background flex items-center justify-center p-6">
    <section className="w-full max-w-sm rounded-2xl border bg-card p-8 shadow-xl" aria-labelledby="login-title">
      <img src="/spawner.png" alt="" className="mx-auto mb-6 h-16 w-16 object-contain" />
      <h1 id="login-title" className="text-center text-2xl font-semibold tracking-tight">Sign in to Spawner</h1>
      <p className="mt-2 text-center text-sm text-muted-foreground">Manage your Minecraft servers.</p>
      <form onSubmit={submit} className="mt-8 space-y-5">
        <div className="space-y-2"><Label htmlFor="username">Username</Label>
          <Input id="username" name="username" autoComplete="username" autoFocus required maxLength={128} value={username} onChange={e => setUsername(e.target.value)} disabled={busy} /></div>
        <div className="space-y-2"><Label htmlFor="password">Password</Label>
          <Input id="password" name="password" type="password" autoComplete="current-password" required maxLength={1024} value={password} onChange={e => setPassword(e.target.value)} disabled={busy} /></div>
        {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
        <Button type="submit" className="w-full" disabled={busy}>{busy ? <LoaderCircle className="animate-spin" /> : <LockKeyhole />} {busy ? 'Signing in…' : 'Sign in'}</Button>
      </form>
      <p className="mt-6 text-center text-xs text-muted-foreground">Administrator access only. Contact your host if you need help signing in.</p>
    </section>
  </main>;
}
