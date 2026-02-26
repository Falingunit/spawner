export function apiBaseUrlForBrowser() {
  const explicit = (import.meta.env.VITE_API_ORIGIN as string | undefined) ?? "";
  if (explicit) return explicit;

  try {
    const { protocol, hostname, port } = window.location;
    const isLocal = hostname === "localhost" || hostname === "127.0.0.1";
    if (isLocal && protocol.startsWith("http") && port && port !== "5000") {
      return `${protocol}//${hostname}:5000`;
    }
  } catch {
    // ignore
  }

  return "";
}

export async function apiFetchBlob(path: string) {
  const res = await fetch(`${apiBaseUrlForBrowser()}${path}`);
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  return res.blob();
}

