using System.Buffers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace SyncDesk.Sinalizacao;

internal sealed class Sockets(Sessoes sessoes, Configuracao config, SessoesLoginWeb logins)
{
    private readonly SemaphoreSlim _vagas = new(config.LimiteConexoes);
    private const int TamanhoMaximo = 65_536;

    public async Task Atender(HttpContext context, string modo)
    {
        string origem = context.Request.Headers.Origin.ToString();
        bool web = modo == "tecnico-web";
        if ((modo != "cliente" && modo != "tecnico" && !web) || !context.WebSockets.IsWebSocketRequest ||
            (!string.Equals(origem, $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase) &&
                !(context.Request.IsHttps && !web && origem == "https://syncdesk-app.invalid")))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        string? dono = null;
        TimeSpan? vigencia = null;
        CancellationToken tokenLogin = CancellationToken.None;
        if (web)
        {
            var auth = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            dono = auth.Principal?.Identity?.Name;
            vigencia = auth.Properties?.ExpiresUtc - DateTimeOffset.UtcNow;
            if (!auth.Succeeded || dono is null || vigencia is null || vigencia <= TimeSpan.Zero ||
                logins.Vigencia(auth.Principal) is not { } login)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            tokenLogin = login;
        }
        if (!_vagas.Wait(0))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var par = new Par(modo == "cliente", context.RequestAborted);
            using var encerramento = CancellationTokenSource.CreateLinkedTokenSource(par.Vida, tokenLogin);
            if (vigencia is { } duracao) encerramento.CancelAfter(duracao);
            using var limite = new TokenBucketRateLimiter(new()
            {
                TokenLimit = 80, TokensPerPeriod = 20, ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                AutoReplenishment = false
            });
            if (par.Cliente) sessoes.Registrar(par);
            var receber = Receber(socket, par, dono, limite, encerramento.Token);
            var enviar = Enviar(socket, par, encerramento.Token);
            try
            {
                await Task.WhenAny(receber, enviar);
            }
            finally
            {
                sessoes.Sair(par);
                await encerramento.CancelAsync();
                try { await Task.WhenAll(receber, enviar); }
                catch (Exception e) when (e is WebSocketException or OperationCanceledException or JsonException) { }
                socket.Abort();
            }
        }
        finally
        {
            _vagas.Release();
        }
    }

    private static async Task Enviar(WebSocket socket, Par par, CancellationToken token)
    {
        await foreach (object evento in par.Saida.ReadAllAsync(token))
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(evento);
            await socket.SendAsync(json.AsMemory(), WebSocketMessageType.Text, true, token);
        }
    }

    private async Task Receber(WebSocket socket, Par par, string? dono, TokenBucketRateLimiter limite, CancellationToken token)
    {
        var pequeno = new byte[2048];
        while (!token.IsCancellationRequested)
        {
            byte[] buffer = pequeno;
            int total = 0;
            try
            {
                ValueWebSocketReceiveResult recebido;
                do
                {
                    recebido = await socket.ReceiveAsync(buffer.AsMemory(total, Math.Min(buffer.Length, TamanhoMaximo) - total), token);
                    if (recebido.MessageType != WebSocketMessageType.Text) return;
                    total += recebido.Count;
                    if (recebido.EndOfMessage) break;
                    if (total >= TamanhoMaximo) return;
                    if (total == pequeno.Length && buffer == pequeno)
                    {
                        buffer = ArrayPool<byte>.Shared.Rent(TamanhoMaximo);
                        pequeno.CopyTo(buffer, 0);
                        CryptographicOperations.ZeroMemory(pequeno);
                    }
                } while (true);

                limite.TryReplenish();
                using var permissao = limite.AttemptAcquire();
                if (!permissao.IsAcquired) return;
                using var json = JsonDocument.Parse(buffer.AsMemory(0, total));
                Processar(par, dono, json.RootElement);
            }
            finally
            {
                if (buffer == pequeno) CryptographicOperations.ZeroMemory(pequeno.AsSpan(0, total));
                else ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }
    }

    private void Processar(Par par, string? dono, JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object) return;
        switch (Texto(json, "tipo"))
        {
            case "solicitar" when !par.Cliente && dono is null:
                sessoes.Solicitar(par, Texto(json, "codigo"), Texto(json, "credencial"));
                break;
            case "acompanharConvite" when dono is not null:
                sessoes.AcompanharConvite(par, Texto(json, "id"), dono);
                break;
            case "associarConvite" when par.Cliente:
                sessoes.AssociarConvite(par, Texto(json, "chave"));
                break;
            case "visualizando" when dono is not null:
                sessoes.RegistrarVisualizacao(par, Texto(json, "computador"));
                break;
            case "aceitar" when par.Cliente:
                sessoes.Responder(par, true);
                break;
            case "recusar" when par.Cliente:
                sessoes.Responder(par, false);
                break;
            case "sinal" when json.TryGetProperty("dados", out var dados) && dados.ValueKind == JsonValueKind.Object:
                sessoes.Sinal(par, Texto(json, "sessao"), dados);
                break;
            case "renovar" when json.TryGetProperty("sequencia", out var numero) &&
                numero.ValueKind == JsonValueKind.Number && numero.TryGetInt32(out int sequencia):
                sessoes.Renovar(par, Texto(json, "sessao"), sequencia,
                    json.TryGetProperty("atividade", out var atividade) && atividade.ValueKind == JsonValueKind.True);
                break;
            case "encerrar":
                sessoes.Sair(par, Texto(json, "motivo") == "transporte");
                break;
        }
    }

    private static string Texto(JsonElement json, string propriedade) =>
        json.TryGetProperty(propriedade, out var valor) && valor.ValueKind == JsonValueKind.String
            ? valor.GetString() ?? ""
            : "";
}
