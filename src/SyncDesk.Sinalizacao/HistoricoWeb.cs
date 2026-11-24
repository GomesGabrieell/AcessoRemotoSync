using System.Text.Json;

namespace SyncDesk.Sinalizacao;

internal sealed record RegistroWeb(string Id, string Dono, string Cliente, string Computador, DateTimeOffset Criado,
    DateTimeOffset? Inicio, DateTimeOffset? Fim, string Status);

internal sealed class HistoricoWeb
{
    private const int Limite = 1000;
    private readonly string _caminho;
    private readonly ILogger<HistoricoWeb> _log;
    private readonly List<RegistroWeb> _registros;

    public HistoricoWeb(Configuracao config, ILogger<HistoricoWeb> log)
    {
        _log = log;
        _caminho = Path.GetFullPath(config.ArquivoHistoricoWeb ??
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config.ArquivoCodigos))!, "atendimentos-web.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(_caminho)!);
        _registros = File.Exists(_caminho) ?
            JsonSerializer.Deserialize<List<RegistroWeb>>(File.ReadAllBytes(_caminho)) ?? [] : [];
    }

    public RegistroWeb[] De(string dono) => _registros.Where(r => r.Dono == dono).Reverse().ToArray();

    public void Gravar(RegistroWeb registro)
    {
        _registros.Add(registro);
        if (_registros.Count > Limite) _registros.RemoveRange(0, _registros.Count - Limite);
        var temporario = _caminho + ".tmp";
        try
        {
            using (var arquivo = File.Create(temporario))
            {
                JsonSerializer.Serialize(arquivo, _registros);
                arquivo.Flush(flushToDisk: true);
            }
            File.Move(temporario, _caminho, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogError(e, "Não foi possível persistir o histórico do atendimento {Atendimento}", registro.Id);
        }
    }
}
