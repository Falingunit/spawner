namespace Spawner
{
	public static class ServerPropertiesLoader
	{
		public static ServerProperties? Load(string instanceDirectory)
		{
			var path = Path.Combine(instanceDirectory, "server.properties");
			var dict = LoadProperties(path);

			if (dict.Count == 0)
				return null;

			var sp = new ServerProperties();

			/* ===================== NETWORKING / SECURITY ===================== */

			sp.AcceptsTransfers = GetBool(dict, "accepts-transfers", false);
			sp.EnableQuery = GetBool(dict, "enable-query", false);
			sp.EnableRcon = GetBool(dict, "enable-rcon", false);
			sp.EnableStatus = GetBool(dict, "enable-status", true);
			sp.OnlineMode = GetBool(dict, "online-mode", true);
			sp.PreventProxyConnections = GetBool(dict, "prevent-proxy-connections", false);
			sp.ServerIp = GetString(dict, "server-ip", "");
			sp.ServerPort = GetInt(dict, "server-port", 25565);
			sp.QueryPort = GetInt(dict, "query.port", 25565);
			sp.RconPassword = GetString(dict, "rcon.password", "");
			sp.RconPort = GetInt(dict, "rcon.port", 25575);
			sp.NetworkCompressionThreshold = GetInt(dict, "network-compression-threshold", 256);
			sp.RateLimit = GetInt(dict, "rate-limit", 0);

			/* ===================== GAMEPLAY ===================== */

			sp.DifficultySetting = ParseEnum(
				ServerProperties.Difficulty.Easy,
				GetString(dict, "difficulty", "easy")
			);

			sp.Gamemode = ParseEnum(
				ServerProperties.GameMode.Survival,
				GetString(dict, "gamemode", "survival")
			);

			sp.Hardcore = GetBool(dict, "hardcore", false);
			sp.ForceGamemode = GetBool(dict, "force-gamemode", false);
			sp.AllowFlight = GetBool(dict, "allow-flight", false);
			sp.GenerateStructures = GetBool(dict, "generate-structures", true);
			sp.SpawnProtection = GetInt(dict, "spawn-protection", 16);
			sp.PlayerIdleTimeout = GetInt(dict, "player-idle-timeout", 0);
			sp.MaxPlayers = GetInt(dict, "max-players", 20);
			sp.WhiteList = GetBool(dict, "white-list", false);
			sp.EnforceWhitelist = GetBool(dict, "enforce-whitelist", false);

			/* ===================== WORLD ===================== */

			sp.LevelName = GetString(dict, "level-name", "world");
			sp.LevelSeed = GetString(dict, "level-seed", "");
			sp.LevelTypeSetting = ParseEnum(
				ServerProperties.LevelType.Normal,
				StripNamespace(GetString(dict, "level-type", "minecraft:normal"))
			);
			sp.GeneratorSettings = GetString(dict, "generator-settings", "{}");
			sp.MaxWorldSize = GetInt(dict, "max-world-size", 29999984);
			sp.ViewDistance = GetInt(dict, "view-distance", 10);
			sp.SimulationDistance = GetInt(dict, "simulation-distance", 10);

			/* ===================== PERFORMANCE ===================== */

			sp.MaxTickTime = GetLong(dict, "max-tick-time", 60000);
			sp.MaxChainedNeighborUpdates = GetInt(dict, "max-chained-neighbor-updates", 1_000_000);
			sp.EntityBroadcastRangePercentage = GetInt(dict, "entity-broadcast-range-percentage", 100);
			sp.SyncChunkWrites = GetBool(dict, "sync-chunk-writes", true);
			sp.UseNativeTransport = GetBool(dict, "use-native-transport", true);
			sp.RegionFileCompressionSetting = ParseEnum(
				ServerProperties.RegionFileCompression.Deflate,
				GetString(dict, "region-file-compression", "deflate")
			);
			sp.PauseWhenEmptySeconds = GetInt(dict, "pause-when-empty-seconds", 60);

			/* ===================== CHAT / MODERATION ===================== */

			sp.EnforceSecureProfile = GetBool(dict, "enforce-secure-profile", true);
			sp.EnableCodeOfConduct = GetBool(dict, "enable-code-of-conduct", false);
			sp.TextFilteringConfig = GetString(dict, "text-filtering-config", "");
			sp.TextFilteringVersion = GetInt(dict, "text-filtering-version", 0);

			/* ===================== LOGGING / ADMIN ===================== */

			sp.LogIps = GetBool(dict, "log-ips", true);
			sp.BroadcastConsoleToOps = GetBool(dict, "broadcast-console-to-ops", true);
			sp.BroadcastRconToOps = GetBool(dict, "broadcast-rcon-to-ops", true);
			sp.OpPermissionLevel = GetInt(dict, "op-permission-level", 4);
			sp.FunctionPermissionLevel = GetInt(dict, "function-permission-level", 2);
			sp.Motd = GetString(dict, "motd", "A Minecraft Server");
			sp.BugReportLink = GetString(dict, "bug-report-link", "");
			sp.HideOnlinePlayers = GetBool(dict, "hide-online-players", false);

			/* ===================== RESOURCE PACK ===================== */

			sp.RequireResourcePack = GetBool(dict, "require-resource-pack", false);
			sp.ResourcePack = GetString(dict, "resource-pack", "");
			sp.ResourcePackId = GetString(dict, "resource-pack-id", "");
			sp.ResourcePackPrompt = GetString(dict, "resource-pack-prompt", "");
			sp.ResourcePackSha1 = GetString(dict, "resource-pack-sha1", "");
			sp.InitialEnabledPacks = GetString(dict, "initial-enabled-packs", "vanilla");
			sp.InitialDisabledPacks = GetString(dict, "initial-disabled-packs", "");

			/* ===================== MANAGEMENT SERVER ===================== */

			sp.ManagementServerEnabled = GetBool(dict, "management-server-enabled", false);
			sp.ManagementServerHost = GetString(dict, "management-server-host", "localhost");
			sp.ManagementServerPort = GetInt(dict, "management-server-port", 0);
			sp.ManagementServerSecret = GetString(dict, "management-server-secret", "");
			sp.ManagementServerTlsEnabled = GetBool(dict, "management-server-tls-enabled", true);
			sp.ManagementServerTlsKeystore = GetString(dict, "management-server-tls-keystore", "");
			sp.ManagementServerTlsKeystorePassword =
				GetString(dict, "management-server-tls-keystore-password", "");
			sp.StatusHeartbeatInterval = GetInt(dict, "status-heartbeat-interval", 0);

			return sp;
		}

		// Put this inside the SAME ServerPropertiesLoader class

		public static void Save(string instanceDirectory, ServerProperties sp)
		{
			var path = Path.Combine(instanceDirectory, "server.properties");

			var lines = new List<string>
	{
		"# Generated by Spawner",
		""
	};

			// NETWORKING / SECURITY
			lines.AddRange(new[]
			{
		$"accepts-transfers={ToBool(sp.AcceptsTransfers)}",
		$"enable-query={ToBool(sp.EnableQuery)}",
		$"enable-rcon={ToBool(sp.EnableRcon)}",
		$"enable-status={ToBool(sp.EnableStatus)}",
		$"online-mode={ToBool(sp.OnlineMode)}",
		$"prevent-proxy-connections={ToBool(sp.PreventProxyConnections)}",
		$"server-ip={sp.ServerIp ?? ""}",
		$"server-port={sp.ServerPort}",
		$"query.port={sp.QueryPort}",
		$"rcon.password={sp.RconPassword ?? ""}",
		$"rcon.port={sp.RconPort}",
		$"network-compression-threshold={sp.NetworkCompressionThreshold}",
		$"rate-limit={sp.RateLimit}",
		""
	});

			// GAMEPLAY
			lines.AddRange(new[]
			{
		$"difficulty={ToMcLower(sp.DifficultySetting)}",
		$"gamemode={ToMcLower(sp.Gamemode)}",
		$"hardcore={ToBool(sp.Hardcore)}",
		$"force-gamemode={ToBool(sp.ForceGamemode)}",
		$"allow-flight={ToBool(sp.AllowFlight)}",
		$"generate-structures={ToBool(sp.GenerateStructures)}",
		$"spawn-protection={sp.SpawnProtection}",
		$"player-idle-timeout={sp.PlayerIdleTimeout}",
		$"max-players={sp.MaxPlayers}",
		$"white-list={ToBool(sp.WhiteList)}",
		$"enforce-whitelist={ToBool(sp.EnforceWhitelist)}",
		""
	});

			// WORLD
			lines.AddRange(new[]
			{
		$"level-name={sp.LevelName ?? "world"}",
		$"level-seed={sp.LevelSeed ?? ""}",
		$"level-type=minecraft:{ToMcLower(sp.LevelTypeSetting)}",
		$"generator-settings={sp.GeneratorSettings ?? "{}"}",
		$"max-world-size={sp.MaxWorldSize}",
		$"view-distance={sp.ViewDistance}",
		$"simulation-distance={sp.SimulationDistance}",
		""
	});

			// PERFORMANCE
			lines.AddRange(new[]
			{
		$"max-tick-time={sp.MaxTickTime}",
		$"max-chained-neighbor-updates={sp.MaxChainedNeighborUpdates}",
		$"entity-broadcast-range-percentage={sp.EntityBroadcastRangePercentage}",
		$"sync-chunk-writes={ToBool(sp.SyncChunkWrites)}",
		$"use-native-transport={ToBool(sp.UseNativeTransport)}",
		$"region-file-compression={ToMcLower(sp.RegionFileCompressionSetting)}",
		$"pause-when-empty-seconds={sp.PauseWhenEmptySeconds}",
		""
	});

			// CHAT / MODERATION
			lines.AddRange(new[]
			{
		$"enforce-secure-profile={ToBool(sp.EnforceSecureProfile)}",
		$"enable-code-of-conduct={ToBool(sp.EnableCodeOfConduct)}",
		$"text-filtering-config={sp.TextFilteringConfig ?? ""}",
		$"text-filtering-version={sp.TextFilteringVersion}",
		""
	});

			// LOGGING / ADMIN
			lines.AddRange(new[]
			{
		$"log-ips={ToBool(sp.LogIps)}",
		$"broadcast-console-to-ops={ToBool(sp.BroadcastConsoleToOps)}",
		$"broadcast-rcon-to-ops={ToBool(sp.BroadcastRconToOps)}",
		$"op-permission-level={sp.OpPermissionLevel}",
		$"function-permission-level={sp.FunctionPermissionLevel}",
		$"motd={sp.Motd ?? "A Minecraft Server"}",
		$"bug-report-link={sp.BugReportLink ?? ""}",
		$"hide-online-players={ToBool(sp.HideOnlinePlayers)}",
		""
	});

			// RESOURCE PACK
			lines.AddRange(new[]
			{
		$"require-resource-pack={ToBool(sp.RequireResourcePack)}",
		$"resource-pack={sp.ResourcePack ?? ""}",
		$"resource-pack-id={sp.ResourcePackId ?? ""}",
		$"resource-pack-prompt={sp.ResourcePackPrompt ?? ""}",
		$"resource-pack-sha1={sp.ResourcePackSha1 ?? ""}",
		$"initial-enabled-packs={sp.InitialEnabledPacks ?? "vanilla"}",
		$"initial-disabled-packs={sp.InitialDisabledPacks ?? ""}",
		""
	});

			// MANAGEMENT SERVER
			lines.AddRange(new[]
			{
		$"management-server-enabled={ToBool(sp.ManagementServerEnabled)}",
		$"management-server-host={sp.ManagementServerHost ?? "localhost"}",
		$"management-server-port={sp.ManagementServerPort}",
		$"management-server-secret={sp.ManagementServerSecret ?? ""}",
		$"management-server-tls-enabled={ToBool(sp.ManagementServerTlsEnabled)}",
		$"management-server-tls-keystore={sp.ManagementServerTlsKeystore ?? ""}",
		$"management-server-tls-keystore-password={sp.ManagementServerTlsKeystorePassword ?? ""}",
		$"status-heartbeat-interval={sp.StatusHeartbeatInterval}",
		""
	});

			Directory.CreateDirectory(instanceDirectory);
			File.WriteAllLines(path, lines);
		}

		// Put these private helpers inside the SAME class too

		private static string ToBool(bool value) => value ? "true" : "false";

		private static string ToMcLower<T>(T value) where T : struct, Enum
		{
			// Enum name -> "lower_snake"
			return value.ToString()
				.Replace("LargeBiomes", "large_biomes") // special-case for your enum
				.Replace("SingleBiomeSurface", "single_biome_surface")
				.Replace("Lz4", "lz4")
				.ToLowerInvariant();
		}


		/* ===================== HELPERS ===================== */

		private static Dictionary<string, string> LoadProperties(string path)
		{
			var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

			if (!File.Exists(path))
				return dict;

			foreach (var line in File.ReadAllLines(path))
			{
				var trimmed = line.Trim();
				if (trimmed.Length == 0 || trimmed.StartsWith("#"))
					continue;

				var idx = trimmed.IndexOf('=');
				if (idx <= 0)
					continue;

				var key = trimmed[..idx].Trim();
				var value = trimmed[(idx + 1)..].Trim();
				dict[key] = value;
			}

			return dict;
		}

		private static string GetString(Dictionary<string, string> d, string k, string def)
			=> d.TryGetValue(k, out var v) ? v : def;

		private static bool GetBool(Dictionary<string, string> d, string k, bool def)
			=> d.TryGetValue(k, out var v) && bool.TryParse(v, out var b) ? b : def;

		private static int GetInt(Dictionary<string, string> d, string k, int def)
			=> d.TryGetValue(k, out var v) && int.TryParse(v, out var i) ? i : def;

		private static long GetLong(Dictionary<string, string> d, string k, long def)
			=> d.TryGetValue(k, out var v) && long.TryParse(v, out var l) ? l : def;

		private static T ParseEnum<T>(T def, string raw) where T : struct, Enum
		{
			var normalized = raw
				.ToUpperInvariant()
				.Replace('-', '_')
				.Replace(' ', '_');

			return Enum.TryParse(normalized, out T value) ? value : def;
		}

		private static string StripNamespace(string value)
		{
			var idx = value.IndexOf(':');
			return idx < 0 ? value : value[(idx + 1)..];
		}
	}
}
