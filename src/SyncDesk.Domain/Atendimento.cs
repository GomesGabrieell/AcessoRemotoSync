namespace SyncDesk.Domain;

public enum EstadoAtendimento { Aguardando, Solicitado, Ativo, Encerrado }
public enum MotivoExpiracao { Codigo, Duracao, Conexao, Inatividade }

public sealed record PrazosAtendimento(TimeSpan Codigo, TimeSpan Solicitacao, TimeSpan Duracao, TimeSpan Inatividade);

public sealed class Atendimento(TimeProvider relogio, PrazosAtendimento prazos)
{
    private long _cliente, _tecnico, _atividade;
    public EstadoAtendimento Estado { get; private set; } = EstadoAtendimento.Aguardando;
    public DateTimeOffset Vence { get; private set; } = relogio.GetUtcNow() + prazos.Codigo;

    public bool Solicitar()
    {
        if (Estado != EstadoAtendimento.Aguardando || Expiracao() is not null) return false;
        Estado = EstadoAtendimento.Solicitado;
        var vence = relogio.GetUtcNow() + prazos.Solicitacao;
        if (vence < Vence) Vence = vence;
        return true;
    }

    public bool Aceitar()
    {
        if (Estado != EstadoAtendimento.Solicitado || Expiracao() is not null) return false;
        Estado = EstadoAtendimento.Ativo;
        Vence = relogio.GetUtcNow() + prazos.Duracao;
        _cliente = _tecnico = _atividade = relogio.GetTimestamp();
        return true;
    }

    public bool Renovar(bool cliente, bool atividade)
    {
        if (Estado != EstadoAtendimento.Ativo || Expiracao() is not null) return false;
        long agora = relogio.GetTimestamp();
        if (cliente) _cliente = agora;
        else _tecnico = agora;
        if (atividade) _atividade = agora;
        return relogio.GetElapsedTime(cliente ? _tecnico : _cliente, agora) <= TimeSpan.FromSeconds(2);
    }

    public MotivoExpiracao? Expiracao()
    {
        if (Estado == EstadoAtendimento.Encerrado) return null;
        if (Vence <= relogio.GetUtcNow())
            return Estado == EstadoAtendimento.Ativo ? MotivoExpiracao.Duracao : MotivoExpiracao.Codigo;
        if (Estado != EstadoAtendimento.Ativo) return null;
        long agora = relogio.GetTimestamp();
        if (relogio.GetElapsedTime(Math.Min(_cliente, _tecnico), agora) >= TimeSpan.FromSeconds(4))
            return MotivoExpiracao.Conexao;
        return relogio.GetElapsedTime(_atividade, agora) >= prazos.Inatividade ? MotivoExpiracao.Inatividade : null;
    }

    public void Encerrar() => Estado = EstadoAtendimento.Encerrado;
}
