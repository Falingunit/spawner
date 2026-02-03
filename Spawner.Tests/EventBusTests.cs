using Spawner.Realtime;
using System.Net.WebSockets;
using System.Text;

namespace Spawner.Tests;

public sealed class EventBusTests
{
	[Fact]
	public async Task StoreOnly_DoesNotBroadcast()
	{
		var bus = new EventBus();
		var ws = new TestWebSocket();
		var client = new WsClient("c1", ws);
		client.Subscribe(new[] { Topics.Servers });
		bus.Register(client);

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var sendLoop = Task.Run(() => client.RunSendLoopAsync(cts.Token));

		bus.StoreOnly(Topics.Servers, new { kind = "snapshot", servers = Array.Empty<object>() });
		await Task.Delay(50);

		Assert.Empty(ws.SentMessages);

		bus.Publish(Topics.Servers, new { kind = "snapshot", servers = Array.Empty<object>() });
		await Task.Delay(50);

		Assert.NotEmpty(ws.SentMessages);

		cts.Cancel();
		await Task.WhenAny(sendLoop, Task.Delay(1000));
	}

	[Fact]
	public async Task ReceiveTextAsync_ReturnsNullOnClose()
	{
		using var ws = new CloseOnReceiveWebSocket();
		var txt = await Spawner.Controllers.WsV1Controller.ReceiveTextAsync(ws, CancellationToken.None);
		Assert.Null(txt);
	}

	private sealed class TestWebSocket : WebSocket
	{
		private readonly object _lock = new();
		public List<string> SentMessages { get; } = new();

		public override WebSocketCloseStatus? CloseStatus => null;
		public override string? CloseStatusDescription => null;
		public override WebSocketState State => WebSocketState.Open;
		public override string SubProtocol => "";

		public override void Abort() { }

		public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
			Task.CompletedTask;

		public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
			Task.CompletedTask;

		public override void Dispose() { }

		public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
		{
			if (messageType == WebSocketMessageType.Text)
			{
				var msg = Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count);
				lock (_lock) SentMessages.Add(msg);
			}
			return Task.CompletedTask;
		}
	}

	private sealed class CloseOnReceiveWebSocket : WebSocket
	{
		public override WebSocketCloseStatus? CloseStatus => WebSocketCloseStatus.NormalClosure;
		public override string? CloseStatusDescription => "closed";
		public override WebSocketState State => WebSocketState.Open;
		public override string SubProtocol => "";

		public override void Abort() { }

		public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
			Task.CompletedTask;

		public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
			Task.CompletedTask;

		public override void Dispose() { }

		public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
			Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true));

		public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
			Task.CompletedTask;
	}
}
