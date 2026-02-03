using Microsoft.AspNetCore.Mvc;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Spawner.Realtime;

namespace Spawner.Controllers;

[ApiController]
[Route("ws/v1")]
public sealed class WsV1Controller : ControllerBase
{
	private readonly EventBus _bus;
	private readonly InstanceManager _manager;

	public WsV1Controller(EventBus bus, InstanceManager manager)
	{
		_bus = bus;
		_manager = manager;
	}

	[HttpGet]
	public async Task Get(CancellationToken ct)
	{
		if (!HttpContext.WebSockets.IsWebSocketRequest)
		{
			HttpContext.Response.StatusCode = 400;
			return;
		}

		using var ws = await HttpContext.WebSockets.AcceptWebSocketAsync();
		WsClient? client = null;
		var sendLoopCts = new CancellationTokenSource();

		try
		{
			// First message must be hello
			var helloJson = await ReceiveTextAsync(ws, ct);
			if (helloJson is null) return;
			var hello = JsonSerializer.Deserialize<HelloMsg>(helloJson)
						?? throw new Exception("Invalid hello");

			if (hello.type != "hello" || hello.protocolVersion != 1)
			{
				await SendAsync(ws, JsonSerializer.Serialize(new Envelope(
					type: "hello_ack",
					protocolVersion: 1,
					resume: false,
					serverTime: DateTime.UtcNow.ToString("O"),
					lastEventId: _bus.LastEventId
				)), ct);
				await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Bad protocol", ct);
				return;
			}

			client = new WsClient(hello.clientId ?? Guid.NewGuid().ToString(), ws);
			_bus.Register(client);

			// start sender loop
			_ = Task.Run(async () =>
			{
				try { await client.RunSendLoopAsync(sendLoopCts.Token); }
				catch (OperationCanceledException) { }
				catch (WebSocketException) { }
			});

			// subscriptions
			var topics = hello.subscriptions?.Select(s => s.topic).Where(t => t != null).Cast<string>().ToList()
						 ?? new List<string>();
			client.Subscribe(topics);

			bool resumed = false;
			if (!string.IsNullOrWhiteSpace(hello.resumeFromEventId))
			{
				var missed = _bus.TryGetSince(hello.resumeFromEventId!);
				if (missed != null)
				{
					resumed = true;
					foreach (var evt in missed)
					{
						// only send if the client is subscribed to that topic
						if (evt.topic != null && client.IsSubscribed(evt.topic))
							await client.SendAsync(JsonSerializer.Serialize(evt));
					}
				}
			}

			// hello_ack
			await client.SendAsync(JsonSerializer.Serialize(new Envelope(
				type: "hello_ack",
				protocolVersion: 1,
				sessionId: client.SessionId,
				serverTime: DateTime.UtcNow.ToString("O"),
				lastEventId: _bus.LastEventId,
				resume: resumed
			)));

			_ = Task.Run(async () =>
			{
				try
				{
					while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
					{
						await Task.Delay(TimeSpan.FromSeconds(15), ct);
						var ping = new Envelope(type: "ping", ts: DateTime.UtcNow.ToString("O"));
						await client!.SendAsync(JsonSerializer.Serialize(ping));
					}
				}
				catch (OperationCanceledException) { }
				catch (WebSocketException) { }
			}, ct);

			// Always send a fresh snapshot for critical topics.
			// Even if the client requests resume, it may be reconnecting with an empty local state (e.g. page reload).
			foreach (var t in topics)
			{
				if (t == Topics.Servers)
				{
					var env = _bus.StoreOnly(Topics.Servers, BuildServersSnapshot());
					await client.SendAsync(JsonSerializer.Serialize(env));
				}
			}

			// Main receive loop for subscribe/unsubscribe/cmd/pong
			while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
			{
				var json = await ReceiveTextAsync(ws, ct);
				if (json is null) break;
				var baseMsg = JsonSerializer.Deserialize<BaseMsg>(json);
				if (baseMsg == null) continue;

				switch (baseMsg.type)
				{
					case "subscribe":
						{
							var msg = JsonSerializer.Deserialize<SubscribeMsg>(json)!;
							client.Subscribe(msg.topics ?? Array.Empty<string>());
							// send snapshot if subscribing to servers
							if (msg.topics?.Contains(Topics.Servers) == true)
							{
								var env = _bus.StoreOnly(Topics.Servers, BuildServersSnapshot());
								await client.SendAsync(JsonSerializer.Serialize(env));
							}
							break;
						}
					case "unsubscribe":
						{
							var msg = JsonSerializer.Deserialize<UnsubscribeMsg>(json)!;
							client.Unsubscribe(msg.topics ?? Array.Empty<string>());
							break;
						}
					case "cmd":
						{
							var msg = JsonSerializer.Deserialize<CmdMsg>(json)!;
							await HandleCmdAsync(client, msg);
							break;
						}
					case "pong":
						break;
				}
			}
		}
		catch (OperationCanceledException)
		{
			// normal on client disconnect / request abort
		}
		catch (WebSocketException)
		{
			// normal on client disconnect
		}
		finally
		{
			sendLoopCts.Cancel();
			if (client != null) _bus.Unregister(client.ClientId);
		}
	}

	private object BuildServersSnapshot()
	{
		var servers = _manager.GetAllInstanceProperties()
			.Select(p =>
			{
				var init = _manager.GetInitStatus(p.InstanceID);
				var status = _manager.GetInstanceStatus(p.InstanceID);
				var props = _manager.GetInstanceServerProperties(p.InstanceID);
				var statusStr = status.ToString().ToLowerInvariant() != "offline"
					? status.ToString().ToLowerInvariant()
					: (init.state == "downloading" ? "downloading" : "offline");
				return new
				{
					id = p.InstanceID,
					name = p.InstanceName,
					iconUrl = $"/api/v1/servers/{p.InstanceID}/icon",
					version = p.GameVersion,
					type = p.InstanceType.ToString().ToLowerInvariant(),
					status = statusStr,
					playersOnline = _manager.GetInstancePlayerCount(p.InstanceID),
					playersMax = props?.MaxPlayers ?? 0,
					port = props?.ServerPort ?? 0,
					motd = props?.Motd ?? "",
					archived = p.IsArchived,
					init
				};
			}).ToList();

		return new { kind = "snapshot", servers };
	}

	private async Task HandleCmdAsync(WsClient client, CmdMsg msg)
	{
		try
		{
			switch (msg.name)
			{
				case "server.start":
					_manager.StartInstance((string)msg.args!["serverId"]!);
					await client.SendAsync(JsonSerializer.Serialize(new Envelope(type: "cmd_ack", requestId: msg.requestId)));
					break;

				case "server.stop":
					await _manager.StopInstance((string)msg.args!["serverId"]!);
					await client.SendAsync(JsonSerializer.Serialize(new Envelope(type: "cmd_ack", requestId: msg.requestId)));
					break;

				case "console.send":
					_manager.SendCommandToInstance((string)msg.args!["serverId"]!, (string)msg.args!["command"]!);
					await client.SendAsync(JsonSerializer.Serialize(new Envelope(type: "cmd_ack", requestId: msg.requestId)));
					break;

				default:
					await client.SendAsync(JsonSerializer.Serialize(new Envelope(
						type: "cmd_error",
						requestId: msg.requestId,
						error: new ApiError("unknown_cmd", "Unknown cmd")
					)));
					break;
			}
		}
		catch (Exception ex)
		{
			await client.SendAsync(JsonSerializer.Serialize(new Envelope(
				type: "cmd_error",
				requestId: msg.requestId,
				error: new ApiError("cmd_failed", ex.Message)
			)));
		}
	}

	internal static async Task<string?> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
	{
		var buffer = new byte[4096];
		using var ms = new MemoryStream();

		while (true)
		{
			WebSocketReceiveResult res;
			try
			{
				res = await ws.ReceiveAsync(buffer, ct);
			}
			catch (OperationCanceledException)
			{
				return null;
			}
			catch (WebSocketException)
			{
				return null;
			}

			if (res.MessageType == WebSocketMessageType.Close)
				return null;

			ms.Write(buffer, 0, res.Count);
			if (res.EndOfMessage) break;

			if (ms.Length > 64 * 1024)
				throw new Exception("Message too big");
		}

		return Encoding.UTF8.GetString(ms.ToArray());
	}

	private static Task SendAsync(WebSocket ws, string json, CancellationToken ct)
	{
		var bytes = Encoding.UTF8.GetBytes(json);
		return ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
	}

	// message DTOs
	private record BaseMsg(string type);
	private record HelloMsg(string type, int protocolVersion, string? clientId, string? resumeFromEventId, Sub[]? subscriptions);
	private record Sub(string topic);
	private record SubscribeMsg(string type, string[]? topics);
	private record UnsubscribeMsg(string type, string[]? topics);
	private record CmdMsg(string type, string requestId, string name, Dictionary<string, object>? args);
}
