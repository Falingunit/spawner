using Spawner.Realtime;
using System.Net.WebSockets;
using System.Text;

namespace Spawner.Tests;

public sealed class EventBusTests
{
	[Fact]
	public async Task SharedClientId_ReceivesInBothTabsAndUnregistersOnlyOneSession()
	{
		var bus = new EventBus();
		using var ws1 = new TestWebSocket();
		using var ws2 = new TestWebSocket();
		var first = new WsClient("shared-browser", ws1);
		var second = new WsClient("shared-browser", ws2);
		first.Subscribe([Topics.Servers]);
		second.Subscribe([Topics.Servers]);
		bus.Register(first);
		bus.Register(second);
		bus.Publish(Topics.Servers, new { kind = "first" });
		bus.Unregister(first.SessionId);
		bus.Publish(Topics.Servers, new { kind = "second" });

		// The loop drains each queue synchronously until it waits for new messages.
		using var cts = new CancellationTokenSource();
		var loop1 = first.RunSendLoopAsync(cts.Token);
		var loop2 = second.RunSendLoopAsync(cts.Token);
		Assert.Single(ws1.SentMessages);
		Assert.Equal(2, ws2.SentMessages.Count);
		cts.Cancel();
		try { await Task.WhenAll(loop1, loop2); } catch (OperationCanceledException) { }
	}

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
