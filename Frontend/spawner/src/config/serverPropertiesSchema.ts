export type PropertyType = "boolean" | "string" | "number" | "select";

export type ServerProperty = {
  key: string;
  type: PropertyType;
  default: string | number | boolean;
  description: string;
  min?: number;
  max?: number;
  options?: { label: string; value: string | number }[];
};

export type PropertyGroup = {
  id: string;
  title: string;
  properties: ServerProperty[];
};

export const SERVER_PROPERTIES: PropertyGroup[] = [
  {
    id: "general",
    title: "General",
    properties: [
      {
        key: "accepts-transfers",
        type: "boolean",
        default: false,
        description:
          "Whether to accept incoming transfers via a transfer packet. If false, transfers are rejected and the player is disconnected.",
      },
      {
        key: "allow-flight",
        type: "boolean",
        default: false,
        description:
          "Allows players to fly in Survival using mods. May increase griefing risk. No effect in Creative.",
      },
      {
        key: "broadcast-console-to-ops",
        type: "boolean",
        default: true,
        description: "Sends console command output to all online operators.",
      },
      {
        key: "broadcast-rcon-to-ops",
        type: "boolean",
        default: true,
        description: "Sends RCON command output to all online operators.",
      },
      {
        key: "bug-report-link",
        type: "string",
        default: "",
        description:
          "URL for the report_bug server link. Not sent if empty.",
      },
      {
        key: "difficulty",
        type: "select",
        default: "easy",
        options: [
          { label: "Peaceful", value: "peaceful" },
          { label: "Easy", value: "easy" },
          { label: "Normal", value: "normal" },
          { label: "Hard", value: "hard" },
        ],
        description:
          "Server difficulty: peaceful (0), easy (1), normal (2), hard (3).",
      },
      {
        key: "enable-code-of-conduct",
        type: "boolean",
        default: false,
        description:
          "Enables per-language code of conduct files from server.properties/codeofconduct.",
      },
      {
        key: "enable-jmx-monitoring",
        type: "boolean",
        default: false,
        description:
          "Exposes JMX MBeans for tick timing (requires JVM flags).",
      },
      { key: "enable-query", type: "boolean", default: false, description: "Enables GameSpy-style query protocol." },
      { key: "enable-rcon", type: "boolean", default: false, description: "Enables remote console access (unencrypted)." },
      { key: "enable-status", type: "boolean", default: true, description: "Controls whether the server appears online in the server list." },
      { key: "enforce-secure-profile", type: "boolean", default: true, description: "Requires Mojang-signed public keys; enables reportable chat." },
      { key: "enforce-whitelist", type: "boolean", default: false, description: "Kicks non-whitelisted players when whitelist is enforced." },
      {
        key: "entity-broadcast-range-percentage",
        type: "number",
        default: 100,
        min: 10,
        max: 1000,
        description:
          "Percentage of normal entity render distance sent to clients.",
      },
      { key: "force-gamemode", type: "boolean", default: false, description: "Forces players into the default gamemode on join." },
      { key: "function-permission-level", type: "number", default: 2, min: 1, max: 4, description: "Default permission level for functions." },
      {
        key: "gamemode",
        type: "select",
        default: "survival",
        options: [
          { label: "Survival", value: "survival" },
          { label: "Creative", value: "creative" },
          { label: "Adventure", value: "adventure" },
          { label: "Spectator", value: "spectator" },
        ],
        description:
          "Default gamemode (survival, creative, adventure, spectator).",
      },
      { key: "generate-structures", type: "boolean", default: true, description: "Enables structure generation (villages, etc.)." },
      { key: "generator-settings", type: "string", default: "{}", description: "Custom world generation settings." },
      { key: "hardcore", type: "boolean", default: false, description: "Enables hardcore mode for newly created worlds." },
      { key: "hide-online-players", type: "boolean", default: false, description: "Hides player list from server status responses." },
      { key: "initial-disabled-packs", type: "string", default: "", description: "Datapacks not auto-enabled on world creation." },
      { key: "initial-enabled-packs", type: "string", default: "vanilla", description: "Datapacks enabled on world creation." },
      { key: "level-name", type: "string", default: "world", description: "World folder name or path." },
      { key: "level-seed", type: "string", default: "", description: "World seed (random if empty)." },
      {
        key: "level-type",
        type: "select",
        default: "minecraft:normal",
        options: [
          { label: "Normal", value: "minecraft:normal" },
          { label: "Flat", value: "minecraft:flat" },
          { label: "Large Biomes", value: "minecraft:large_biomes" },
          { label: "Amplified", value: "minecraft:amplified" },
          { label: "Single Biome", value: "minecraft:single_biome_surface" },
        ],
        description:
          "World preset (normal, flat, large_biomes, amplified, single_biome_surface).",
      },
      { key: "log-ips", type: "boolean", default: true, description: "Logs client IP addresses." },
      { key: "management-server-enabled", type: "boolean", default: false, description: "Enables Minecraft Server Management Protocol." },
      { key: "management-server-host", type: "string", default: "localhost", description: "Host for management server." },
      { key: "management-server-port", type: "number", default: 0, description: "Port for management server." },
      { key: "management-server-secret", type: "string", default: "", description: "Authorization secret (auto-generated if empty)." },
      { key: "management-server-tls-enabled", type: "boolean", default: true, description: "Enables TLS for management server." },
      { key: "management-server-tls-keystore", type: "string", default: "", description: "Path to TLS keystore file." },
      { key: "management-server-tls-keystore-password", type: "string", default: "", description: "Keystore password." },
      { key: "max-chained-neighbor-updates", type: "number", default: 1000000, description: "Max consecutive neighbor updates before skipping." },
      { key: "max-players", type: "number", default: 20, description: "Maximum concurrent players." },
      { key: "max-tick-time", type: "number", default: 60000, description: "Watchdog timeout per tick in ms (-1 disables)." },
      { key: "max-world-size", type: "number", default: 29999984, description: "Maximum world border radius." },
      { key: "motd", type: "string", default: "A Minecraft Server", description: "Server list message (supports formatting)." },
      { key: "network-compression-threshold", type: "number", default: 256, description: "Packet size threshold for compression." },
      { key: "online-mode", type: "boolean", default: true, description: "Verifies players with Mojang authentication servers." },
      { key: "op-permission-level", type: "number", default: 4, min: 0, max: 4, description: "Default operator permission level." },
      { key: "pause-when-empty-seconds", type: "number", default: 60, description: "Time before server pauses after last player leaves." },
      { key: "player-idle-timeout", type: "number", default: 0, description: "Minutes before idle players are kicked (0 = never)." },
      { key: "prevent-proxy-connections", type: "boolean", default: false, description: "Kicks players using mismatched proxy authentication." },
      { key: "server-port", type: "number", default: 25565, min: 1, max: 65535, description: "Server listening port." },
      { key: "view-distance", type: "number", default: 10, min: 2, max: 32, description: "Maximum distance (in chunks) sent to clients." },
      { key: "white-list", type: "boolean", default: false, description: "Enables the whitelist." },
    ],
  },
];
