using System.Collections.Concurrent;

namespace Spawner.Realtime
{

	public sealed class ConsoleStore
	{
		private readonly int _maxLines;
		private readonly ConcurrentDictionary<string, BoundedQueue<string>> _buffers = new();

		public ConsoleStore(int maxLines = 500)
		{
			_maxLines = maxLines;
		}

		public void Append(string serverId, string line)
		{
			var q = _buffers.GetOrAdd(serverId, _ => new BoundedQueue<string>(_maxLines));
			q.Enqueue(line);
		}

		public IReadOnlyList<string> Get(string serverId, int limit)
		{
			if (!_buffers.TryGetValue(serverId, out var q)) return Array.Empty<string>();
			return q.Snapshot(limit);
		}

		private sealed class BoundedQueue<T>
		{
			private readonly int _cap;
			private readonly LinkedList<T> _list = new();
			private readonly object _lock = new();

			public BoundedQueue(int cap) => _cap = cap;

			public void Enqueue(T item)
			{
				lock (_lock)
				{
					_list.AddLast(item);
					while (_list.Count > _cap) _list.RemoveFirst();
				}
			}

			public IReadOnlyList<T> Snapshot(int limit)
			{
				lock (_lock)
				{
					return _list.TakeLast(Math.Min(limit, _list.Count)).ToList();
				}
			}
		}
	}
}