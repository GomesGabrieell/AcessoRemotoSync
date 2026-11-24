using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SyncDesk.Controle;

namespace SyncDesk.Views;

public partial class AtendimentoWindow
{
    private readonly HashSet<string> _autorizados = new(StringComparer.Ordinal);
    private MonitorWindows[] _monitores = [];
    private MonitorWindows? _selecao;
    private bool _capturaAtiva, _capturaOcupada, _controleConcedido;
    private int _geracaoCaptura;
    private FluxoPixels? _pixels;
    private bool _binarioIndisponivel;
    private long _momentoCpu;
    private TimeSpan _tempoCpu;
    private double? _usoCpu;

    private void IniciarCaptura(bool controle)
    {
        if (!EntradaWindows.DesktopInterativo()) { Responder(new { tipo = "capturaEncerrada" }); return; }
        _monitores = MonitoresWindows.Listar();
        if (_monitores.Length == 0) { Responder(new { tipo = "capturaEncerrada" }); return; }
        _capturaAtiva = true;
        _controleConcedido = controle;
        _autorizados.UnionWith(_monitores.Select(t => t.Id));
        AlterarSelecao(_monitores[0]);
    }

    private void AtualizarCaptura()
    {
        if (!_capturaAtiva) return;
        if (!EntradaWindows.DesktopInterativo())
        {
            EncerrarCaptura();
            _controle.Revogar();
            Responder(new { tipo = "capturaEncerrada" });
            return;
        }
        var atual = MonitoresWindows.Listar();
        if (_monitores.SequenceEqual(atual)) return;
        _monitores = atual;
        _autorizados.IntersectWith(atual.Select(t => t.Id));
        var proximo = atual.FirstOrDefault(t => t.Id == _selecao?.Id && _autorizados.Contains(t.Id)) ??
            atual.FirstOrDefault(t => _autorizados.Contains(t.Id));
        if (proximo != _selecao) AlterarSelecao(proximo);
        else AnunciarMonitores();
    }

    private void AutorizarMonitores()
    {
        if (!_capturaAtiva) return;
        AtualizarCaptura();
        if (!_capturaAtiva) return;
        _autorizados.UnionWith(_monitores.Select(t => t.Id));
        if (_selecao is null) AlterarSelecao(_monitores.FirstOrDefault());
        else AnunciarMonitores();
    }

    private void SelecionarMonitor(string id)
    {
        if (!_capturaAtiva) return;
        AtualizarCaptura();
        if (!_capturaAtiva) return;
        if (_autorizados.Contains(id) && _monitores.FirstOrDefault(t => t.Id == id) is { } tela && tela != _selecao)
            AlterarSelecao(tela);
        else AnunciarMonitores();
    }

    private void AlterarSelecao(MonitorWindows? tela)
    {
        LiberarPixels();
        _selecao = tela;
        _geracaoCaptura++;
        bool controle = _controleConcedido && tela is not null && _controle.AutorizarMonitor(tela);
        if (!controle) _controle.Revogar();
        MostrarIndicador(controle);
        Responder(new { tipo = "permissao", controle, computador = Environment.MachineName });
        AnunciarMonitores();
    }

    private void AnunciarMonitores() => Responder(new
    {
        tipo = "monitores",
        telas = _monitores.Select((t, i) => new { id = t.Id, numero = i + 1, autorizado = _autorizados.Contains(t.Id) }),
        selecionado = _selecao?.Id,
        geracao = _geracaoCaptura
    });

    private async Task CapturarQuadro(bool binario, int pedido, bool repetir)
    {
        if (!_capturaAtiva || _capturaOcupada) return;
        var sessao = _sessaoAutorizada;
        int geracao = _geracaoCaptura;
        byte[]? dados = null;
        bool temPixels = false;
        double capturaMs = 0;
        FluxoPixels? fluxo = null;
        _capturaOcupada = true;
        try
        {
            if (_selecao is { } tela)
            {
                if (binario && !_binarioIndisponivel)
                {
                    fluxo = _pixels ??= new FluxoPixels(tela, Conteudo.CoreWebView2.Environment);
                    var buffer = fluxo.Buffer.Buffer;
                    (temPixels, capturaMs) = await Task.Run(() =>
                    {
                        long inicio = Stopwatch.GetTimestamp();
                        bool disponivel = fluxo.Fonte.Capturar(buffer, repetir);
                        return (disponivel, Stopwatch.GetElapsedTime(inicio).TotalMilliseconds);
                    });
                }
                else dados = await Task.Run(() => MonitoresWindows.Capturar(tela));
            }
        }
        catch (Exception e) when (e is ExternalException or ArgumentException or InvalidOperationException or NotImplementedException)
        {
            if (fluxo is null && binario) _binarioIndisponivel = true;
        }
        finally
        {
            _capturaOcupada = false;
            if (fluxo is not null && fluxo != _pixels) fluxo.Dispose();
        }

        if (_fechada || !_capturaAtiva || sessao != _sessaoAutorizada || Environment.TickCount64 >= _autorizadaAte) return;
        bool atual = geracao == _geracaoCaptura && EntradaWindows.DesktopInterativo();
        if (atual && temPixels && fluxo is not null && !fluxo.Compartilhado)
        {
            var (largura, altura) = fluxo.Fonte.Tamanho;
            try
            {
                Conteudo.CoreWebView2.PostSharedBufferToScript(fluxo.Buffer, CoreWebView2SharedBufferAccess.ReadOnly,
                    JsonSerializer.Serialize(new { geracao, largura, altura }, JsonWeb));
                fluxo.Compartilhado = true;
            }
            catch (Exception e) when (e is ExternalException or InvalidOperationException or NotImplementedException)
            {
                _binarioIndisponivel = true;
                LiberarPixels();
                temPixels = false;
            }
        }
        Responder(new { tipo = "quadro", pedido, geracao = _geracaoCaptura, pixels = atual && temPixels,
            fonte = fluxo?.Fonte.Fonte, capturaMs, jpeg = atual && dados is not null ? Convert.ToBase64String(dados) : null,
            compatibilidade = _binarioIndisponivel, cpu = AmostrarCpu() });
    }

    private double? AmostrarCpu()
    {
        long agora = Stopwatch.GetTimestamp();
        var intervalo = Stopwatch.GetElapsedTime(_momentoCpu, agora);
        if (_momentoCpu != 0 && intervalo.TotalSeconds < 2) return _usoCpu;
        using var processo = Process.GetCurrentProcess();
        var total = processo.TotalProcessorTime;
        if (_momentoCpu != 0)
            _usoCpu = 100 * (total - _tempoCpu).TotalMilliseconds / (intervalo.TotalMilliseconds * Environment.ProcessorCount);
        _momentoCpu = agora;
        _tempoCpu = total;
        return _usoCpu;
    }

    private void LiberarPixels()
    {
        if (!_capturaOcupada) _pixels?.Dispose();
        _pixels = null;
    }

    private void EncerrarCaptura()
    {
        LiberarPixels();
        _capturaAtiva = false;
        _controleConcedido = false;
        _selecao = null;
        _monitores = [];
        _autorizados.Clear();
        _momentoCpu = 0;
        _usoCpu = null;
        _geracaoCaptura++;
    }

    private sealed class FluxoPixels : IDisposable
    {
        public CapturaPixels Fonte { get; }
        public CoreWebView2SharedBuffer Buffer { get; }
        public bool Compartilhado { get; set; }

        public FluxoPixels(MonitorWindows monitor, CoreWebView2Environment ambiente)
        {
            Fonte = new(monitor);
            var (largura, altura) = Fonte.Tamanho;
            Buffer = ambiente.CreateSharedBuffer((ulong)largura * (ulong)altura * 4);
        }

        public void Dispose()
        {
            Fonte.Dispose();
            Buffer.Dispose();
        }
    }
}
