using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using SyncDesk;
using SyncDesk.Arquivos;
using SyncDesk.Controle;
using SyncDesk.Diagnostico;

await Caso("Mouse e teclado locais mantêm a prioridade enquanto estiverem em uso", _ =>
{
    var prioridade = new PrioridadeEntradaLocal();
    Exigir(!prioridade.Pausada(1000), "Controle remoto pausado sem entrada local.");
    prioridade.Registrar(1000);
    prioridade.Registrar(1300);
    Exigir(prioridade.Pausada(1500), "Um novo movimento deve renovar a pausa.");
    Exigir(!prioridade.Pausada(1800), "Controle remoto deve retomar após 500 ms.");
    prioridade.RegistrarBotao(2000, 0, true);
    prioridade.RegistrarTecla(2010, 0xA2, true);
    Exigir(prioridade.BotaoPressionado(0) && prioridade.TeclaPressionada(0xA2),
        "O botão e a tecla locais devem permanecer protegidos.");
    prioridade.RegistrarBotao(5000, 0, false);
    Exigir(prioridade.Pausada(6000), "Soltar o mouse não deve interromper o uso do teclado local.");
    prioridade.RegistrarTecla(6500, 0xA2, false);
    Exigir(prioridade.Pausada(6999) && !prioridade.Pausada(7000),
        "Controle remoto deve retomar 500 ms depois da última soltura.");
    return Task.CompletedTask;
});
await Caso("Técnico → cliente: lote, arquivo vazio e colisão de nomes", TecnicoParaCliente);
await Caso("Cliente → técnico: múltiplas janelas de dados", ClienteParaTecnico);
await Caso("Cancelamento no meio do arquivo", pasta => Interromper(pasta, cancelar: true));
await Caso("Queda da conexão no meio do arquivo", pasta => Interromper(pasta, cancelar: false));
await Caso("Hash incorreto descarta o temporário", IntegridadeInvalida);
await Caso("Nomes fora da pasta são rejeitados", NomesInvalidos);
await Caso("Histórico local opt-in e removido ao desativar", Historico);
await Caso("Diagnóstico limita campos e rotaciona o arquivo", Diagnostico);

static async Task Caso(string nome, Func<string, Task> executar)
{
    string pasta = Path.Combine(Path.GetTempPath(), "syncdesk-teste-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(pasta);
    try
    {
        await executar(pasta);
        Console.WriteLine("OK " + nome);
    }
    finally { Directory.Delete(pasta, recursive: true); }
}

static async Task TecnicoParaCliente(string pasta)
{
    string origem = Directory.CreateDirectory(Path.Combine(pasta, "origem")).FullName;
    string destino = Directory.CreateDirectory(Path.Combine(pasta, "destino")).FullName;
    string original = Path.Combine(origem, "relatório.txt");
    string vazio = Path.Combine(origem, "vazio.txt");
    File.WriteAllText(original, "dados do técnico");
    File.WriteAllBytes(vazio, []);
    File.WriteAllText(Path.Combine(destino, "relatório.txt"), "arquivo existente");
    await using var par = new ParTeste();
    par.Tecnico.Copiar([original, vazio]);
    await Esperar(() => par.Cliente.OfertaRemota.Length > 0);
    par.Cliente.Colar(destino, par.Cliente.OfertaRemota);
    await Esperar(() => par.StatusCliente.Any(s => !s.Ativo && s.Texto.Contains("Renomeados")));
    await Esperar(() => par.StatusTecnico.Any(s => !s.Ativo && s.Texto.Contains("Concluído")));
    Exigir(File.ReadAllText(Path.Combine(destino, "relatório.txt")) == "arquivo existente", "Arquivo sobrescrito.");
    Exigir(File.ReadAllText(Path.Combine(destino, "relatório (2).txt")) == "dados do técnico", "Conteúdo divergente.");
    Exigir(new FileInfo(Path.Combine(destino, "vazio.txt")).Length == 0, "Arquivo vazio perdido.");
    SemTemporarios(destino);
}

static async Task ClienteParaTecnico(string pasta)
{
    string destino = Directory.CreateDirectory(Path.Combine(pasta, "destino")).FullName;
    string arquivo = Path.Combine(pasta, "dados.bin");
    var dados = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 17);
    File.WriteAllBytes(arquivo, dados);
    await using var par = new ParTeste();
    par.Cliente.Copiar([arquivo]);
    await Esperar(() => par.Tecnico.OfertaRemota.Length > 0);
    par.Tecnico.Colar(destino, par.Tecnico.OfertaRemota);
    await Esperar(() => par.StatusTecnico.Any(s => !s.Ativo && s.Texto.Contains("Concluído")));
    Exigir(dados.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(destino, "dados.bin"))), "Arquivo divergente.");
    SemTemporarios(destino);
}

static async Task Interromper(string pasta, bool cancelar)
{
    string destino = Directory.CreateDirectory(Path.Combine(pasta, "destino")).FullName;
    string arquivo = Path.Combine(pasta, "interrompido.bin");
    File.WriteAllBytes(arquivo, RandomNumberGenerator.GetBytes(4 * 1024 * 1024));
    var janelaRecebida = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var par = new ParTeste(paraTecnico: m =>
    {
        if (m.Tipo != "janela") return m;
        janelaRecebida.TrySetResult();
        return null;
    });
    par.Tecnico.Copiar([arquivo]);
    await Esperar(() => par.Cliente.OfertaRemota.Length > 0);
    par.Cliente.Colar(destino, par.Cliente.OfertaRemota);
    await janelaRecebida.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Exigir(Directory.GetFiles(destino, "*.part").Length == 1, "Não houve gravação parcial.");
    if (cancelar)
    {
        par.Cliente.Cancelar(envio: false);
        await Esperar(() => par.StatusCliente.Any(s => s.Texto.Contains("cancelado")));
    }
    else
    {
        await par.DisposeAsync();
    }
    SemTemporarios(destino);
    Exigir(!File.Exists(Path.Combine(destino, "interrompido.bin")), "Arquivo incompleto publicado.");
}

static async Task IntegridadeInvalida(string pasta)
{
    string arquivo = Path.Combine(pasta, "hash.txt");
    string destino = Directory.CreateDirectory(Path.Combine(pasta, "destino")).FullName;
    File.WriteAllText(arquivo, "integridade");
    await using var par = new ParTeste(paraCliente: m =>
        m.Tipo == "fimArquivo" ? m with { Dados = new byte[32] } : m);
    par.Tecnico.Copiar([arquivo]);
    await Esperar(() => par.Cliente.OfertaRemota.Length > 0);
    par.Cliente.Colar(destino, par.Cliente.OfertaRemota);
    await Esperar(() => par.StatusCliente.Any(s => !s.Ativo && s.Texto.Contains("descartado")));
    Exigir(!File.Exists(Path.Combine(destino, "hash.txt")), "Arquivo corrompido publicado.");
    SemTemporarios(destino);
}

static async Task NomesInvalidos(string pasta)
{
    foreach (string nome in new[] { @"..\fora.txt", @"C:\fora.txt", "aux.txt", "COM¹.txt", "arquivo.txt:stream", "nome." })
        Exigir(!DestinoSeguro.NomeValido(nome), "Nome inválido aceito: " + nome);
    string arquivo = Path.Combine(pasta, "original.txt");
    string destino = Directory.CreateDirectory(Path.Combine(pasta, "destino")).FullName;
    File.WriteAllText(arquivo, "original");
    await using var par = new ParTeste(paraCliente: m => m.Tipo == "arquivo" ? m with { Texto = @"..\fora.txt" } : m);
    par.Tecnico.Copiar([arquivo]);
    await Esperar(() => par.Cliente.OfertaRemota.Length > 0);
    par.Cliente.Colar(destino, par.Cliente.OfertaRemota);
    await Esperar(() => par.StatusCliente.Any(s => !s.Ativo && s.Texto.Contains("descartado")));
    Exigir(!File.Exists(Path.Combine(pasta, "fora.txt")), "Escrita fora da pasta.");
    SemTemporarios(destino);
}

static void SemTemporarios(string pasta) =>
    Exigir(Directory.GetFiles(pasta, "*.part").Length == 0, "Arquivo temporário não foi descartado.");

static async Task Historico(string pasta)
{
    var preferencias = new PreferenciasTecnico(pasta);
    preferencias.Registrar(10);
    Exigir(!Directory.EnumerateFiles(pasta).Any(), "Atendimento foi guardado sem autorização.");
    preferencias.Configurar("escuro", true);
    preferencias.Registrar(55);
    await preferencias.Gravacao;
    var reabertura = new PreferenciasTecnico(pasta);
    await reabertura.Carregar();
    Exigir(reabertura.Atual is { Tema: "escuro", Historico: true, Recentes: [{ Segundos: 55 }] }, "Preferências divergentes.");
    reabertura.Configurar("escuro", false);
    await reabertura.Gravacao;
    await preferencias.Carregar();
    Exigir(!preferencias.Atual.Historico && preferencias.Atual.Recentes is null, "Histórico permaneceu após desativação.");
}

static async Task Diagnostico(string pasta)
{
    string atual = Path.Combine(pasta, "eventos.ndjson");
    File.WriteAllText(atual, new string('x', 256 * 1024));
    string correlacao = Guid.NewGuid().ToString("N");
    await using (var log = new DiagnosticoLocal(pasta))
    {
        log.Registrar("credencial");
        log.Registrar("rede_perdida", "123456");
        log.Registrar("sessao_ativa", correlacao);
    }
    string[] linhas = File.ReadAllLines(atual);
    Exigir(linhas.Length == 2 && File.Exists(Path.Combine(pasta, "eventos-anterior.ndjson")), "Rotação de diagnóstico inválida.");
    using var e1 = JsonDocument.Parse(linhas[0]);
    using var e2 = JsonDocument.Parse(linhas[1]);
    Exigir(e1.RootElement.GetProperty("atendimento").ValueKind == JsonValueKind.Null &&
        e2.RootElement.GetProperty("atendimento").GetString() == correlacao,
        "O diagnóstico persistiu um campo fora do formato permitido.");
}

static void Exigir(bool condicao, string mensagem)
{
    if (!condicao) throw new InvalidOperationException(mensagem);
}

static async Task Esperar(Func<bool> condicao)
{
    using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (!condicao()) await Task.Delay(20, prazo.Token);
}

sealed class ParTeste : IAsyncDisposable
{
    public TransferenciaSessao Tecnico { get; }
    public TransferenciaSessao Cliente { get; }
    public ConcurrentQueue<Progresso> StatusTecnico { get; } = new();
    public ConcurrentQueue<Progresso> StatusCliente { get; } = new();

    public ParTeste(Func<Mensagem, Mensagem?>? paraCliente = null, Func<Mensagem, Mensagem?>? paraTecnico = null)
    {
        Tecnico = new(true, m => Entregar(Cliente!, paraCliente, m), StatusTecnico.Enqueue);
        Cliente = new(false, m => Entregar(Tecnico, paraTecnico, m), StatusCliente.Enqueue);
    }

    private static Task Entregar(TransferenciaSessao destino, Func<Mensagem, Mensagem?>? filtro, Mensagem original)
    {
        var mensagem = filtro is null ? original : filtro(original);
        if (mensagem is not null) destino.Receber(mensagem);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await Task.WhenAll(Tecnico.DisposeAsync().AsTask(), Cliente.DisposeAsync().AsTask());
}
