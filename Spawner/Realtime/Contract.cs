using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace Spawner.Realtime
{
	public static class Topics
	{
		public const string Servers = "servers";
		public static string Server(string id) => $"server:{id}";
		public static string Console(string id) => $"server:{id}:console";
		public static string Properties(string id) => $"server:{id}:properties";
		public static string Logs(string id) => $"server:{id}:logs";
		public static string Whitelist(string id) => $"server:{id}:whitelist";
	}

	public record ServerDto
	(
		string id,
		string name,
		string iconUrl,
		string version,
		string type,
		string status,
		int playersOnline,
		int playersMax,
		int port,
		string motd
	);

	public record ApiError(string code, string message, object? details = null);

	public record ErrorEnvelope(ApiError error);

	public record JobDto(string id, string type, string serverId, string state);

	public record Envelope(
		string type,
		string? eventId = null,
		string? topic = null,
		string? ts = null,
		object? payload = null,
		int? protocolVersion = null,
		string? sessionId = null,
		string? serverTime = null,
		string? lastEventId = null,
		bool? resume = null,
		string? requestId = null,
		string? name = null,
		object? args = null,
		ApiError? error = null
	);
}
