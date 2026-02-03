using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Spawner.Realtime
{
	public sealed class WsClient
	{
		private readonly WebSocket _ws;
		private readonly Channel<string> _sendQueue = Channel.CreateUnbounded<string>();
		private readonly HashSet<string> _topics = new();
		private readonly object _lock = new();

		public string ClientId { get; }
		public string SessionId { get; } = $"sess_{Guid.NewGuid():N}";

		public WsClient(string clientId, WebSocket ws)
		{
			ClientId = clientId;
			_ws = ws;
		}

		public bool IsSubscribed(string topic)
		{
			lock (_lock) return _topics.Contains(topic);
		}

		public void Subscribe(IEnumerable<string> topics)
		{
			lock (_lock) foreach (var t in topics) _topics.Add(t);
		}

		public void Unsubscribe(IEnumerable<string> topics)
		{
			lock (_lock) foreach (var t in topics) _topics.Remove(t);
		}

		public Task SendAsync(string json) => _sendQueue.Writer.WriteAsync(json).AsTask();

		public async Task RunSendLoopAsync(CancellationToken ct)
		{
			while (await _sendQueue.Reader.WaitToReadAsync(ct))
			{
				while (_sendQueue.Reader.TryRead(out var msg))
				{
					if (_ws.State != WebSocketState.Open) return;
					var bytes = Encoding.UTF8.GetBytes(msg);
					await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
				}
			}
		}
	}
}
