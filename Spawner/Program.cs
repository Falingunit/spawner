using Spawner;
using Spawner.Realtime;
using Spawner.Services;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net.WebSockets;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // Trust only the framework's loopback proxy defaults, never arbitrary clients.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
});
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
AdminAuthentication.Configure(builder);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Validate authentication configuration before initializing managed servers.
app.Services.GetRequiredService<AdminAuthentication>();
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        if (!context.Request.IsHttps) { context.Response.StatusCode = 400; return; }
        await next(context);
    });
}
app.UseRouting();

app.UseWebSockets(new WebSocketOptions
{
	KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.Use(AdminAuthentication.ProtectRequest);

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
