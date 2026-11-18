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

	private static InstanceManager CreateManager(string dir) => new(
		Options.Create(new Settings { DefaultJavaArgs = "" }),
		new ConfigurationBuilder().Build(), new TestHostEnvironment { ContentRootPath = dir });

	[Fact]
	public async Task SaveInstanceProperties_SerializesConcurrentWriters()
	{
		var dir = Path.Combine(Path.GetTempPath(), "spawner_save_" + Guid.NewGuid().ToString("N"));
		try
		{
			var manager = CreateManager(dir);
			await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
			{
				for (var i = 0; i < 20; i++) manager.SaveInstanceProperties(dir);
			})));
			Assert.Equal("[]", File.ReadAllText(Path.Combine(dir, "instances.json")));
			Assert.False(File.Exists(Path.Combine(dir, "instances.json.tmp")));
		}
		finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
	}

	[Fact]
	public void InstanceOperation_BlocksStartsAndOverlappingUpgradesUntilDisposed()
	{
		var manager = CreateManager(Path.GetTempPath());
		var lease = manager.TryBeginInstanceOperation("upgrading");
		Assert.NotNull(lease);
		Assert.Null(manager.TryBeginInstanceOperation("upgrading"));
		var error = Assert.Throws<InvalidOperationException>(() => manager.StartInstance("upgrading"));
		Assert.Contains("operation", error.Message);
		using var independent = manager.TryBeginInstanceOperation("other");
		Assert.NotNull(independent);
		lease!.Dispose();
		lease.Dispose();
		using var next = manager.TryBeginInstanceOperation("upgrading");
		Assert.NotNull(next);
		Assert.Null(manager.TryBeginInstanceOperation("upgrading"));
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
