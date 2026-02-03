using Spawner.Realtime;

namespace Spawner.Realtime
{

	public sealed class StatePublisher
	{
		private readonly InstanceManager _manager;
		private readonly EventBus _bus;
		private readonly ConsoleStore _console;

		public StatePublisher(InstanceManager manager, EventBus bus, ConsoleStore console)
		{
			_manager = manager;
			_bus = bus;
			_console = console;
		}

		public void HookAllInstances()
		{
			foreach (var inst in _manager.GetAllInstances())
			{
				HookInstance(inst);
			}
		}

		public void HookInstance(Instance inst)
		{
			var id = inst.InstanceProperties.InstanceID;

			inst.OnStatusChanged += status =>
			{
				_bus.Publish(Topics.Servers, new
				{
					kind = "server.patch",
					serverId = id,
					patch = new { status = MapStatus(status) }
				});
			};

			inst.OnPlayersChanged += players =>
			{
				_bus.Publish(Topics.Servers, new
				{
					kind = "server.patch",
					serverId = id,
					patch = new { playersOnline = players }
				});
			};

			inst.Server.OnStdOutLine += line =>
			{
				_console.Append(id, line);
				_bus.Publish(Topics.Console(id), new
				{
					kind = "console.line",
					serverId = id,
					line,
					level = "info"
				});

				_bus.Publish(Topics.Logs(id), new
				{
					kind = "log.line",
					serverId = id,
					line,
					level = "info"
				});
			};

			inst.Server.OnStdErrLine += line =>
			{
				_console.Append(id, line);
				_bus.Publish(Topics.Console(id), new
				{
					kind = "console.line",
					serverId = id,
					line,
					level = "error"
				});

				_bus.Publish(Topics.Logs(id), new
				{
					kind = "log.line",
					serverId = id,
					line,
					level = "error"
				});
			};
		}

		private static string MapStatus(InstanceStatus s) => s switch
		{
			InstanceStatus.Online => "online",
			InstanceStatus.Offline => "offline",
			InstanceStatus.Starting => "starting",
			InstanceStatus.Stopping => "stopping",
			_ => "offline"
		};
	}
}
