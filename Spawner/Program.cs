using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Add services to DI container (you’ll add your own later)
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

// Basic middleware
app.UseHttpsRedirection();

// Example API
app.MapGet("/health", () => Results.Ok(new { ok = true, time = DateTimeOffset.UtcNow }));

app.Run("http://127.0.0.1:8080");