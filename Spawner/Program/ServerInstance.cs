using System.Data.SqlTypes;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using Microsoft.Win32.SafeHandles;

namespace Spawner
{
    public class ServerInstance
    {
        private Process? _minecraftProcess;
        private StreamWriter? _stdin;

        public event Action<string>? OnStdoutLine;
        public event Action<string>? OnStderrLine;
        public event Action? OnExited;

        public bool isRunning => _process is { HasExited: false };

        public async Task StartAsync(
            string javaExePath,
            string serverJarPath,
            string serverDirectory,
            string jvmArgs,
            CancellationToken ct = default
        )
        {
            if (IsRunning) throw new InvalidOperationException("Server already running.");
            if (!File.Exists(serverJarPath)) throw new InvalidOperationException("No server jar present at \"" + serverJarPath + "\". Could not start server.");
            
            var psi = new ProcessStartInfo
            {
                FileName = javaPath ?? "java",
                Arguments = $"-Xms{minRam} -Xmx{maxRam} -jar \"{jarPath}\" nogui",
                WorkingDirectory = serverDir,

                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _minecraftProcess = new Process { StartInfo = processStartInfo, EnableRaisingEvents = true };
            _minecraftProcess.HasExited += (_, __) => OnExited?.Invoke();

            if (!_minecraftProcess.Start())
                throw new InvalidOperationException("Failed to start minecraft server.");

            _stdin = _minecraftProcess.StandartInput;
            _stdin.AutoFlush = true;
        }

    }
}