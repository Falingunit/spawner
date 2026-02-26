// src/types/server.ts
export type ServerStatus = "online" | "offline" | "starting" | "stopping" | "downloading";

export type ServerInitState = {
  state: "idle" | "downloading" | "ready" | "error" | string;
  stage?: string | null;
  fileName?: string | null;
  bytesReceived?: number;
  totalBytes?: number | null;
  percent?: number | null;
  message?: string | null;
};

export type Server = {
  id: string;
  name: string;
  iconUrl: string;
  version: string;
  type: string;
  status: ServerStatus;
  playersOnline: number;
  playersMax: number;
  port: number;
  motd: string;
  archived?: boolean;
  init?: ServerInitState;
};
