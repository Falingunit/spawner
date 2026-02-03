using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Spawner.Services;

public static class PropertiesRevision
{
	public static string Compute(object properties)
	{
		var json = JsonSerializer.Serialize(properties);
		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
		return $"rev_{Convert.ToHexString(hash)}";
	}
}
