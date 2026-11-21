export type Session = { authenticated: boolean; username?: string; csrfToken: string };
let csrfToken = "";
let pendingSession: Promise<Session> | undefined;

export function getSession(): Promise<Session> {
  if (!pendingSession) {
    pendingSession = fetch('/api/v1/auth/session', { credentials: 'same-origin', cache: 'no-store' })
      .then(async res => {
        if (!res.ok) throw new Error('Unable to connect to Spawner. Please try again.');
        const session = await res.json() as Session;
        csrfToken = session.csrfToken;
        return session;
      }).finally(() => { pendingSession = undefined; });
  }
  return pendingSession;
}

export async function authFetch(input: string, init?: RequestInit) {
  const url = new URL(input, window.location.origin);
  if (url.origin !== window.location.origin) throw new Error('Spawner requires a same-origin API.');
  const headers = new Headers(init?.headers);
  if (!['GET', 'HEAD', 'OPTIONS'].includes((init?.method ?? 'GET').toUpperCase())) {
    if (!csrfToken) await getSession();
    headers.set('X-CSRF-Token', csrfToken);
  }
  const res = await fetch(input, { ...init, headers, credentials: 'same-origin', cache: 'no-store' });
  if (res.status === 401 && url.pathname !== '/api/v1/auth/login') {
    csrfToken = '';
    window.dispatchEvent(new Event('spawner-session-expired'));
  }
  return res;
}

export async function login(username: string, password: string) {
  // Refresh the anonymous token, including after another tab logs out.
  await getSession();
  const res = await authFetch('/api/v1/auth/login', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ username, password }),
  });
  if (!res.ok) throw new Error(res.status === 429 ? 'Too many sign-in attempts. Wait a minute and try again.' :
    res.status === 401 ? 'Invalid username or password.' : 'Sign-in failed. Please try again.');
  return getSession();
}

export async function logout() {
  await getSession();
  const res = await authFetch('/api/v1/auth/logout', { method: 'POST' });
  if (!res.ok && res.status !== 401) throw new Error('Sign-out failed. Please try again.');
  csrfToken = '';
  // Reload clears server data, console history, and outstanding connections from memory.
  window.location.reload();
}
