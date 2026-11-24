using System.Text.Json;

namespace SyncDesk.Controle;

internal sealed class ControleAssistido(Action<Action> agendar)
{
    private readonly HashSet<(ushort Vk, bool Estendida)> _teclas = [];
    private readonly HashSet<int> _botoes = [];
    private (int Largura, int Altura)? _tela;
    private MonitorWindows? _monitor;
    private long _ultimoSinal;
    private long _janela;
    private int _eventos;
    private long _ultimoTesteDesktop;
    private MonitorEntradaLocal? _entradaLocal;

    public bool Ativo => _tela is not null || _monitor is not null;

    public bool AutorizarMonitor(MonitorWindows monitor)
    {
        if (!EntradaWindows.DesktopInterativo() || !MonitoresWindows.Listar().Contains(monitor) ||
            !ObservarEntradaLocal())
        {
            Revogar();
            return false;
        }
        SoltarTudo();
        _tela = null;
        _monitor = monitor;
        ReiniciarPulso();
        return true;
    }

    public bool AutorizarMonitor(int largura, int altura)
    {
        Revogar();
        if (largura <= 0 || altura <= 0 || EntradaWindows.TelaUnica() is not { } tela ||
            tela.Largura <= 0 || tela.Altura <= 0 || !EntradaWindows.DesktopInterativo())
            return false;

        double proporcao = (double)largura / altura;
        double desktop = (double)tela.Largura / tela.Altura;
        if (Math.Abs(proporcao / desktop - 1) > 0.025 || !ObservarEntradaLocal()) return false;
        _tela = tela;
        ReiniciarPulso();
        return true;
    }

    private bool ObservarEntradaLocal()
    {
        if (_entradaLocal is not null) return true;
        var monitor = new MonitorEntradaLocal(() => agendar(() =>
        {
            if (_entradaLocal?.Prioridade.Pausada(Environment.TickCount64) == true) SoltarTudo();
        }));
        if (!monitor.Iniciar()) return false;
        _entradaLocal = monitor;
        return true;
    }

    private bool ClienteUsando() => _entradaLocal?.Prioridade.Pausada(Environment.TickCount64) == true;

    private void ReiniciarPulso()
    {
        _ultimoTesteDesktop = _ultimoSinal = _janela = Environment.TickCount64;
        _eventos = 0;
    }

    public bool Valido() => Ativo && Environment.TickCount64 - _ultimoSinal < 5000 &&
        (_monitor is { } monitor ? MonitoresWindows.Listar().Contains(monitor) : EntradaWindows.TelaUnica() == _tela) &&
        EntradaWindows.DesktopInterativo();

    private bool Mover(double x, double y) => _monitor is { } monitor
        ? EntradaWindows.Mover(monitor, x, y) : EntradaWindows.Mover(x, y);

    public bool Aplicar(JsonElement dados)
    {
        if (!Ativo || dados.ValueKind != JsonValueKind.Object) return true;
        long agora = Environment.TickCount64;
        if (agora - _janela >= 1000)
        {
            _janela = agora;
            _eventos = 0;
        }
        if (++_eventos > 240 || agora - _ultimoSinal >= 5000) return false;
        string acao = Texto(dados, "acao");
        if (acao != "mover" || agora - _ultimoTesteDesktop >= 75)
        {
            if (!Valido()) return false;
            _ultimoTesteDesktop = agora;
        }
        switch (acao)
        {
            case "sinal":
                _ultimoSinal = agora;
                break;
            case "soltar":
                SoltarTudo();
                break;
            default:
                if (!ClienteUsando()) return AplicarEntrada(dados, acao);
                SoltarTudo();
                break;
        }
        return true;
    }

    private bool AplicarEntrada(JsonElement dados, string acao)
    {
        switch (acao)
        {
            case "mover" when Coordenadas(dados, out var x, out var y):
                return Mover(x, y);
            case "botao" when Coordenadas(dados, out var bx, out var by) &&
                Inteiro(dados, "botao", out int botao) && botao is >= 0 and <= 2 &&
                Booleano(dados, "pressionar", out bool pressionar):
                if (!pressionar && !_botoes.Contains(botao)) break;
                if (!Mover(bx, by)) return false;
                if (ClienteUsando()) break;
                if (!EntradaWindows.Botao(botao, pressionar)) return false;
                if (pressionar) _botoes.Add(botao);
                else _botoes.Remove(botao);
                break;
            case "rolar" when Coordenadas(dados, out var rx, out var ry) &&
                Inteiro(dados, "dx", out int dx) && Inteiro(dados, "dy", out int dy):
                return Mover(rx, ry) && (ClienteUsando() ||
                    EntradaWindows.Rolar(Math.Clamp(dx, -360, 360), Math.Clamp(dy, -360, 360)));
            case "tecla" when MapaTeclas.TentarMapear(Texto(dados, "codigo"), out var tecla) &&
                Booleano(dados, "pressionar", out bool baixar):
                if (baixar && _monitor is { } selecionado && !MonitoresWindows.FocoEm(selecionado)) break;
                if (!baixar && !_teclas.Contains(tecla)) break;
                if (baixar && Booleano(dados, "repeticao", out bool repeticao) && repeticao && !_teclas.Contains(tecla))
                    break;
                if (!EntradaWindows.Tecla(tecla.Vk, tecla.Estendida, baixar)) return false;
                if (baixar) _teclas.Add(tecla);
                else _teclas.Remove(tecla);
                break;
        }
        return true;
    }

    public void Revogar()
    {
        _tela = null;
        _monitor = null;
        SoltarTudo();
        _entradaLocal?.Dispose();
        _entradaLocal = null;
    }

    private void SoltarTudo()
    {
        foreach (var tecla in _teclas)
            if (_entradaLocal?.Prioridade.TeclaPressionada(tecla.Vk) != true)
                EntradaWindows.Tecla(tecla.Vk, tecla.Estendida, false);
        foreach (int botao in _botoes)
            if (_entradaLocal?.Prioridade.BotaoPressionado(botao) != true) EntradaWindows.Botao(botao, false);
        _teclas.Clear();
        _botoes.Clear();
    }

    internal static string Texto(JsonElement dados, string nome) =>
        dados.TryGetProperty(nome, out var valor) && valor.ValueKind == JsonValueKind.String ? valor.GetString() ?? "" : "";

    internal static bool Inteiro(JsonElement dados, string nome, out int resultado)
    {
        resultado = 0;
        return dados.TryGetProperty(nome, out var valor) && valor.ValueKind == JsonValueKind.Number &&
            valor.TryGetInt32(out resultado);
    }

    private static bool Booleano(JsonElement dados, string nome, out bool resultado)
    {
        resultado = false;
        if (!dados.TryGetProperty(nome, out var valor) || valor.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        resultado = valor.GetBoolean();
        return true;
    }

    private static bool Coordenadas(JsonElement dados, out double x, out double y)
    {
        bool temX = Fracao(dados, "x", out x);
        bool temY = Fracao(dados, "y", out y);
        return temX && temY;
    }

    private static bool Fracao(JsonElement dados, string nome, out double resultado)
    {
        resultado = 0;
        return dados.TryGetProperty(nome, out var valor) && valor.ValueKind == JsonValueKind.Number &&
            valor.TryGetDouble(out resultado) && resultado is >= 0 and <= 1;
    }
}
