using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace SyncDesk.Diagnostico;

internal sealed class DiagnosticoLocal : IAsyncDisposable
{
    private static readonly HashSet<string> Eventos = new(StringComparer.Ordinal)
    {
        "aplicativo_iniciado", "aplicativo_fechado", "painel_falhou", "sinalizacao_conectada",
        "solicitacao", "autorizado", "recusado", "sessao_ativa", "sessao_encerrada",
        "rede_perdida", "transporte_falhou", "transferencia_falhou"
    };
    private readonly Channel<string> _fila = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest
    });
    private readonly Task _escrita;
    private readonly string _diretorio;

    public DiagnosticoLocal(string? diretorio = null)
    {
        _diretorio = diretorio ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SyncDesk", "diagnostico");
        _escrita = Task.Run(Gravar);
    }

    public void Registrar(string evento, string? atendimento = null)
    {
        if (!Eventos.Contains(evento)) return;
        string? correlacao = Guid.TryParseExact(atendimento, "N", out var id) ? id.ToString("N") : null;
        _fila.Writer.TryWrite(JsonSerializer.Serialize(new { horario = DateTimeOffset.UtcNow, evento, atendimento = correlacao }));
    }

    private async Task Gravar()
    {
        using var exclusividade = new Mutex(false, @"Local\SyncDesk-diagnostico");
        await foreach (string linha in _fila.Reader.ReadAllAsync())
        {
            bool adquirido = false;
            try
            {
                try { adquirido = exclusividade.WaitOne(100); }
                catch (AbandonedMutexException) { adquirido = true; }
                if (!adquirido) continue;
                Directory.CreateDirectory(_diretorio);
                string arquivo = Path.Combine(_diretorio, "eventos.ndjson");
                if (File.Exists(arquivo) && new FileInfo(arquivo).Length >= 256 * 1024)
                    File.Move(arquivo, Path.Combine(_diretorio, "eventos-anterior.ndjson"), true);
                File.AppendAllText(arquivo, linha + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            finally { if (adquirido) exclusividade.ReleaseMutex(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _fila.Writer.TryComplete();
        await _escrita;
    }
}
