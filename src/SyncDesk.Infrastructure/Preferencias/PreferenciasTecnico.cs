using System.IO;
using System.Text.Json;

namespace SyncDesk;

internal sealed record AtendimentoRecente(DateTimeOffset Data, int Segundos);
internal sealed record PreferenciasPainel(string Tema = "sistema", bool Historico = false, AtendimentoRecente[]? Recentes = null);

internal sealed class PreferenciasTecnico(string? diretorio = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _arquivo = Path.Combine(diretorio ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SyncDesk"),
        "preferencias-tecnico.json");
    public PreferenciasPainel Atual { get; private set; } = new();
    public Task Gravacao { get; private set; } = Task.CompletedTask;

    public async Task Carregar()
    {
        try
        {
            if (new FileInfo(_arquivo) is not { Exists: true, Length: <= 8192 }) return;
            await using var stream = File.OpenRead(_arquivo);
            if (await JsonSerializer.DeserializeAsync<PreferenciasPainel>(stream, Json) is { } dados)
                Atual = new(TemaValido(dados.Tema), dados.Historico, dados.Historico
                    ? dados.Recentes?.Where(item => item is { Segundos: >= 0 and <= 28_800 }).Take(20).ToArray() : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public void Configurar(string tema, bool historico)
    {
        Atual = new(TemaValido(tema), historico, historico ? Atual.Recentes : null);
        Persistir();
    }

    public void Registrar(int segundos)
    {
        if (!Atual.Historico || segundos is < 0 or > 28_800) return;
        Atual = Atual with
        {
            Recentes = Atual.Recentes is { } recentes
                ? [new(DateTimeOffset.UtcNow, segundos), .. recentes.Take(19)]
                : [new(DateTimeOffset.UtcNow, segundos)]
        };
        Persistir();
    }

    private void Persistir()
    {
        var anterior = Gravacao;
        var dados = Atual;
        Gravacao = Task.Run(async () =>
        {
            await anterior;
            string temporario = _arquivo + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_arquivo)!);
                await File.WriteAllTextAsync(temporario, JsonSerializer.Serialize(dados, Json));
                File.Move(temporario, _arquivo, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            finally
            {
                try { File.Delete(temporario); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        });
    }

    private static string TemaValido(string? tema) => tema is "claro" or "escuro" ? tema : "sistema";
}
