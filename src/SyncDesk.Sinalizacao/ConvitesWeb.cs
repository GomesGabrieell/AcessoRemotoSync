using System.Security.Cryptography;
using System.Text;

namespace SyncDesk.Sinalizacao;

internal sealed class ConviteWeb(string dono, string cliente, DateTimeOffset agora, TimeSpan validade)
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Chave { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    public string Dono { get; } = dono;
    public string NomeCliente { get; } = cliente;
    public string Computador { get; set; } = "";
    public DateTimeOffset Criado { get; } = agora;
    public DateTimeOffset Vence { get; } = agora + validade;
    public DateTimeOffset? Autorizado { get; set; }
    public DateTimeOffset? Inicio { get; set; }
    public Par? Cliente { get; set; }
    public Par? Tecnico { get; set; }
    public bool Vinculado => Cliente is not null;

    public RegistroWeb Registro(DateTimeOffset? fim = null, string? motivo = null) => new(
        Id, Dono, NomeCliente, Computador, Criado, Inicio, fim, motivo ??
        (Inicio is not null ? "ativo" : Autorizado is not null ? "conectando" :
            Vinculado && Tecnico is not null ? "autorizacao" : "aguardando"));
}

internal sealed partial class Sessoes
{
    private readonly Dictionary<string, ConviteWeb> _convites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConviteWeb> _chaves = new(StringComparer.Ordinal);

    public bool ValidarLogin(string nome, string credencial)
    {
        if (nome.Length is < 1 or > 80 || credencial.Length is < 32 or > 256 ||
            !config.Tecnicos.TryGetValue(nome, out var esperado)) return false;
        byte[] tentativa = SHA256.HashData(Encoding.UTF8.GetBytes(credencial));
        byte[] referencia = SHA256.HashData(Encoding.UTF8.GetBytes(esperado));
        try { return CryptographicOperations.FixedTimeEquals(tentativa, referencia); }
        finally
        {
            CryptographicOperations.ZeroMemory(tentativa);
            CryptographicOperations.ZeroMemory(referencia);
        }
    }

    public object? CriarConvite(string dono, string nome)
    {
        lock (_bloqueio)
        {
            if (_convites.Count >= 512 || _convites.Values.Count(c => c.Dono == dono) >= 12) return null;
            var convite = new ConviteWeb(dono, nome, relogio.GetUtcNow(), TimeSpan.FromMinutes(config.ValidadeConviteMinutos));
            _convites.Add(convite.Id, convite);
            _chaves.Add(convite.Chave, convite);
            log.LogInformation("Convite {Convite} criado por {Tecnico}", convite.Id, dono);
            return Detalhe(convite);
        }
    }

    public object PainelWeb(string dono)
    {
        lock (_bloqueio)
            return new
            {
                tecnico = dono,
                atuais = _convites.Values.Where(c => c.Dono == dono).OrderByDescending(c => c.Criado).Select(Detalhe).ToArray(),
                historico = historico.De(dono)
            };
    }

    private static object Detalhe(ConviteWeb c) => new
    {
        registro = c.Registro(), url = c.Vinculado ? null : "/atendimento/" + c.Chave, vence = c.Vence
    };

    public bool ConviteDisponivel(string chave)
    {
        lock (_bloqueio)
            return chave.Length == 48 && _chaves.TryGetValue(chave, out var c) && c.Vence > relogio.GetUtcNow();
    }

    public bool Pertence(string id, string dono)
    {
        lock (_bloqueio)
            return _convites.TryGetValue(id, out var convite) && convite.Dono == dono;
    }

    public bool CancelarConvite(string id, string dono)
    {
        lock (_bloqueio)
        {
            if (!_convites.TryGetValue(id, out var c) || c.Dono != dono || c.Autorizado is not null) return false;
            if (c.Cliente?.Sessao is { Tecnico: not null } sessao)
                Encerrar(sessao, "O técnico cancelou o convite.", "cancelado");
            else FinalizarConvite(c, "cancelado", notificar: true);
            return true;
        }
    }

    public void AssociarConvite(Par cliente, string chave)
    {
        lock (_bloqueio)
        {
            if (cliente is not { Cliente: true, Convite: null, Sessao: { Tecnico: null } sessao } ||
                sessao.Atendimento.Expiracao() is not null || chave.Length != 48 ||
                !_chaves.TryGetValue(chave, out var c) || c.Vence <= relogio.GetUtcNow())
            {
                cliente.Enviar(new { tipo = "conviteInvalido" });
                return;
            }
            _chaves.Remove(chave);
            _abertas.Remove(sessao.Codigo);
            c.Cliente = cliente;
            cliente.Convite = c;
            cliente.Enviar(new { tipo = "conviteVinculado" });
            log.LogInformation("Convite {Convite} associado a {Atendimento}", c.Id, sessao.Id);
            Parear(c);
        }
    }

    public void AcompanharConvite(Par tecnico, string id, string dono)
    {
        lock (_bloqueio)
        {
            if (tecnico.Convite is not null || !_convites.TryGetValue(id, out var c) ||
                c.Dono != dono || c.Tecnico is not null || c.Vence <= relogio.GetUtcNow())
            {
                tecnico.Enviar(new { tipo = "erro", mensagem = "Convite indisponível ou aberto em outra janela." });
                return;
            }
            c.Tecnico = tecnico;
            tecnico.Convite = c;
            tecnico.Enviar(new { tipo = "conviteAguardando" });
            Parear(c);
        }
    }

    public void RegistrarVisualizacao(Par tecnico, string computador)
    {
        lock (_bloqueio)
        {
            if (tecnico.Convite is not { Autorizado: not null } convite || convite.Tecnico != tecnico ||
                tecnico.Sessao is not { } sessao || EncerrarPorPrazo(sessao)) return;
            convite.Inicio ??= relogio.GetUtcNow();
            convite.Computador = new string(computador.Where(c => !char.IsControl(c)).Take(80).ToArray());
        }
    }

    private void Parear(ConviteWeb convite)
    {
        if (convite.Cliente?.Sessao is not { } s || convite.Tecnico is not { } tecnico || !s.Atendimento.Solicitar()) return;
        s.Tecnico = tecnico;
        tecnico.Sessao = s;
        tecnico.Enviar(new { tipo = "aguardando", atendimento = s.Id });
        s.Cliente.Enviar(new { tipo = "solicitacao", tecnico = convite.Dono, atendimento = s.Id });
    }

    private void FinalizarConvite(ConviteWeb convite, string motivo, bool notificar)
    {
        _convites.Remove(convite.Id);
        _chaves.Remove(convite.Chave);
        if (convite.Cliente is { } cliente) cliente.Convite = null;
        if (convite.Tecnico is { } tecnico) tecnico.Convite = null;
        historico.Gravar(convite.Registro(relogio.GetUtcNow(), motivo));
        log.LogInformation("Convite {Convite} finalizado por {Motivo}. Técnico {Tecnico}", convite.Id, motivo, convite.Dono);
        if (!notificar) return;
        var evento = new { tipo = "encerrada", mensagem = "Convite finalizado. Solicite um novo link de atendimento.", motivo };
        convite.Cliente?.Enviar(evento);
        convite.Tecnico?.Enviar(evento);
    }
}
