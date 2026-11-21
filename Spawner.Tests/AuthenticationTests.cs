using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spawner.Controllers;
using Spawner.Realtime;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using Spawner.Services;

namespace Spawner.Tests;

public sealed class AuthenticationTests : IAsyncLifetime
{
    private const string Origin = "http://localhost:5173";
    private const string Password = "a-long-test-password-only";
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private CookieContainer _cookies = null!;
    private static readonly string Hash = MakeHash();

    private static string MakeHash()
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        return "pbkdf2-sha256-600000:" + Convert.ToBase64String(salt) + ":" +
            Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(Password, salt, 600000, HashAlgorithmName.SHA256, 32));
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["Authentication:Username"] = "admin", ["Authentication:PasswordHash"] = Hash,
            ["Authentication:PublicOrigin"] = Origin
        });
        builder.Services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);
        builder.Services.AddSingleton<InstanceManager>();
        builder.Services.AddSingleton<EventBus>();
        AdminAuthentication.Configure(builder);
        _app = builder.Build();
        _app.UseRouting();
        _app.UseWebSockets();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.UseRateLimiter();
        _app.Use(AdminAuthentication.ProtectRequest);
        _app.MapControllers();
        _app.MapGet("/protected", () => "private");
        _app.MapPost("/protected", () => "changed");
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _cookies = new CookieContainer();
        _client = new HttpClient(new HttpClientHandler { CookieContainer = _cookies, AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync() { _client.Dispose(); await _app.DisposeAsync(); }

    private async Task<string> Token()
    {
        var response = await _client.GetAsync("/api/v1/auth/session");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("csrfToken").GetString()!;
    }

    private Task<HttpResponseMessage> Post(string path, string? token, string? origin = Origin, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (token != null) request.Headers.Add("X-CSRF-Token", token);
        if (origin != null) request.Headers.Add("Origin", origin);
        if (body != null) request.Content = JsonContent.Create(body);
        return _client.SendAsync(request);
    }

    private async Task Login()
    {
        var response = await Post("/api/v1/auth/login", await Token(), body: new { username = "admin", password = Password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("spawner-session="));
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
    }

    [Theory]
    [InlineData("/api/v1/servers")]
    [InlineData("/api/v1/servers/test/files/raw?path=server.properties")]
    [InlineData("/api/v1/modrinth/search")]
    [InlineData("/ws/v1")]
    [InlineData("/protected")]
    public async Task EveryEndpointRequiresLogin(string path)
        => Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync(path)).StatusCode);

    [Fact]
    public async Task LoginRejectsMissingOrForgedCsrfAndForeignOrigin()
    {
        var body = new { username = "admin", password = Password };
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/api/v1/auth/login", null, body: body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/api/v1/auth/login", "forged", body: body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/api/v1/auth/login", await Token(), "https://evil.example", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/api/v1/auth/login", await Token(), null, body)).StatusCode);
    }

    [Fact]
    public async Task LoginAndLogoutRevokeCookieAndSessionToken()
    {
        await Login();
        var cookie = _cookies.GetCookieHeader(_client.BaseAddress!);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/protected")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/protected", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post("/protected", await Token())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post("/api/v1/auth/logout", await Token())).StatusCode);
        using var replay = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = _client.BaseAddress };
        replay.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/protected")).StatusCode);
        var auth = _app.Services.GetRequiredService<AdminAuthentication>();
        var principal = auth.CreateSession();
        var token = auth.SessionToken(principal)!.Value;
        auth.Revoke(principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.True(token.IsCancellationRequested);
        Assert.Null(auth.SessionToken(principal));
    }

    [Fact]
    public async Task InvalidCredentialsAreGenericAndRateLimited()
    {
        var token = await Token();
        for (var i = 0; i < 10; i++)
        {
            var response = await Post("/api/v1/auth/login", token, body: new { username = i % 2 == 0 ? "admin" : "missing", password = "wrong" });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("Invalid username or password.", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Post("/api/v1/auth/login", token, body: new { username = "admin", password = Password })).StatusCode);
    }

    [Fact]
    public async Task TamperedCookieIsRejected()
    {
        _cookies.Add(_client.BaseAddress!, new Cookie("spawner-session", "forged"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/protected")).StatusCode);
    }

    [Fact]
    public async Task AuthenticatedWebsocketRejectsForeignAndMissingOrigin()
    {
        await Login();
        foreach (var origin in new string?[] { "https://evil.example", null })
        {
            using var ws = new ClientWebSocket();
            ws.Options.Cookies = _cookies;
            if (origin != null) ws.Options.SetRequestHeader("Origin", origin);
            var url = new UriBuilder(_client.BaseAddress!) { Scheme = "ws", Path = "/ws/v1" }.Uri;
            var error = await Assert.ThrowsAsync<WebSocketException>(() => ws.ConnectAsync(url, CancellationToken.None));
            Assert.Contains("403", error.Message);
        }
    }

    [Fact]
    public async Task LogoutDisconnectsAnAlreadyOpenWebsocket()
    {
        await Login();
        using var ws = new ClientWebSocket();
        ws.Options.Cookies = _cookies;
        ws.Options.SetRequestHeader("Origin", Origin);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ws.ConnectAsync(new UriBuilder(_client.BaseAddress!) { Scheme = "ws", Path = "/ws/v1" }.Uri, timeout.Token);
        await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"protocolVersion\":1}"), WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[4096];
        var hello = await ws.ReceiveAsync(buffer, timeout.Token);
        Assert.Contains("hello_ack", System.Text.Encoding.UTF8.GetString(buffer, 0, hello.Count));
        Assert.Equal(HttpStatusCode.NoContent, (await Post("/api/v1/auth/logout", await Token())).StatusCode);
        await Assert.ThrowsAsync<WebSocketException>(async () => { await ws.ReceiveAsync(buffer, timeout.Token); });
        Assert.False(timeout.IsCancellationRequested);
    }

    [Fact]
    public async Task ProductionCookiesAreSecureAndHostScoped()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        AdminAuthentication.Configure(builder);
        await using var app = builder.Build();
        var cookie = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AdminAuthentication.Scheme);
        var csrf = app.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;
        Assert.Equal(Microsoft.AspNetCore.Http.CookieSecurePolicy.Always, cookie.Cookie.SecurePolicy);
        Assert.Equal(Microsoft.AspNetCore.Http.CookieSecurePolicy.Always, csrf.Cookie.SecurePolicy);
        Assert.StartsWith("__Host-", cookie.Cookie.Name);
        Assert.StartsWith("__Host-", csrf.Cookie.Name);
        Assert.Null(cookie.Cookie.Domain);
        Assert.Equal("/", cookie.Cookie.Path);
        Assert.False(cookie.SlidingExpiration);
        Assert.Equal(TimeSpan.FromHours(8), cookie.ExpireTimeSpan);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("admin", "invalid", "https://spawner.example")]
    [InlineData("admin", "valid", "http://spawner.example")]
    [InlineData("admin", "valid", "https://spawner.example/path")]
    public void InvalidConfigurationFailsClosed(string? username, string? hash, string? origin)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["Authentication:Username"] = username, ["Authentication:PasswordHash"] = hash == "valid" ? Hash : hash,
            ["Authentication:PublicOrigin"] = origin
        });
        Assert.Throws<InvalidOperationException>(() => new AdminAuthentication(builder.Configuration, builder.Environment));
    }
}
