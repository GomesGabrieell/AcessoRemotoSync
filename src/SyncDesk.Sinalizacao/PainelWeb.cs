using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace SyncDesk.Sinalizacao;

internal static class PainelWeb
{
    public static void Mapear(WebApplication app, Configuracao config)
    {
        var api = app.MapGroup("/api/web").AddEndpointFilter(async (contexto, proximo) =>
        {
            var pedido = contexto.HttpContext.Request;
            if (pedido.Method != "GET" && pedido.Headers.Origin != $"{pedido.Scheme}://{pedido.Host}")
                return Results.StatusCode(403);
            return await proximo(contexto);
        });
        api.MapPost("/entrar", async (HttpContext contexto, Login login, Sessoes sessoes, SessoesLoginWeb logins) =>
        {
            if (!sessoes.ValidarLogin(login.Nome ?? "", login.Credencial ?? "")) return Results.Unauthorized();
            if (logins.Abrir() is not { } id) return Results.StatusCode(429);
            logins.Sair(contexto.User);
            var identidade = new ClaimsIdentity([new Claim(ClaimTypes.Name, login.Nome!), new Claim(ClaimTypes.Sid, id)],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await contexto.SignInAsync(new ClaimsPrincipal(identidade),
                new AuthenticationProperties { IsPersistent = false, AllowRefresh = false });
            return Results.NoContent();
        }).RequireRateLimiting("login-web").WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(2048));

        var privado = api.MapGroup("").RequireAuthorization();
        privado.MapGet("/painel", (ClaimsPrincipal tecnico, Sessoes sessoes) =>
            Results.Ok(sessoes.PainelWeb(tecnico.Identity!.Name!)));
        privado.MapPost("/sair", async (HttpContext contexto, SessoesLoginWeb logins) =>
        {
            logins.Sair(contexto.User);
            await contexto.SignOutAsync();
            return Results.NoContent();
        });
        privado.MapPost("/convites", (ClaimsPrincipal tecnico, NovoConvite dados, Sessoes sessoes) =>
        {
            var nome = new string((dados.Cliente ?? "").Where(c => !char.IsControl(c)).Take(80).ToArray()).Trim();
            var convite = sessoes.CriarConvite(tecnico.Identity!.Name!, nome);
            return convite is null ? Results.StatusCode(429) : Results.Ok(convite);
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(2048));
        privado.MapDelete("/convites/{id}", (string id, ClaimsPrincipal tecnico, Sessoes sessoes) =>
            sessoes.CancelarConvite(id, tecnico.Identity!.Name!) ? Results.NoContent() : Results.NotFound());

        app.MapGet("/painel", () => Html(app, "painel-web.html"));
        app.MapGet("/sessao", (string? id, ClaimsPrincipal tecnico, Sessoes sessoes) =>
            id is not null && sessoes.Pertence(id, tecnico.Identity!.Name!) ? Html(app, "index.html") :
                Results.NotFound()).RequireAuthorization();
        app.MapGet("/atendimento/{chave}", (string chave, Sessoes sessoes) =>
            Html(app, "convite.html", sessoes.ConviteDisponivel(chave) ? 200 : 404))
            .RequireRateLimiting("convite-web");
        app.MapGet("/api/convite/{chave}", (string chave, Sessoes sessoes) =>
            sessoes.ConviteDisponivel(chave) ? Results.Ok(new { download = LocalizarExecutavel(config) is not null }) : Results.NotFound())
            .RequireRateLimiting("convite-web");
        app.MapGet("/atendimento/{chave}/baixar", (string chave, Sessoes sessoes, ModoSinalizacao modo) =>
        {
            if (!sessoes.ConviteDisponivel(chave)) return Results.NotFound();
            var caminho = LocalizarExecutavel(config);
            string prefixo = modo.TesteWebLocal ? "web-local-" : modo.TesteLocal ? "local-" : "";
            return caminho is null ? Results.StatusCode(503) :
                Results.File(caminho, "application/octet-stream", $"SyncDesk-{prefixo}{chave}.exe",
                    enableRangeProcessing: true);
        }).RequireRateLimiting("convite-web");
    }

    private static string? LocalizarExecutavel(Configuracao config)
    {
        if (string.IsNullOrWhiteSpace(config.ExecutavelCliente)) return null;
        string caminho = Path.GetFullPath(config.ExecutavelCliente);
        return File.Exists(caminho) ? caminho : null;
    }

    private static IResult Html(WebApplication app, string nome, int status = 200) =>
        Results.Content(File.ReadAllText(Path.Combine(app.Environment.WebRootPath, nome)),
            "text/html; charset=utf-8", statusCode: status);

    internal sealed record Login(string? Nome, string? Credencial);
    internal sealed record NovoConvite(string? Cliente);
}
