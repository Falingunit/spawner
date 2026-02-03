using System.Collections.Concurrent;
using System.Text.Json;

namespace Spawner.Realtime
{
	public sealed class EventBus
	{
		private long _seq = 0;

		private readonly int _maxStored = 5000;
		private readonly LinkedList<Envelope> _stored = new();
		private readonly object _storeLock = new();

		private readonly ConcurrentDictionary<string, WsClient> _clients = new();

		public string NextEventId() => $"evt_{Interlocked.Increment(ref _seq)}";

		public void Register(WsClient client) => _clients[client.ClientId] = client;
		public void Unregister(string clientId) => _clients.TryRemove(clientId, out _);

		public string LastEventId => $"evt_{Volatile.Read(ref _seq)}";

		public Envelope Publish(string topic, object payload)
		{
			var evt = new Envelope(
				type: "event",
				eventId: NextEventId(),
				topic: topic,
				ts: DateTime.UtcNow.ToString("O"),
				payload: payload
			);

			Store(evt);
			Broadcast(evt);
			return evt;
		}

		public Envelope StoreOnly(string topic, object payload)
		{
			var evt = new Envelope(
				type: "event",
				eventId: NextEventId(),
				topic: topic,
				ts: DateTime.UtcNow.ToString("O"),
				payload: payload
			);

			Store(evt);
			return evt;
		}

		private void Store(Envelope evt)
		{
			lock (_storeLock)
			{
				_stored.AddLast(evt);
				while (_stored.Count > _maxStored) _stored.RemoveFirst();
			}
		}

		public IReadOnlyList<Envelope>? TryGetSince(string resumeFromEventId)
		{
			lock (_storeLock)
			{
				var list = _stored.ToList();
				var idx = list.FindIndex(e => e.eventId == resumeFromEventId);
				if (idx < 0) return null;
				return list.Skip(idx + 1).ToList();
			}
		}

		private void Broadcast(Envelope evt)
		{
			var json = JsonSerializer.Serialize(evt);
			foreach (var kv in _clients)
			{
				var client = kv.Value;
				if (!client.IsSubscribed(evt.topic!)) continue;
				_ = client.SendAsync(json);
			}
		}
	}
}
