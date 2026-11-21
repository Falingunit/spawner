using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.RateLimiting;

namespace Spawner.Services;

public sealed class AdminAuthentication : IDisposable
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;
    private readonly string _username;
    private readonly byte[] _salt;
    private readonly byte[] _hash;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _sessions = new();
    public string Origin { get; }

    public AdminAuthentication(IConfiguration config, IHostEnvironment environment)
    {
        _username = config["Authentication:Username"] ?? "";
        Origin = config["Authentication:PublicOrigin"]?.TrimEnd('/') ?? "";
        if (_username.Length is < 1 or > 128 ||
            !Uri.TryCreate(Origin, UriKind.Absolute, out var uri) ||
            uri.GetLeftPart(UriPartial.Authority) != Origin ||
            (uri.Scheme != "https" && !(environment.IsDevelopment() && uri.Scheme == "http" && uri.IsLoopback)))
            throw new InvalidOperationException("Configure Authentication:Username and Authentication:PublicOrigin (HTTPS; loopback HTTP allowed in Development).");
        try
        {
            var parts = (config["Authentication:PasswordHash"] ?? "").Split(':');
            if (parts.Length != 3 || parts[0] != "pbkdf2-sha256-600000") throw new FormatException();
            _salt = Convert.FromBase64String(parts[1]);
            _hash = Convert.FromBase64String(parts[2]);
            if (_salt.Length != 32 || _hash.Length != 32) throw new FormatException();
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Configure Authentication:PasswordHash using scripts/hash-admin-password.py. No default login is provided.");
        }
    }

    public bool Verify(string username, string password)
    {
        if (username.Length > 128 || password.Length > 1024) return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, _salt, 600_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actual, _hash) & string.Equals(username, _username, StringComparison.Ordinal);
    }

    public ClaimsPrincipal CreateSession()
    {
        // Bound memory and revoke expired sessions, including their open sockets.
        foreach (var entry in _sessions)
            if (entry.Value.IsCancellationRequested) Revoke(entry.Key);
        if (_sessions.Count >= 100) throw new InvalidOperationException("Too many active sessions.");
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var lifetime = new CancellationTokenSource(TimeSpan.FromHours(8));
        _sessions[id] = lifetime;
        return new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Name, _username), new Claim(ClaimTypes.NameIdentifier, id)
        ], Scheme));
    }

    public CancellationToken? SessionToken(ClaimsPrincipal principal)
    {
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return id != null && _sessions.TryGetValue(id, out var lifetime) && !lifetime.IsCancellationRequested
            ? lifetime.Token : null;
    }

    public void Revoke(string? id)
    {
        if (id != null && _sessions.TryRemove(id, out var lifetime))
        {
            lifetime.Cancel();
            // Tokens may still be used by requests unwinding after cancellation.
        }
    }

    public void Dispose()
    {
        foreach (var lifetime in _sessions.Values) { lifetime.Cancel(); lifetime.Dispose(); }
        _sessions.Clear();
    }

    public static void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<AdminAuthentication>();
        builder.Services.AddAuthentication(Scheme).AddCookie(options =>
        {
            options.Cookie.Name = builder.Environment.IsDevelopment() ? "spawner-session" : "__Host-spawner-session";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
            options.Events.OnValidatePrincipal = context =>
            {
                if (context.HttpContext.RequestServices.GetRequiredService<AdminAuthentication>().SessionToken(context.Principal!) == null)
                    context.RejectPrincipal();
                return Task.CompletedTask;
            };
        });
        builder.Services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-Token";
            options.Cookie.Name = builder.Environment.IsDevelopment() ? "spawner-csrf" : "__Host-spawner-csrf";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            // A global budget cannot be bypassed by spoofing proxy headers or rotating IPs.
            options.AddFixedWindowLimiter("login", limiter =>
            {
                limiter.PermitLimit = 10;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
                limiter.AutoReplenishment = true;
            });
        });
    }

    public static async Task ProtectRequest(HttpContext context, RequestDelegate next)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        var unsafeMethod = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method);
        if (unsafeMethod || context.WebSockets.IsWebSocketRequest)
        {
            var auth = context.RequestServices.GetRequiredService<AdminAuthentication>();
            if (!string.Equals(context.Request.Headers.Origin, auth.Origin, StringComparison.Ordinal))
            {
                context.Response.StatusCode = 403;
                return;
            }
            if (unsafeMethod)
            {
                // Require the header so antiforgery never buffers an attacker-supplied form body.
                if (string.IsNullOrEmpty(context.Request.Headers["X-CSRF-Token"]))
                {
                    context.Response.StatusCode = 403;
                    return;
                }
                try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException) { context.Response.StatusCode = 403; return; }
            }
        }
        await next(context);
    }
}
