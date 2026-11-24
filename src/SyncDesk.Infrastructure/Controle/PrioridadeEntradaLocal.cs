namespace SyncDesk.Controle;

internal sealed class PrioridadeEntradaLocal
{
    internal const int PausaMs = 500;
    private readonly HashSet<int> _teclas = [];
    private readonly HashSet<int> _botoes = [];
    private long _retomarEm;

    public bool Pausada(long agora) => agora < _retomarEm || _teclas.Count != 0 || _botoes.Count != 0;

    public void Registrar(long agora) => _retomarEm = agora + PausaMs;

    public void RegistrarTecla(long agora, int vk, bool pressionar)
    {
        if (pressionar) _teclas.Add(vk);
        else _teclas.Remove(vk);
        Registrar(agora);
    }

    public void RegistrarBotao(long agora, int botao, bool pressionar)
    {
        if (pressionar) _botoes.Add(botao);
        else _botoes.Remove(botao);
        Registrar(agora);
    }

    public bool TeclaPressionada(int vk) => _teclas.Contains(vk);
    public bool BotaoPressionado(int botao) => _botoes.Contains(botao);
}
