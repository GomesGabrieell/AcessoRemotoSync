using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace SyncDesk.Arquivos;

internal sealed class TransferenciaSessao : IAsyncDisposable
{
    private const int Bloco = 11 * 1024, Janela = 12, MaxArquivos = 256;
    private readonly Channel<Func<Task>> _fila = Channel.CreateBounded<Func<Task>>(64);
    private readonly CancellationTokenSource _vida = new();
    private readonly Func<Mensagem, Task> _enviar;
    private readonly Action<Progresso> _progresso;
    private readonly Task _processar, _vigiar;
    private readonly TaskCompletionSource _encerrada = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _lado;
    private long _relogio, _versao;
    private string[] _arquivos = [];
    private string _ofertaLocal = "", _ofertaRemota = "";
    private Recepcao? _recepcao;
    private Envio? _envio;
    private int _terminou;

    public TransferenciaSessao(bool tecnico, Func<Mensagem, Task> enviar, Action<Progresso> progresso)
    {
        _lado = tecnico ? 1 : 0;
        _enviar = enviar;
        _progresso = progresso;
        _processar = Task.Run(Processar);
        _vigiar = Task.Run(Vigiar);
    }

    public string OfertaRemota => Volatile.Read(ref _ofertaRemota);

    public void Copiar(string[] arquivos)
    {
        Agendar(async () =>
        {
            _arquivos = arquivos.Length <= MaxArquivos ? arquivos : [];
            _ofertaLocal = _arquivos.Length > 0 ? Guid.NewGuid().ToString("N") : "";
            Volatile.Write(ref _ofertaRemota, "");
            _versao = ++_relogio * 2 + _lado;
            await _enviar(new("oferta", _ofertaLocal, _versao));
            if (arquivos.Length > MaxArquivos)
                Publicar(false, false, "Copie até 256 arquivos por vez.");
        });
    }

    public void Colar(string pasta, string oferta) => Agendar(async () =>
    {
        if (oferta != _ofertaRemota || oferta.Length == 0) return;
        if (_recepcao is not null)
        {
            Publicar(false, true, "Já existe um recebimento em andamento.", _recepcao.Bytes, _recepcao.Total, _recepcao.Relogio);
            return;
        }
        try
        {
            _recepcao = new(Guid.NewGuid().ToString("N"), new DestinoSeguro(pasta));
            Publicar(false, true, "Solicitando arquivos…");
            await _enviar(new("colar", _recepcao.Id, Texto: oferta));
        }
        catch (Exception e) when (FalhaTratavel(e))
        {
            await AbortarRecepcao("Não foi possível abrir a pasta de destino.", avisoSemRecepcao: true);
        }
    });

    public void Receber(Mensagem mensagem) => Agendar(() => Tratar(mensagem));

    public void Cancelar(bool envio) => Agendar(async () =>
    {
        if (!envio)
        {
            await AbortarRecepcao("Recebimento cancelado.", falha: false);
            return;
        }
        if (_envio is { } atual)
        {
            atual.Cancelar.Cancel();
            await _enviar(new("cancelar", atual.Id));
        }
    });

    private void Agendar(Func<Task> tarefa)
    {
        if (!_fila.Writer.TryWrite(tarefa) && !_vida.IsCancellationRequested)
            _ = DisposeAsync();
    }

    private async Task Processar()
    {
        try
        {
            await foreach (var tarefa in _fila.Reader.ReadAllAsync(_vida.Token))
                await tarefa();
        }
        catch (OperationCanceledException) when (_vida.IsCancellationRequested) { }
        finally
        {
            _recepcao?.Dispose();
            _recepcao = null;
            if (_envio is { } envio)
            {
                envio.Cancelar.Cancel();
                if (envio.Trabalho is not null) await envio.Trabalho;
                envio.Cancelar.Dispose();
                _envio = null;
            }
            Volatile.Write(ref _ofertaRemota, "");
        }
    }

    private async Task Vigiar()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_vida.Token))
                Agendar(async () =>
                {
                    if (_recepcao is { } atual && Stopwatch.GetElapsedTime(atual.UltimaMensagem).TotalSeconds >= 40)
                        await AbortarRecepcao("A transferência foi interrompida por falta de resposta.");
                });
        }
        catch (OperationCanceledException) when (_vida.IsCancellationRequested) { }
    }

    private async Task Tratar(Mensagem m)
    {
        if (m.Id is null || m.Texto is null || m.Tipo is null) return;
        if (m.Tipo == "oferta")
        {
            if (m.Valor <= 0 || m.Valor > 2_000_000_000 || (m.Valor & 1) == _lado ||
                (m.Id != "" && !Guid.TryParseExact(m.Id, "N", out _))) return;
            _relogio = Math.Max(_relogio, m.Valor / 2);
            if (m.Valor > _versao)
            {
                _versao = m.Valor;
                _ofertaLocal = "";
                Volatile.Write(ref _ofertaRemota, m.Id);
            }
            return;
        }
        if (!Guid.TryParseExact(m.Id, "N", out _)) return;
        if (m.Tipo == "colar")
        {
            if (_envio is not null || _ofertaLocal == "" || m.Texto != _ofertaLocal)
            {
                await _enviar(new("erro", m.Id));
                return;
            }
            var envio = _envio = new(m.Id);
            string[] selecionados = _arquivos;
            envio.Trabalho = Task.Run(() => EnviarLote(envio, selecionados));
            return;
        }
        if (_envio is { } atual && m.Id == atual.Id)
        {
            if (m.Tipo is "cancelar" or "erro") atual.Cancelar.Cancel();
            else if (m.Tipo is "pronto" or "janela" or "gravado" or "concluido")
            {
                if (!atual.Respostas.Writer.TryWrite(m)) atual.Cancelar.Cancel();
            }
            return;
        }
        if (_recepcao is not { } entrada || m.Id != entrada.Id) return;
        entrada.UltimaMensagem = Stopwatch.GetTimestamp();
        try { await ReceberDados(entrada, m); }
        catch (Exception e) when (FalhaTratavel(e))
        {
            await AbortarRecepcao("Recebimento interrompido. Confira a pasta, o espaço livre e a integridade dos arquivos.");
        }
    }

    private async Task ReceberDados(Recepcao r, Mensagem m)
    {
        switch (m.Tipo)
        {
            case "lote" when r.Quantidade == 0 && m.Valor is > 0 and <= MaxArquivos &&
                long.TryParse(m.Texto, NumberStyles.None, CultureInfo.InvariantCulture, out long total):
                r.Quantidade = (int)m.Valor;
                r.Total = total;
                Publicar(false, true, "Recebendo arquivos…", r.Bytes, r.Total, r.Relogio);
                break;
            case "arquivo" when r.Stream is null && r.Concluidos < r.Quantidade &&
                m.Valor >= 0 && m.Valor <= r.Total - r.Bytes && DestinoSeguro.NomeValido(m.Texto):
                (r.Temporario, r.Stream) = r.Destino.AbrirTemporario();
                r.Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                r.Nome = m.Texto;
                r.Tamanho = m.Valor;
                r.Posicao = 0;
                await _enviar(new("pronto", r.Id, r.Concluidos));
                break;
            case "bloco" when r.Stream is not null && m.Dados is { Length: > 0 and <= Bloco } dados &&
                m.Valor == r.Posicao && dados.Length <= r.Tamanho - r.Posicao:
                await r.Stream.WriteAsync(dados, _vida.Token);
                r.Hash!.AppendData(dados);
                r.Posicao += dados.Length;
                r.Bytes += dados.Length;
                if (Stopwatch.GetElapsedTime(r.UltimoProgresso).TotalMilliseconds >= 250)
                {
                    r.UltimoProgresso = Stopwatch.GetTimestamp();
                    Publicar(false, true, r.Nome, r.Bytes, r.Total, r.Relogio);
                }
                break;
            case "janela" when r.Stream is not null && m.Valor == r.Posicao:
                await _enviar(new("janela", r.Id, r.Posicao));
                break;
            case "fimArquivo" when r.Stream is not null && r.Posicao == r.Tamanho && m.Dados is { Length: 32 } hash &&
                CryptographicOperations.FixedTimeEquals(r.Hash!.GetHashAndReset(), hash):
                await r.Stream.FlushAsync(_vida.Token);
                r.Stream.Flush(flushToDisk: true);
                await r.Stream.DisposeAsync();
                r.Stream = null;
                _vida.Token.ThrowIfCancellationRequested();
                string publicado = r.Destino.Publicar(r.Temporario!, r.Nome);
                r.Temporario = null;
                r.Hash.Dispose();
                r.Hash = null;
                if (publicado != r.Nome) r.Renomeados++;
                await _enviar(new("gravado", r.Id, r.Concluidos++, publicado));
                break;
            case "fim" when r.Quantidade > 0 && r.Concluidos == r.Quantidade && r.Total == r.Bytes:
                Publicar(false, false, Resumo(r.Concluidos, r.Renomeados), r.Bytes, r.Total);
                r.Dispose();
                _recepcao = null;
                await _enviar(new("concluido", r.Id, r.Concluidos));
                break;
            case "cancelar" or "erro":
                await AbortarRecepcao("O outro computador interrompeu o envio.", notificarPar: false, falha: m.Tipo == "erro");
                break;
            default:
                await AbortarRecepcao("Dados inválidos. O arquivo incompleto foi descartado.");
                break;
        }
    }

    private async Task AbortarRecepcao(string texto, bool notificarPar = true, bool avisoSemRecepcao = false, bool falha = true)
    {
        var antiga = _recepcao;
        _recepcao = null;
        if (antiga is null && !avisoSemRecepcao) return;
        antiga?.Dispose();
        Publicar(false, false, antiga is { Concluidos: > 0 } ? texto + $" Arquivos concluídos: {antiga.Concluidos}." : texto,
            falha: falha);
        if (notificarPar && antiga is not null) await _enviar(new("cancelar", antiga.Id));
    }

    private async Task EnviarLote(Envio envio, string[] caminhos)
    {
        var relogio = Stopwatch.StartNew();
        using var cancelamento = CancellationTokenSource.CreateLinkedTokenSource(_vida.Token, envio.Cancelar.Token);
        var token = cancelamento.Token;
        try
        {
            Publicar(true, true, "Preparando envio…");
            var arquivos = caminhos.Select(c => new FileInfo(DestinoSeguro.CaminhoLocal(c))).ToArray();
            long total = arquivos.Sum(a =>
            {
                if (!DestinoSeguro.NomeValido(a.Name) ||
                    (a.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new IOException("Somente arquivos locais comuns podem ser enviados.");
                return a.Length;
            });
            await _enviar(new("lote", envio.Id, arquivos.Length, total.ToString(CultureInfo.InvariantCulture)));
            long bytes = 0;
            int renomeados = 0;
            var buffer = new byte[Bloco];
            for (int i = 0; i < arquivos.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var arquivo = arquivos[i];
                using var pai = new DestinoSeguro(arquivo.DirectoryName!);
                await using var fonte = new FileStream(arquivo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                    Bloco, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (fonte.Length != arquivo.Length || (File.GetAttributes(arquivo.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("O arquivo de origem foi alterado.");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await _enviar(new("arquivo", envio.Id, fonte.Length, arquivo.Name));
                await Esperar(envio, "pronto", i, token);
                long posicao = 0;
                while (posicao < fonte.Length)
                {
                    for (int j = 0; j < Janela && posicao < fonte.Length; j++)
                    {
                        int n = await fonte.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, fonte.Length - posicao)), token);
                        if (n == 0) throw new EndOfStreamException();
                        hash.AppendData(buffer, 0, n);
                        await _enviar(new("bloco", envio.Id, posicao, Dados: buffer[..n]));
                        posicao += n;
                    }
                    await _enviar(new("janela", envio.Id, posicao));
                    await Esperar(envio, "janela", posicao, token);
                    if (Stopwatch.GetElapsedTime(envio.UltimoProgresso).TotalMilliseconds >= 250)
                    {
                        envio.UltimoProgresso = Stopwatch.GetTimestamp();
                        Publicar(true, true, arquivo.Name, bytes + posicao, total, relogio);
                    }
                }
                await _enviar(new("fimArquivo", envio.Id, Dados: hash.GetHashAndReset()));
                var gravado = await Esperar(envio, "gravado", i, token);
                if (gravado.Texto != arquivo.Name) renomeados++;
                bytes += posicao;
            }
            await _enviar(new("fim", envio.Id));
            await Esperar(envio, "concluido", arquivos.Length, token);
            Publicar(true, false, Resumo(arquivos.Length, renomeados), bytes, total);
        }
        catch (Exception e) when (FalhaTratavel(e))
        {
            Publicar(true, false, token.IsCancellationRequested ? "Envio cancelado ou desconectado." :
                "Envio interrompido. Verifique se os arquivos continuam disponíveis.", falha: !token.IsCancellationRequested);
            if (!_vida.IsCancellationRequested) await _enviar(new("erro", envio.Id));
        }
        finally
        {
            Agendar(() =>
            {
                if (_envio == envio) _envio = null;
                envio.Cancelar.Dispose();
                return Task.CompletedTask;
            });
        }
    }

    private static async Task<Mensagem> Esperar(Envio envio, string tipo, long valor, CancellationToken token)
    {
        var resposta = await envio.Respostas.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(35), token);
        if (resposta.Tipo != tipo || resposta.Valor != valor) throw new IOException("Resposta de transferência inválida.");
        return resposta;
    }

    private static string Resumo(int arquivos, int renomeados) =>
        $"Concluído: {arquivos} arquivo(s)." + (renomeados == 0 ? "" : $" Renomeados para evitar substituição: {renomeados}.");

    private void Publicar(bool envio, bool ativo, string texto, long bytes = 0, long total = 0, Stopwatch? relogio = null, bool falha = false) =>
        _progresso(new(envio, ativo, texto, bytes, total, relogio is null ? 0 : bytes / Math.Max(.1, relogio.Elapsed.TotalSeconds), falha));

    private static bool FalhaTratavel(Exception e) =>
        e is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or ArgumentException or OverflowException;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _terminou, 1) != 0)
        {
            await _encerrada.Task;
            return;
        }
        try
        {
            _fila.Writer.TryComplete();
            await _vida.CancelAsync();
            await Task.WhenAll(_processar, _vigiar);
        }
        finally
        {
            _vida.Dispose();
            _encerrada.TrySetResult();
        }
    }

    private sealed class Envio(string id)
    {
        public string Id { get; } = id;
        public CancellationTokenSource Cancelar { get; } = new();
        public Channel<Mensagem> Respostas { get; } = Channel.CreateBounded<Mensagem>(8);
        public Task? Trabalho { get; set; }
        public long UltimoProgresso { get; set; }
    }

    private sealed class Recepcao(string id, DestinoSeguro destino) : IDisposable
    {
        public string Id { get; } = id;
        public DestinoSeguro Destino { get; } = destino;
        public Stopwatch Relogio { get; } = Stopwatch.StartNew();
        public long UltimaMensagem { get; set; } = Stopwatch.GetTimestamp();
        public long UltimoProgresso { get; set; }
        public int Quantidade, Concluidos, Renomeados;
        public long Bytes, Total, Posicao, Tamanho;
        public string Nome = "";
        public string? Temporario;
        public FileStream? Stream;
        public IncrementalHash? Hash;

        public void Dispose()
        {
            try
            {
                try { Stream?.Dispose(); }
                catch (IOException) { }
                Hash?.Dispose();
                try { if (Temporario is not null) File.Delete(Temporario); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            finally { Destino.Dispose(); }
        }
    }
}
