using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using SyncDesk.Domain;

namespace SyncDesk.Sinalizacao;

internal sealed class Par(bool cliente, CancellationToken conexao) : IDisposable
{
    private readonly CancellationTokenSource _vida = CancellationTokenSource.CreateLinkedTokenSource(conexao);
    private readonly Channel<object> _saida = Channel.CreateBounded<object>(32);
    public bool Cliente { get; } = cliente;
    public CancellationToken Vida => _vida.Token;
    public ChannelReader<object> Saida => _saida.Reader;
    public Sessao? Sessao { get; set; }
    public ConviteWeb? Convite { get; set; }
    public void Enviar(object evento)
    {
        if (!_saida.Writer.TryWrite(evento)) _vida.Cancel();
    }
    public void Dispose() => _vida.Dispose();
}

internal sealed class Sessao(Par cliente, string codigo, Atendimento atendimento)
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Codigo { get; } = codigo;
    public Par Cliente { get; } = cliente;
    public Par? Tecnico { get; set; }
    public Atendimento Atendimento { get; } = atendimento;
    public bool OfertaEncaminhada { get; set; }
    public bool RespostaEncaminhada { get; set; }
}

internal sealed partial class Sessoes(Configuracao config, ModoSinalizacao modo, TimeProvider relogio, CodigosEmitidos codigos,
    ILogger<Sessoes> log, HistoricoWeb historico) : IDisposable
{
    private readonly object _bloqueio = new();
    private readonly Dictionary<string, Sessao> _abertas = [];
    private readonly HashSet<Sessao> _sessoes = [];
    private readonly FixedWindowRateLimiter _registro = Limite(180);
    private readonly FixedWindowRateLimiter _global = Limite(120);
    private readonly FixedWindowRateLimiter? _testeLocal = modo.TesteLocal ? Limite(20) : null;
    private readonly PrazosAtendimento _prazos = new(
        TimeSpan.FromSeconds(config.ValidadeCodigoSegundos), TimeSpan.FromSeconds(config.ValidadeSolicitacaoSegundos),
        TimeSpan.FromMinutes(config.SessaoMaximaMinutos), TimeSpan.FromMinutes(config.InatividadeMaximaMinutos));
    private readonly Dictionary<string, (string Nome, byte[] Hash, FixedWindowRateLimiter Limite)> _tecnicos =
        modo.TesteLocal ? new(StringComparer.Ordinal) :
            config.Tecnicos.ToDictionary(x => x.Key, x => (x.Key, Hash(x.Value), Limite(5)), StringComparer.Ordinal);

    private static byte[] Hash(string valor) => SHA256.HashData(Encoding.UTF8.GetBytes(valor));
    private static FixedWindowRateLimiter Limite(int permissoes) => new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = permissoes,
        Window = TimeSpan.FromMinutes(1),
        AutoReplenishment = true
    });

    public void Registrar(Par par)
    {
        lock (_bloqueio)
        {
            if (par.Sessao is not null) return;
            using var permissao = _registro.AttemptAcquire();
            string? codigo = permissao.IsAcquired ? codigos.Emitir() : null;
            if (codigo is null)
            {
                par.Enviar(new { tipo = "erro", mensagem = "Não foi possível emitir um código. Tente novamente mais tarde." });
                return;
            }
            var sessao = new Sessao(par, codigo, new Atendimento(relogio, _prazos));
            par.Sessao = sessao;
            _abertas.Add(codigo, sessao);
            _sessoes.Add(sessao);
            par.Enviar(new { tipo = "registrado", codigo });
        }
    }

    private (string Nome, FixedWindowRateLimiter Limite)? Autenticar(string credencial)
    {
        if (_testeLocal is not null) return ("Técnico local", _testeLocal);
        if (credencial.Length is < 32 or > 256) return null;
        var hash = Hash(credencial);
        (string, FixedWindowRateLimiter)? autorizado = null;
        foreach (var tecnico in _tecnicos.Values)
        {
            if (!CryptographicOperations.FixedTimeEquals(tecnico.Hash, hash)) continue;
            autorizado = (tecnico.Nome, tecnico.Limite);
        }
        CryptographicOperations.ZeroMemory(hash);
        return autorizado;
    }

    public void Solicitar(Par par, string codigo, string credencial)
    {
        if (Autenticar(credencial) is not { } tecnico) { ErroCredencial(par); return; }
        using var porTecnico = tecnico.Limite.AttemptAcquire();
        using var global = porTecnico.IsAcquired ? _global.AttemptAcquire() : null;
        if (global?.IsAcquired != true)
        {
            par.Enviar(new { tipo = "erro", mensagem = "Limite de tentativas atingido. Aguarde um minuto." });
            return;
        }

        lock (_bloqueio)
        {
            if (par.Sessao is not null) return;
            if (codigo.Length != 6 || !_abertas.TryGetValue(codigo, out var sessao) || !sessao.Atendimento.Solicitar())
            {
                par.Enviar(new { tipo = "erro", mensagem = "Código inválido ou indisponível." });
                return;
            }
            _abertas.Remove(codigo);
            sessao.Tecnico = par;
            par.Sessao = sessao;
            par.Enviar(new { tipo = "aguardando", atendimento = sessao.Id });
            sessao.Cliente.Enviar(new { tipo = "solicitacao", tecnico = tecnico.Nome, atendimento = sessao.Id });
            log.LogInformation("Solicitação de atendimento {Atendimento}", sessao.Id);
        }
    }

    private static void ErroCredencial(Par par) =>
        par.Enviar(new { tipo = "erro", mensagem = "Credencial do técnico inválida." });

    public void Responder(Par par, bool aceita)
    {
        lock (_bloqueio)
        {
            var sessao = par.Sessao;
            if (!par.Cliente || sessao?.Tecnico is null || sessao.Atendimento.Estado != EstadoAtendimento.Solicitado ||
                EncerrarPorPrazo(sessao)) return;
            if (!aceita) { Encerrar(sessao, "Solicitação recusada pelo cliente.", "recusado"); return; }
            if (!sessao.Atendimento.Aceitar()) { EncerrarPorPrazo(sessao); return; }
            var agora = relogio.GetUtcNow();
            if (par.Convite is { } convite) convite.Autorizado = agora;
            sessao.Cliente.Enviar(new { tipo = "aceita", sessao = sessao.Id, ice = config.Ice(agora, sessao.Id, true) });
            sessao.Tecnico.Enviar(new { tipo = "aceita", sessao = sessao.Id, ice = config.Ice(agora, sessao.Id, false) });
            log.LogInformation("Atendimento {Atendimento} autorizado", sessao.Id);
        }
    }

    public void Renovar(Par par, string identificador, int sequencia, bool atividade)
    {
        lock (_bloqueio)
        {
            if (par.Sessao is not { Atendimento.Estado: EstadoAtendimento.Ativo } sessao ||
                sessao.Id != identificador || sequencia <= 0 || EncerrarPorPrazo(sessao)) return;
            if (sessao.Atendimento.Renovar(par.Cliente, atividade))
                par.Enviar(new { tipo = "autorizacao", sessao = identificador, sequencia });
        }
    }

    public void Sinal(Par par, string identificador, JsonElement dados)
    {
        var sinal = SinalValidado.Ler(dados, par.Cliente);
        if (sinal is null) return;
        lock (_bloqueio)
        {
            if (par.Sessao is not { Atendimento.Estado: EstadoAtendimento.Ativo, Tecnico: not null } sessao ||
                sessao.Id != identificador || EncerrarPorPrazo(sessao)) return;
            if (sinal.Descricao)
            {
                if (par.Cliente)
                {
                    if (sessao.OfertaEncaminhada) return;
                    sessao.OfertaEncaminhada = true;
                }
                else
                {
                    if (!sessao.OfertaEncaminhada || sessao.RespostaEncaminhada) return;
                    sessao.RespostaEncaminhada = true;
                }
            }
            (par.Cliente ? sessao.Tecnico : sessao.Cliente).Enviar(new { tipo = "sinal", sessao = sessao.Id, dados = sinal.Dados });
        }
    }

    public void Sair(Par par, bool falhaNoTransporte = false)
    {
        lock (_bloqueio)
        {
            if (par.Sessao is { } sessao)
                Encerrar(sessao, falhaNoTransporte ? "O transporte do atendimento foi interrompido." :
                    par.Cliente ? "O cliente encerrou o atendimento." : "O técnico encerrou o atendimento.",
                    falhaNoTransporte ? "transporte" : "encerrado");
            else if (par.Convite is { } convite && convite.Tecnico == par)
            {
                convite.Tecnico = null;
                par.Convite = null;
            }
        }
    }

    public void Expirar()
    {
        lock (_bloqueio)
        {
            foreach (var sessao in _sessoes.Where(s => s.Atendimento.Expiracao() is not null).ToArray())
                EncerrarPorPrazo(sessao);
            foreach (var convite in _convites.Values.Where(c => c.Cliente?.Sessao?.Tecnico is null &&
                         c.Vence <= relogio.GetUtcNow()).ToArray())
                FinalizarConvite(convite, "expirado", notificar: true);
        }
    }

    private bool EncerrarPorPrazo(Sessao sessao)
    {
        if (sessao.Atendimento.Expiracao() is not { } motivo) return false;
        string mensagem = motivo switch
        {
            MotivoExpiracao.Codigo => "Código expirado.",
            MotivoExpiracao.Duracao => "Sessão expirada.",
            MotivoExpiracao.Inatividade => "Atendimento encerrado por inatividade.",
            _ => "A autorização foi encerrada após perda de conexão. Inicie um novo atendimento."
        };
        Encerrar(sessao, mensagem, motivo.ToString().ToLowerInvariant());
        return true;
    }

    private void Encerrar(Sessao sessao, string mensagem, string motivo)
    {
        if (sessao.Cliente.Convite is { } convite)
            FinalizarConvite(convite, motivo, notificar: false);
        _abertas.Remove(sessao.Codigo);
        _sessoes.Remove(sessao);
        if (sessao.Atendimento.Estado != EstadoAtendimento.Aguardando)
            log.LogInformation("Atendimento {Atendimento} encerrado. Motivo {Motivo}", sessao.Id, motivo);
        sessao.Atendimento.Encerrar();
        var evento = new { tipo = "encerrada", mensagem, motivo, atendimento = sessao.Id };
        sessao.Cliente.Sessao = null;
        sessao.Cliente.Enviar(evento);
        if (sessao.Tecnico is { } tecnico)
        {
            tecnico.Sessao = null;
            tecnico.Enviar(evento);
        }
    }

    public void Dispose()
    {
        _global.Dispose();
        _registro.Dispose();
        _testeLocal?.Dispose();
        foreach (var tecnico in _tecnicos.Values) tecnico.Limite.Dispose();
    }
}

internal sealed class Expiracao(Sessoes sessoes) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken)) sessoes.Expirar();
    }
}
