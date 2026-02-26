import { SERVER_PROPERTIES } from "@/config/serverPropertiesSchema";

export type ServerPropertyValue = string | number | boolean | null;
export type ServerPropertiesState = Record<string, ServerPropertyValue>;

export function createDefaultProperties(): ServerPropertiesState {
  const state: ServerPropertiesState = {};
  for (const group of SERVER_PROPERTIES) {
    for (const prop of group.properties) {
      state[prop.key] = prop.default ?? null;
    }
  }
  return state;
}

