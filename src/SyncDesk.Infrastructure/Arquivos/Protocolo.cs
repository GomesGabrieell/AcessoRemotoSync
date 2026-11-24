namespace SyncDesk.Arquivos;

internal sealed record Mensagem(
    string Tipo, string Id = "", long Valor = 0, string Texto = "", byte[]? Dados = null);

internal sealed record Progresso(
    bool Envio, bool Ativo, string Texto, long Bytes, long Total, double Velocidade, bool Falha = false);
