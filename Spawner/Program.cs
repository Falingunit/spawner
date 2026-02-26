using Spawner;
using Spawner.Realtime;
using Spawner.Services;
using System.IO;
using System.Net.WebSockets;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var allowedCorsOrigins = builder.Configuration
	.GetSection("Cors:AllowedOrigins")
	.Get<string[]>()?
	.Where(static x => !string.IsNullOrWhiteSpace(x))
	.Select(static x => x.Trim().TrimEnd('/'))
	.Distinct(StringComparer.OrdinalIgnoreCase)
	.ToArray() ?? [];

builder.Services.AddControllers();

var contentRoot = builder.Environment.ContentRootPath;
builder.Services
	.AddOptions<Settings>()
	.Bind(builder.Configuration.GetSection("Settings"))
	.ValidateDataAnnotations()
	.ValidateOnStart();

builder.Services.AddSingleton<InstanceManager>();
builder.Services.AddSingleton<WebSocketHub>();

builder.Services.AddSingleton<IdempotencyStore>();
builder.Services.AddSingleton<ConsoleStore>();
builder.Services.AddSingleton<EventBus>();
builder.Services.AddSingleton<StatePublisher>();
builder.Services.AddSingleton<ModrinthClient>();
// CORS:
// - Development: allow any origin for local UI work.
// - Production: same-origin by default unless explicit origins are configured.
builder.Services.AddCors(options =>
{
	options.AddDefaultPolicy(policy =>
	{
		if (builder.Environment.IsDevelopment())
		{
			policy
				.AllowAnyOrigin()
				.AllowAnyMethod()
				.AllowAnyHeader();
			return;
		}

		policy
			.AllowAnyMethod()
			.AllowAnyHeader();

		if (allowedCorsOrigins.Length > 0)
		{
			policy.WithOrigins(allowedCorsOrigins);
		}
	});
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseCors();

app.UseWebSockets(new WebSocketOptions
{
	KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.Services.GetRequiredService<InstanceManager>().Initialize();
app.Services.GetRequiredService<StatePublisher>().HookAllInstances();

if (app.Environment.IsDevelopment())
{
	app.UseSwagger();
	app.UseSwaggerUI();
}

app.MapControllers();
app.Run();

public sealed class WebSocketHub
{
	private readonly object _lock = new();
	private readonly HashSet<WebSocket> _sockets = new();

	public void Add(WebSocket ws)
	{
		lock (_lock) _sockets.Add(ws);
	}

	public void Remove(WebSocket ws)
	{
		lock (_lock) _sockets.Remove(ws);
	}

	public async Task BroadcastTextAsync(string message, CancellationToken ct = default)
	{
		ArraySegment<byte> payload = Encoding.UTF8.GetBytes(message);

		List<WebSocket> targets;
		lock (_lock) targets = _sockets.ToList();

		foreach (var ws in targets)
		{
			if (ws.State != WebSocketState.Open) continue;

			try
			{
				await ws.SendAsync(
					payload,
					WebSocketMessageType.Text,
					endOfMessage: true,
					cancellationToken: ct);
			}
			catch
			{
				Remove(ws);
				try { ws.Abort(); } catch { }
				try { ws.Dispose(); } catch { }
			}
		}
	}
}
