using System.Threading.RateLimiting;
using System.Net;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using SyncDesk.Sinalizacao;

bool testeWebLocal = args.Contains("--teste-web-local", StringComparer.OrdinalIgnoreCase);
bool testeLocal = testeWebLocal || args.Contains("--teste-local", StringComparer.OrdinalIgnoreCase);
int portaTeste = testeWebLocal ? 7446 : 7445;
var builder = WebApplication.CreateBuilder(args.Where(arg =>
    !arg.Equals("--teste-local", StringComparison.OrdinalIgnoreCase) &&
    !arg.Equals("--teste-web-local", StringComparison.OrdinalIgnoreCase)).ToArray());
if (testeLocal)
{
    if (builder.Configuration.GetSection("Kestrel:Endpoints").Exists())
        throw new InvalidOperationException($"O teste local usa exclusivamente 127.0.0.1:{portaTeste}.");
    builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, portaTeste));
}
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Result", LogLevel.Warning);
var config = builder.Configuration.GetSection("SyncDesk").Get<Configuracao>() ?? new();
config.Validar(testeLocal);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(new ModoSinalizacao(testeLocal, testeWebLocal));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CodigosEmitidos>();
builder.Services.AddSingleton<HistoricoWeb>();
builder.Services.AddSingleton<SessoesLoginWeb>();
builder.Services.AddSingleton<Sessoes>();
builder.Services.AddHostedService<Expiracao>();
builder.Services.AddSingleton<Sockets>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = testeLocal ? $"SyncDesk.Web.Local.{portaTeste}" : "__Host-SyncDesk.Web";
    options.Cookie.Path = "/";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = testeLocal ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = SessoesLoginWeb.Validade;
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnValidatePrincipal = context =>
    {
        if (context.HttpContext.RequestServices.GetRequiredService<SessoesLoginWeb>().Vigencia(context.Principal) is null)
            context.RejectPrincipal();
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("abertura", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
    foreach (var (nome, limite) in new[] { ("login-web", 6), ("convite-web", 30) })
        options.AddPolicy(nome, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = limite, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();
app.Use(async (context, next) =>
{
    if (testeLocal && (context.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip) ||
        context.Request.Host.Host != "127.0.0.1" || context.Request.Host.Port != portaTeste))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    if (!testeLocal && !context.Request.IsHttps)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("O SyncDesk requer HTTPS.");
        return;
    }
    context.Response.Headers["Cache-Control"] = "no-store";
    string moldura = context.Request.Path == "/sessao" ? "'self'" : "'none'";
    context.Response.Headers["Content-Security-Policy"] =
        $"default-src 'self'; connect-src 'self'; media-src blob: 'self'; frame-ancestors {moldura}; object-src 'none'; base-uri 'none'; form-action 'none'";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});
if (testeLocal) app.MapGet("/teste-local/pronto", () => Results.Text("SyncDesk teste local"));
else app.UseHsts();
app.UseWebSockets(new() { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(20) });
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
PainelWeb.Mapear(app, config);
app.MapMethods("/ws/{modo}", ["GET", "CONNECT"],
    (HttpContext context, string modo, Sockets sockets) => sockets.Atender(context, modo))
    .RequireRateLimiting("abertura");
app.Run();
