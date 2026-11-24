using System.Security.Claims;

namespace SyncDesk.Sinalizacao;

internal sealed class SessoesLoginWeb(TimeProvider relogio)
{
    public static readonly TimeSpan Validade = TimeSpan.FromHours(8);
    private readonly Dictionary<string, (DateTimeOffset Vence, CancellationTokenSource Vida)> _sessoes = [];
    private readonly object _bloqueio = new();

    public string? Abrir()
    {
        lock (_bloqueio)
        {
            var agora = relogio.GetUtcNow();
            foreach (var chave in _sessoes.Where(s => s.Value.Vence <= agora).Select(s => s.Key).ToArray())
                _sessoes.Remove(chave);
            if (_sessoes.Count >= 256) return null;
            string id = Guid.NewGuid().ToString("N");
            _sessoes.Add(id, (agora + Validade, new()));
            return id;
        }
    }

    public CancellationToken? Vigencia(ClaimsPrincipal? principal)
    {
        string? id = principal?.FindFirstValue(ClaimTypes.Sid);
        lock (_bloqueio)
            return id is not null && _sessoes.TryGetValue(id, out var sessao) && sessao.Vence > relogio.GetUtcNow()
                ? sessao.Vida.Token : null;
    }

    public void Sair(ClaimsPrincipal tecnico)
    {
        string? id = tecnico.FindFirstValue(ClaimTypes.Sid);
        CancellationTokenSource? vida = null;
        lock (_bloqueio)
            if (id is not null && _sessoes.Remove(id, out var sessao)) vida = sessao.Vida;
        vida?.Cancel();
    }
}
