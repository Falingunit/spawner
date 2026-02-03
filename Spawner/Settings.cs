using System.ComponentModel.DataAnnotations;

namespace Spawner;

public sealed class Settings
{
	public string DefaultJavaArgs { get; set; } = "";
}
