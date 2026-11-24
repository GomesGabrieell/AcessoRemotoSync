using System.Security.Cryptography;
using System.Text;

namespace SyncDesk.Sinalizacao;

internal sealed record ModoSinalizacao(bool TesteLocal, bool TesteWebLocal);

public sealed class Configuracao
{
    public Dictionary<string, string> Tecnicos { get; init; } = [];
    public string[] Stun { get; init; } = [];
    public string[] Turn { get; init; } = [];
    public string? SegredoTurn { get; init; }
    public bool ForcarTurn { get; init; }
    public int ValidadeCodigoSegundos { get; init; } = 300;
    public int ValidadeConviteMinutos { get; init; } = 10;
    public int ValidadeSolicitacaoSegundos { get; init; } = 45;
    public int SessaoMaximaMinutos { get; init; } = 120;
    public int InatividadeMaximaMinutos { get; init; } = 15;
    public int LimiteConexoes { get; init; } = 256;
    public string? ExecutavelCliente { get; init; }
    public string? ArquivoHistoricoWeb { get; init; }
    public string ArquivoCodigos { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SyncDesk", "sinalizacao", "codigos.bin");

    public void Validar(bool testeLocal = false)
    {
        if ((!testeLocal && Tecnicos.Count == 0) || Tecnicos.Any(t => string.IsNullOrWhiteSpace(t.Key) ||
                t.Key.Length > 80 || t.Value is null || t.Value.Length < 32 || t.Value.Length > 256) ||
            Tecnicos.Values.Distinct(StringComparer.Ordinal).Count() != Tecnicos.Count)
            throw new InvalidOperationException("Configure pelo menos um técnico com uma credencial aleatória e exclusiva de 32 a 256 caracteres.");
        if (ValidadeCodigoSegundos is < 1 or > 900 || ValidadeConviteMinutos is < 1 or > 60 ||
            ValidadeSolicitacaoSegundos is < 1 or > 120 ||
            SessaoMaximaMinutos is < 1 or > 480 || InatividadeMaximaMinutos is < 1 or > 120 ||
            LimiteConexoes is < 2 or > 10000 ||
            string.IsNullOrWhiteSpace(ArquivoCodigos))
            throw new InvalidOperationException("Os limites de validade ou capacidade da sinalização são inválidos.");
        if (Stun.Any(url => !url.StartsWith("stun:", StringComparison.Ordinal)) ||
            Turn.Any(url => !url.StartsWith("turn:", StringComparison.Ordinal) && !url.StartsWith("turns:", StringComparison.Ordinal)) ||
            (Turn.Length > 0 && (SegredoTurn?.Length ?? 0) < 32) || (ForcarTurn && Turn.Length == 0))
            throw new InvalidOperationException("Configure URLs ICE válidas e um segredo TURN de pelo menos 32 caracteres.");
    }

    public object Ice(DateTimeOffset agora, string sessao, bool cliente)
    {
        var servidores = new List<object>();
        if (Stun.Length > 0) servidores.Add(new { urls = Stun });
        if (Turn.Length > 0)
        {
            string usuario = $"{agora.AddMinutes(SessaoMaximaMinutos + 5).ToUnixTimeSeconds()}:{sessao}:{(cliente ? "cliente" : "tecnico")}";
            var chave = Encoding.UTF8.GetBytes(SegredoTurn!);
            string senha = Convert.ToBase64String(HMACSHA1.HashData(chave, Encoding.UTF8.GetBytes(usuario)));
            servidores.Add(new { urls = Turn, username = usuario, credential = senha });
        }
        return new { iceServers = servidores, iceTransportPolicy = ForcarTurn ? "relay" : "all" };
    }
}
