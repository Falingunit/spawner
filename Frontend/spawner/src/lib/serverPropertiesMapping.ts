import { SERVER_PROPERTIES } from "@/config/serverPropertiesSchema";
import type { ServerPropertiesState, ServerPropertyValue } from "@/lib/serverProperties";

type SchemaEntry =
  | { type: "boolean" }
  | { type: "number"; min?: number; max?: number }
  | { type: "string" }
  | { type: "select"; numeric: boolean };

const SCHEMA_BY_KEY: Record<string, SchemaEntry> = (() => {
  const out: Record<string, SchemaEntry> = {};
  for (const group of SERVER_PROPERTIES) {
    for (const prop of group.properties) {
      if (prop.type === "boolean") out[prop.key] = { type: "boolean" };
      else if (prop.type === "number") out[prop.key] = { type: "number", min: prop.min, max: prop.max };
      else if (prop.type === "select") {
        const numeric = prop.options?.some((o) => typeof o.value === "number") ?? false;
        out[prop.key] = { type: "select", numeric };
      } else out[prop.key] = { type: "string" };
    }
  }
  return out;
})();

function parseBoolean(raw: unknown): boolean | null {
  if (raw == null) return null;
  if (typeof raw === "boolean") return raw;
  if (typeof raw === "number") return raw !== 0;
  if (typeof raw === "string") {
    const s = raw.trim().toLowerCase();
    if (s === "true" || s === "1" || s === "yes" || s === "on") return true;
    if (s === "false" || s === "0" || s === "no" || s === "off") return false;
  }
  return null;
}

function parseNumber(raw: unknown): number | null {
  if (raw == null) return null;
  if (typeof raw === "number" && Number.isFinite(raw)) return raw;
  if (typeof raw === "string" && raw.trim() !== "") {
    const n = Number(raw);
    if (Number.isFinite(n)) return n;
  }
  return null;
}

function parseValue(key: string, raw: unknown): ServerPropertyValue {
  const schema = SCHEMA_BY_KEY[key];
  if (!schema) return raw == null ? null : (raw as ServerPropertyValue);

  if (schema.type === "boolean") {
    const b = parseBoolean(raw);
    return b ?? false;
  }

  if (schema.type === "number") {
    const n = parseNumber(raw);
    return n ?? null;
  }

  if (schema.type === "select") {
    if (raw == null) return "";
    if (schema.numeric) {
      const n = parseNumber(raw);
      return n ?? String(raw);
    }
    return typeof raw === "string" ? raw : String(raw);
  }

  if (raw == null) return "";
  return typeof raw === "string" ? raw : String(raw);
}

export function mapApiPropertiesToFormState(apiProps: Record<string, unknown>): ServerPropertiesState {
  const state: ServerPropertiesState = {};
  for (const [key, value] of Object.entries(apiProps ?? {})) {
    state[key] = parseValue(key, value);
  }
  return state;
}

export function mapFormStateToApiProperties(state: ServerPropertiesState): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(state)) {
    if (value === undefined) continue;
    out[key] = value;
  }
  return out;
}

