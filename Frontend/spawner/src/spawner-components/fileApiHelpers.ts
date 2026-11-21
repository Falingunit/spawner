import { authFetch } from "@/lib/auth";

export function apiBaseUrlForBrowser() { return ""; }

export async function apiFetchBlob(path: string) {
  const res = await authFetch(`${apiBaseUrlForBrowser()}${path}`);
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  return res.blob();
}

