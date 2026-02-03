using System.Collections.Concurrent;

namespace Spawner.Services;

public sealed class IdempotencyStore
{
	private readonly ConcurrentDictionary<string, object> _cache = new();
	public bool TryGet(string key, out object? value) => _cache.TryGetValue(key, out value);
	public void Set(string key, object value) => _cache[key] = value;
	public bool Remove(string key) => _cache.TryRemove(key, out _);
}
