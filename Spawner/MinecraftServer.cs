using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Spawner
{
	public sealed class MinecraftServer(string name, string workingDirectory) : IDisposable, IAsyncDisposable
	{
		private readonly object _lock = new();
		private Process? _minecraftProcess;
		private StreamWriter? _stdin;

		private CancellationTokenSource? _pumpCts;
		private int _stoppedRaised;
		private Task? _stdoutTask;
		private Task? _stderrTask;
		private EventHandler? _exitedHandler;

		public string Name { get; } = name;
		public string WorkingDirectory { get; } = workingDirectory;
		public DateTime StartedAt { get; private set; }

		public bool IsRunning
		{
			get
			{
				lock (_lock)
				{
					return _minecraftProcess != null && !_minecraftProcess.HasExited;
				}
			}
		}
		public int? ProcessId
		{
			get
			{
				lock (_lock)
				{
					return _minecraftProcess is { HasExited: false } ? _minecraftProcess.Id : null;
				}
			}
		}

		public event Action<string>? OnStdOutLine;
		public event Action<string>? OnStdErrLine;
		public event Action? OnStarted;
		public event Action? OnStopped;

		public void Start(string javaPath, string javaArgs, CancellationToken ct, string serverJarName = "server.jar", string minecraftArgs = "nogui")
		{
			Process process;
			lock (_lock)
			{
				if (_minecraftProcess is { HasExited: false })
				{
					throw new InvalidOperationException($"Instance `{Name}` is already running.");
				}

				Directory.CreateDirectory(WorkingDirectory);

				var psi = new ProcessStartInfo
				{
					FileName = javaPath,
					WorkingDirectory = WorkingDirectory,

					Arguments = $"{javaArgs} -jar \"{serverJarName}\" {minecraftArgs}",

					RedirectStandardInput = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,

					CreateNoWindow = true,
				};

				process = new Process { StartInfo = psi, EnableRaisingEvents = true };
				if (!process.Start()) throw new InvalidOperationException($"Failed to start Minecraft instance `{Name}`.");
				
				StartedAt = DateTime.UtcNow;
				_minecraftProcess = process;
				_stdin = process.StandardInput;
				_stdin.AutoFlush = true;

				_stoppedRaised = 0;
				_exitedHandler = (_, _) =>
				{
					if (Interlocked.Exchange(ref _stoppedRaised, 1) == 0)
						OnStopped?.Invoke();
				};
				process.Exited += _exitedHandler;
			}

			_pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			var token = _pumpCts.Token;

			_stdoutTask = PumpLines(process.StandardOutput, l => OnStdOutLine?.Invoke(l), token);
			_stderrTask = PumpLines(process.StandardError, l => OnStdErrLine?.Invoke(l), token);

			OnStarted?.Invoke();
		}

		public static async Task PumpLines(TextReader reader, Action<string> onLineAction, CancellationToken ct)
		{
			try
			{
				while (true)
				{
					var line = await reader.ReadLineAsync(ct);
					if (line is null) break;
					onLineAction(line);
				}
			}
			catch (OperationCanceledException) { }
		}


		public async Task Stop(TimeSpan? timeout = null)
		{
			Process? p;
			lock (_lock) p = _minecraftProcess;

			if (p is null) return;

			await StopServer(p, timeout);
			await CleanupAfterStop(p);
		}

		public async Task ForceStop()
		{
			Process? p;
			lock (_lock) p = _minecraftProcess;

			if (p is null) return;

			try
			{
				if (!p.HasExited)
					p.Kill(entireProcessTree: true);
			}
			catch { }

			try { await WaitForExitAsync(p, TimeSpan.FromSeconds(5)); } catch { }
			await CleanupAfterStop(p);
		}


		private static async Task StopServer(Process? p, TimeSpan? timeout = null)
		{
			if (p is null) return;

			if (!p.HasExited)
			{
				try { p.StandardInput.WriteLine("stop"); }
				catch (InvalidOperationException) { }
				catch (IOException) { }
			}

			timeout ??= TimeSpan.FromSeconds(20);
			var exited = await WaitForExitAsync(p, timeout.Value);
			if (!exited)
			{
				try { p.Kill(entireProcessTree: true); } catch { }
			}
		}


		static async Task<bool> WaitForExitAsync(Process p, TimeSpan timeout)
		{
			using var cts = new CancellationTokenSource(timeout);
			try { await p.WaitForExitAsync(cts.Token); return true; }
			catch (OperationCanceledException) { return false; }
		}

		public void SendCommand(string command)
		{
			lock (_lock)
			{
				if (!IsRunning)
				{
					throw new InvalidOperationException($"Instance `{Name}` is not running.");
				}

				if (_stdin is null)
				{
					throw new InvalidOperationException($"Instance `{Name}` is not running.");
				}
				try { _stdin.WriteLine(command); }
				catch (ObjectDisposedException) { }
				catch (IOException) { }
			}
		}

		public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

		public async ValueTask DisposeAsync()
		{
			await Stop();
		}

		private async Task CleanupAfterStop(Process p)
		{
			CancellationTokenSource? pump;
			Task? outTask;
			Task? errTask;
			EventHandler? handler;

			lock (_lock)
			{
				pump = _pumpCts;
				outTask = _stdoutTask;
				errTask = _stderrTask;
				handler = _exitedHandler;

				_minecraftProcess = null;
				_stdin = null;
				_pumpCts = null;
				_stdoutTask = null;
				_stderrTask = null;
				_exitedHandler = null;
			}

			try { pump?.Cancel(); } catch { }

			try
			{
				await Task.WhenAll(
					outTask ?? Task.CompletedTask,
					errTask ?? Task.CompletedTask);
			}
			catch { }

			try { pump?.Dispose(); } catch { }

			try
			{
				if (handler != null)
					p.Exited -= handler;
				p.Dispose();
			}
			catch { }
		}

	}
}
