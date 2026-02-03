using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Spawner.Tests;

public sealed class InstanceManagerTests
{
	private sealed class TestHostEnvironment : IHostEnvironment
	{
		public string EnvironmentName { get; set; } = Environments.Development;
		public string ApplicationName { get; set; } = "Spawner.Tests";
		public string ContentRootPath { get; set; } = "";
		public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
	}

	[Fact]
	public void LoadInstanceProperties_CreatesFileWhenMissing()
	{
		var dir = Path.Combine(Path.GetTempPath(), "spawner_tests_" + Guid.NewGuid().ToString("N"));
		try
		{
			var settings = Options.Create(new Settings { DefaultJavaArgs = "" });
			var cfg = new ConfigurationBuilder().Build();
			var env = new TestHostEnvironment { ContentRootPath = dir };
			var mgr = new InstanceManager(settings, cfg, env);

			var list = mgr.LoadInstanceProperties(dir);

			Assert.Empty(list);
			Assert.True(File.Exists(Path.Combine(dir, "instances.json")));
		}
		finally
		{
			try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
		}
	}

	[Fact]
	public void LoadInstanceProperties_ResetsCorruptJson()
	{
		var dir = Path.Combine(Path.GetTempPath(), "spawner_tests_" + Guid.NewGuid().ToString("N"));
		try
		{
			Directory.CreateDirectory(dir);
			var path = Path.Combine(dir, "instances.json");
			File.WriteAllText(path, "{ definitely: not json");

			var settings = Options.Create(new Settings { DefaultJavaArgs = "" });
			var cfg = new ConfigurationBuilder().Build();
			var env = new TestHostEnvironment { ContentRootPath = dir };
			var mgr = new InstanceManager(settings, cfg, env);

			var list = mgr.LoadInstanceProperties(dir);

			Assert.Empty(list);
			Assert.Equal("[]", File.ReadAllText(path).Trim());
		}
		finally
		{
			try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
		}
	}
}
