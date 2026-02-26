import { useServerStore } from "@/stores/serverStore";

export function startServersSync() {
  const store = useServerStore.getState();
  void store.init();
  store.connectRealtime();
}

export function stopServersSync() {
  useServerStore.getState().disconnectRealtime();
}

