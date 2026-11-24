using SyncDesk.Domain;

Executar("aceite explícito e encerramento definitivo", (atendimento, _) =>
{
    Exigir(!atendimento.Aceitar() && !atendimento.Renovar(false, true));
    Exigir(atendimento.Solicitar() && atendimento.Estado == EstadoAtendimento.Solicitado);
    Exigir(!atendimento.Solicitar() && !atendimento.Renovar(true, true));
    Exigir(atendimento.Aceitar() && atendimento.Estado == EstadoAtendimento.Ativo);
    atendimento.Encerrar();
    Exigir(!atendimento.Renovar(true, true) && !atendimento.Aceitar() && !atendimento.Solicitar());
});
Executar("validade do código limita o prazo da solicitação", (atendimento, relogio) =>
{
    relogio.Avancar(8);
    Exigir(atendimento.Solicitar());
    relogio.Avancar(2);
    Exigir(atendimento.Expiracao() == MotivoExpiracao.Codigo && !atendimento.Aceitar());
});
Executar("autorização precisa dos dois pares", (atendimento, relogio) =>
{
    Exigir(atendimento.Solicitar() && atendimento.Aceitar());
    relogio.Avancar(3);
    Exigir(!atendimento.Renovar(true, true));
    relogio.Avancar(1);
    Exigir(atendimento.Expiracao() == MotivoExpiracao.Conexao && !atendimento.Renovar(false, true));
});
Executar("pulsos mantêm a conexão mas não contam como atividade", (atendimento, relogio) =>
{
    Exigir(atendimento.Solicitar() && atendimento.Aceitar());
    for (int i = 1; i < 7; i++)
    {
        relogio.Avancar(1);
        Exigir(atendimento.Renovar(true, false) && atendimento.Renovar(false, i == 2));
    }
    relogio.Avancar(1);
    Exigir(atendimento.Expiracao() == MotivoExpiracao.Inatividade);
    Exigir(!atendimento.Renovar(false, true));
});

static void Executar(string nome, Action<Atendimento, RelogioTeste> teste)
{
    var relogio = new RelogioTeste();
    var atendimento = new Atendimento(relogio, new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5),
        TimeSpan.FromHours(1), TimeSpan.FromSeconds(5)));
    teste(atendimento, relogio);
    Console.WriteLine("OK " + nome);
}

static void Exigir(bool ok)
{
    if (!ok) throw new InvalidOperationException("Transição ou prazo de atendimento inesperado.");
}

sealed class RelogioTeste : TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
    public void Avancar(int segundos) => _ticks += TimeSpan.FromSeconds(segundos).Ticks;
}
