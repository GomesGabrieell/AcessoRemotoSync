using System.IO;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using SyncDesk.Arquivos;
using SyncDesk.Controle;
using SyncDesk.Diagnostico;

namespace SyncDesk.Views;

public partial class AtendimentoWindow : Window
{
    private const string OrigemPainel = "https://syncdesk-app.invalid";
    private static readonly Dictionary<string, string> Recursos = new(StringComparer.Ordinal)
    {
        ["/index.html"] = "text/html", ["/syncdesk.css"] = "text/css",
        ["/syncdesk.js"] = "application/javascript", ["/webrtc.js"] = "application/javascript",
        ["/controle.js"] = "application/javascript", ["/qualidade.js"] = "application/javascript",
        ["/painel.js"] = "application/javascript", ["/metricas.js"] = "application/javascript",
        ["/captura.js"] = "application/javascript",
        ["/arquivos-web.js"] = "application/javascript"
    };
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    private readonly string _origemPainel, _prefixoPainel, _sinalizacao;
    private readonly Uri _endereco;
    private readonly bool _tecnico;
    private readonly string? _convite;
    private readonly ControleAssistido _controle = new();
    private readonly DiagnosticoLocal _diagnostico = new();
    private readonly PreferenciasTecnico? _preferencias;
    private readonly DispatcherTimer _verificarEntrada = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _clienteAtivo;
    private ClipboardExplorer? _clipboard;
    private TransferenciaSessao? _arquivos;
    private Task _limpezaArquivos = Task.CompletedTask;
    private Task _inicializacao = Task.CompletedTask;
    private readonly TaskCompletionSource _navegadorSaiu = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _perfil;
    private bool _navegadorIniciado;
    private bool _selecaoCaptura;
    private bool _fechada;
    private bool _aguardandoFechamento;
    private string? _sessaoAutorizada;
    private string? _sessaoRevogada;
    private long _autorizadaAte;
    private long? _inicioTecnico;
    private (WindowState Estado, WindowStyle Estilo, ResizeMode Redimensionamento)? _modoJanela;

    public AtendimentoWindow(Uri origem, bool tecnico, string? convite = null)
    {
        InitializeComponent();
        Title = tecnico ? "SyncDesk — Técnico" : "SyncDesk — Cliente";
        if (tecnico) Width = 1000;
        _tecnico = tecnico;
        _convite = convite;
        if (tecnico) _preferencias = new();
        _diagnostico.Registrar("aplicativo_iniciado");
        bool testeLocal = origem.Scheme == Uri.UriSchemeHttp;
        _origemPainel = testeLocal ? origem.GetLeftPart(UriPartial.Authority) : OrigemPainel;
        _prefixoPainel = testeLocal ? "/__syncdesk_desktop" : "";
        _sinalizacao = (testeLocal ? "ws://" : "wss://") + origem.Authority;
        _endereco = new Uri(_origemPainel + _prefixoPainel + (tecnico ? "/index.html?modo=tecnico" : "/index.html"));
        _verificarEntrada.Tick += (_, _) =>
        {
            if (_sessaoAutorizada is { } sessao && Environment.TickCount64 >= _autorizadaAte)
            {
                LimparAtendimento();
                Responder(new { tipo = "vigenciaEncerrada", sessao });
                return;
            }
            AtualizarCaptura();
            if (_controle.Ativo && !_controle.Valido()) RevogarControle();
        };
        Loaded += Carregar;
        Closing += Fechar;
    }

    private async void Fechar(object? sender, CancelEventArgs e)
    {
        if (_aguardandoFechamento) { e.Cancel = true; return; }
        if (_fechada) return;
        _fechada = true;
        _diagnostico.Registrar("aplicativo_fechado", _sessaoAutorizada);
        LimparAtendimento();
        Conteudo.Dispose();
        e.Cancel = true;
        _aguardandoFechamento = true;
        await Dispatcher.Yield();
        try { await Task.WhenAll(_limpezaArquivos, _diagnostico.DisposeAsync().AsTask(),
            _preferencias?.Gravacao ?? Task.CompletedTask); }
        catch (Exception falha) when (falha is IOException or OperationCanceledException) { }
        await LimparPerfil();
        _aguardandoFechamento = false;
        Close();
    }

    private void Carregar(object sender, RoutedEventArgs e)
    {
        Loaded -= Carregar;
        _inicializacao = PrepararPainel();
    }

    private async Task LimparPerfil()
    {
        try
        {
            await _inicializacao.WaitAsync(TimeSpan.FromSeconds(3));
            if (_navegadorIniciado) await _navegadorSaiu.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException) { }

        if (_perfil is not { } perfil) return;
        await Task.Run(async () =>
        {
            for (int tentativa = 0; tentativa < 24; tentativa++)
            {
                try { Directory.Delete(perfil, recursive: true); return; }
                catch (DirectoryNotFoundException) { return; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(200);
                }
            }
        });
    }

    private async Task PrepararPainel()
    {
        try
        {
            if (_preferencias is not null) await _preferencias.Carregar();
            if (_fechada) return;
            string runtime = Path.Combine(AppContext.BaseDirectory, "WebView2Runtime");
            string? runtimeFixo = File.Exists(Path.Combine(runtime, "msedgewebview2.exe")) ? runtime : null;
            CoreWebView2Environment.GetAvailableBrowserVersionString(runtimeFixo);
            _perfil = Directory.CreateTempSubdirectory("SyncDesk-WebView2-").FullName;
            var ambiente = await CoreWebView2Environment.CreateAsync(runtimeFixo, _perfil);
            if (_fechada) return;
            ambiente.BrowserProcessExited += (_, _) => _navegadorSaiu.TrySetResult();
            var opcoes = ambiente.CreateCoreWebView2ControllerOptions();
            opcoes.IsInPrivateModeEnabled = true;
            await Conteudo.EnsureCoreWebView2Async(ambiente, opcoes);
            _navegadorIniciado = true;
            if (_fechada) return;
            var navegador = Conteudo.CoreWebView2;
            navegador.Settings.AreDevToolsEnabled = false;
            navegador.Settings.AreHostObjectsAllowed = false;
            navegador.Settings.IsWebMessageEnabled = true;
            navegador.Settings.IsPasswordAutosaveEnabled = false;
            navegador.Settings.IsGeneralAutofillEnabled = false;
            navegador.AddWebResourceRequestedFilter(_origemPainel + _prefixoPainel + "/*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            navegador.WebResourceRequested += EntregarRecurso;
            navegador.WebMessageReceived += ReceberMensagem;
            navegador.ScreenCaptureStarting += (_, args) =>
            {
                if (_tecnico || _clienteAtivo) args.Cancel = true;
                else _selecaoCaptura = true;
            };
            string servidor = JsonSerializer.Serialize(_sinalizacao);
            string versao = JsonSerializer.Serialize(typeof(AtendimentoWindow).Assembly.GetName().Version!.ToString(3));
            string preferencias = JsonSerializer.Serialize(_preferencias?.Atual, JsonWeb);
            await navegador.AddScriptToExecuteOnDocumentCreatedAsync(
                $"window.syncDeskServidor = {servidor}; window.syncDeskVersao = {versao}; window.syncDeskPreferencias = {preferencias}; window.syncDeskCapturaNativa = true; window.syncDeskConvite = {JsonSerializer.Serialize(_convite)};");
            if (_fechada) return;
            navegador.NewWindowRequested += (_, args) => args.Handled = true;
            if (_tecnico)
                navegador.ContainsFullScreenElementChanged += (_, _) => AjustarTelaCheia(navegador.ContainsFullScreenElement);
            navegador.NavigationStarting += (_, args) =>
            {
                args.Cancel = !string.Equals(args.Uri, _endereco.AbsoluteUri, StringComparison.Ordinal);
                if (!args.Cancel) LimparAtendimento();
            };
            navegador.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess)
                {
                    PainelDeEstado.Visibility = Visibility.Collapsed;
                    Conteudo.Visibility = Visibility.Visible;
                }
                else if (args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    MostrarErro("Não foi possível abrir o servidor SyncDesk. Confira a rede e o certificado HTTPS.", true);
            };
            navegador.ProcessFailed += (_, _) =>
            {
                _diagnostico.Registrar("painel_falhou", _sessaoAutorizada);
                LimparAtendimento();
                MostrarErro("O painel do SyncDesk foi interrompido. Feche e reabra o aplicativo.", false);
            };
            navegador.Navigate(_endereco.AbsoluteUri);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            if (!_fechada) MostrarErro(
                "O Microsoft Edge WebView2 Runtime está indisponível. Use um Windows com o WebView2 ou solicite o pacote portátil com a pasta WebView2Runtime.", false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!_fechada) MostrarErro(
                "O SyncDesk não conseguiu criar o perfil temporário. Confira se a pasta temporária do seu usuário está acessível.", false);
        }
        catch (NotImplementedException)
        {
            if (!_fechada) MostrarErro("A versão do WebView2 está desatualizada. Atualize o runtime ou solicite um pacote portátil atualizado.", false);
        }
        catch (Exception)
        {
            if (!_fechada) MostrarErro("Não foi possível iniciar o painel do SyncDesk.", false);
        }
    }

    private void EntregarRecurso(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        var ambiente = Conteudo.CoreWebView2.Environment;
        var uri = new Uri(args.Request.Uri);
        if (args.Request.Method != "GET" || uri.GetLeftPart(UriPartial.Authority) != _origemPainel ||
            !uri.AbsolutePath.StartsWith(_prefixoPainel + "/", StringComparison.Ordinal) ||
            !Recursos.TryGetValue(uri.AbsolutePath[_prefixoPainel.Length..], out var tipo))
        {
            args.Response = ambiente.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }

        var recurso = typeof(AtendimentoWindow).Assembly.GetManifestResourceStream("Painel." + uri.AbsolutePath[(_prefixoPainel.Length + 1)..])!;
        string csp = $"default-src 'self'; connect-src {_sinalizacao}; media-src blob: 'self'; " +
            "frame-ancestors 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";
        args.Response = ambiente.CreateWebResourceResponse(recurso, 200, "OK",
            $"Content-Type: {tipo}; charset=utf-8\r\nContent-Security-Policy: {csp}\r\n" +
            "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff");
    }

    private void ReceberMensagem(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_fechada || args.Source != _endereco.AbsoluteUri) return;
        string json = args.WebMessageAsJson;
        if (json.Length > 41_000) return;
        using var documento = JsonDocument.Parse(json);
        var dados = documento.RootElement;
        if (dados.ValueKind != JsonValueKind.Object) return;
        string tipo = ControleAssistido.Texto(dados, "tipo");
        string sessao = ControleAssistido.Texto(dados, "sessao");
        if (tipo == "diagnostico" && json.Length <= 512)
        {
            _diagnostico.Registrar(ControleAssistido.Texto(dados, "evento"), sessao);
            return;
        }
        if (tipo == "preferencias" && _preferencias is not null && json.Length <= 512)
        {
            _preferencias.Configurar(ControleAssistido.Texto(dados, "tema"),
                dados.TryGetProperty("historico", out var guardar) && guardar.ValueKind == JsonValueKind.True);
            EnviarPreferencias();
            return;
        }
        if (tipo == "vigencia")
        {
            RenovarVigencia(dados, sessao);
            return;
        }
        if (tipo == "fim" && (_sessaoAutorizada is null || _sessaoAutorizada == sessao))
        {
            LimparAtendimento();
            return;
        }
        if (sessao != _sessaoAutorizada || Environment.TickCount64 >= _autorizadaAte) return;
        switch (tipo)
        {
            case "sessaoAtiva" when _tecnico:
                _inicioTecnico ??= Stopwatch.GetTimestamp();
                break;
            case "amostrarMemoria" when _tecnico:
                using (var processo = Process.GetCurrentProcess())
                    Responder(new { tipo = "memoria", mib = processo.PrivateMemorySize64 / 1048576.0 });
                break;
            case "atendimentoNativo" when !_tecnico && !_clienteAtivo:
                IniciarCaptura(dados.TryGetProperty("controle", out var controleConcedido) &&
                    controleConcedido.ValueKind == JsonValueKind.True);
                break;
            case "capturar" when !_tecnico:
                ControleAssistido.Inteiro(dados, "pedido", out int pedido);
                _ = CapturarQuadro(dados.TryGetProperty("binario", out var binario) &&
                    binario.ValueKind == JsonValueKind.True, pedido,
                    dados.TryGetProperty("repetir", out var repetir) && repetir.ValueKind == JsonValueKind.True);
                break;
            case "selecionarMonitor" when !_tecnico:
                SelecionarMonitor(ControleAssistido.Texto(dados, "id"));
                break;
            case "autorizarMonitores" when !_tecnico:
                AutorizarMonitores();
                break;
            case "atendimento" when !_tecnico && !_clienteAtivo:
                bool monitor = ControleAssistido.Texto(dados, "superficie") == "monitor";
                bool dimensoes = ControleAssistido.Inteiro(dados, "largura", out int largura) &
                    ControleAssistido.Inteiro(dados, "altura", out int altura);
                bool controle = _selecaoCaptura && monitor && dimensoes && _controle.AutorizarMonitor(largura, altura);
                _selecaoCaptura = false;
                MostrarIndicador(controle);
                Responder(new { tipo = "permissao", controle, computador = Environment.MachineName });
                break;
            case "entrada" when !_tecnico && json.Length <= 1024 && dados.TryGetProperty("evento", out var evento):
                if (_capturaAtiva && (!ControleAssistido.Inteiro(dados, "geracao", out int geracao) || geracao != _geracaoCaptura))
                    break;
                if (!_controle.Aplicar(evento)) RevogarControle();
                break;
            case "arquivosAtivos":
                IniciarArquivos();
                break;
            case "arquivosFim":
                PararArquivos();
                break;
            case "arquivo" when _arquivos is not null && dados.TryGetProperty("mensagem", out var pacote):
                try
                {
                    var mensagem = pacote.Deserialize<Mensagem>(JsonWeb);
                    if (mensagem is not null) _arquivos.Receber(mensagem);
                }
                catch (JsonException) { }
                break;
            case "cancelarArquivo" when _arquivos is not null && dados.TryGetProperty("envio", out var envio):
                _arquivos.Cancelar(envio.ValueKind == JsonValueKind.True);
                break;
        }
    }

    private void EnviarPreferencias()
    {
        if (!_fechada && _preferencias is not null && Conteudo.CoreWebView2 is not null)
            Responder(new { tipo = "preferencias", dados = _preferencias.Atual });
    }

    private void AjustarTelaCheia(bool ativa)
    {
        if (ativa && _modoJanela is null)
        {
            _modoJanela = (WindowState, WindowStyle, ResizeMode);
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else if (!ativa && _modoJanela is { } modo)
        {
            _modoJanela = null;
            WindowStyle = modo.Estilo;
            ResizeMode = modo.Redimensionamento;
            WindowState = modo.Estado;
        }
    }

    private void RenovarVigencia(JsonElement dados, string sessao)
    {
        if (!Guid.TryParseExact(sessao, "N", out _) || sessao == _sessaoRevogada ||
            (_sessaoAutorizada is not null && _sessaoAutorizada != sessao) ||
            !ControleAssistido.Inteiro(dados, "restante", out int restante) || restante is <= 0 or > 4000 ||
            !dados.TryGetProperty("horario", out var horario) || horario.ValueKind != JsonValueKind.Number ||
            !horario.TryGetInt64(out long emitido)) return;
        long idade = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - emitido;
        if (idade < 0 || idade > 500 || idade >= restante) return;
        long agora = Environment.TickCount64;
        if (_sessaoAutorizada is not null && agora >= _autorizadaAte) return;
        _sessaoAutorizada = sessao;
        _autorizadaAte = agora + restante - idade;
        _verificarEntrada.Start();
    }

    private void MostrarIndicador(bool controle)
    {
        _clienteAtivo = true;
        Title = controle ? "SyncDesk — Sessão ativa · controle autorizado" : "SyncDesk — Sessão ativa";
    }

    private void Responder(object dados) => Conteudo.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(dados, JsonWeb));

    private void IniciarArquivos()
    {
        if (_arquivos is not null || PresentationSource.FromVisual(this) is not HwndSource janela) return;
        TransferenciaSessao? atual = null;
        atual = _arquivos = new TransferenciaSessao(_tecnico,
            mensagem => Dispatcher.InvokeAsync(() =>
            {
                if (_fechada || _arquivos != atual) return;
                try { Responder(new { tipo = "arquivo", mensagem }); }
                catch (Exception e) when (e is COMException or InvalidOperationException) { PararArquivos(); }
            }, DispatcherPriority.Background).Task,
            status =>
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    if (!_fechada && _arquivos == atual) AtualizarProgresso(status);
                }, DispatcherPriority.Background);
            });
        try
        {
            _clipboard = new ClipboardExplorer(janela, atual, mensagem =>
                AtualizarProgresso(new Progresso(false, false, mensagem, 0, 0, 0)));
        }
        catch (InvalidOperationException e)
        {
            PararArquivos();
            AtualizarProgresso(new Progresso(false, false, e.Message, 0, 0, 0));
        }
    }

    private void AtualizarProgresso(Progresso status)
    {
        Responder(new { tipo = "progressoArquivo", status });
    }

    private void PararArquivos()
    {
        _clipboard?.Dispose();
        _clipboard = null;
        var atual = _arquivos;
        _arquivos = null;
        if (atual is not null)
            _limpezaArquivos = Task.WhenAll(_limpezaArquivos, atual.DisposeAsync().AsTask());
    }

    private void RevogarControle()
    {
        _controle.Revogar();
        _verificarEntrada.Stop();
        if (_clienteAtivo) MostrarIndicador(false);
        Responder(new { tipo = "permissao", controle = false, computador = Environment.MachineName });
    }

    private void LimparAtendimento()
    {
        if (_inicioTecnico is { } inicio)
        {
            _inicioTecnico = null;
            _preferencias?.Registrar((int)Stopwatch.GetElapsedTime(inicio).TotalSeconds);
            EnviarPreferencias();
        }
        _sessaoRevogada = _sessaoAutorizada ?? _sessaoRevogada;
        _sessaoAutorizada = null;
        _autorizadaAte = 0;
        PararArquivos();
        _verificarEntrada.Stop();
        _selecaoCaptura = false;
        _controle.Revogar();
        EncerrarCaptura();
        _clienteAtivo = false;
        Title = _tecnico ? "SyncDesk — Técnico" : "SyncDesk — Cliente";
    }

    private void MostrarErro(string mensagem, bool permitirRecarga)
    {
        Conteudo.Visibility = Visibility.Hidden;
        PainelDeEstado.Visibility = Visibility.Visible;
        Estado.Text = mensagem;
        TentarNovamente.Visibility = permitirRecarga ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Recarregar(object sender, RoutedEventArgs e)
    {
        TentarNovamente.Visibility = Visibility.Collapsed;
        Estado.Text = "Abrindo o atendimento…";
        Conteudo.CoreWebView2.Navigate(_endereco.AbsoluteUri);
    }
}
