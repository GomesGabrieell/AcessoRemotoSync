using System.Text.Json;

namespace SyncDesk.Sinalizacao;

internal sealed record SinalValidado(object Dados, bool Descricao)
{
    public static SinalValidado? Ler(JsonElement dados, bool cliente)
    {
        bool temDescricao = dados.TryGetProperty("descricao", out var descricao);
        bool temCandidato = dados.TryGetProperty("candidato", out var candidato);
        if (temDescricao == temCandidato) return null;
        if (temDescricao)
        {
            if (descricao.ValueKind != JsonValueKind.Object) return null;
            string? tipo = Texto(descricao, "type", 8);
            string? sdp = Texto(descricao, "sdp", 60_000);
            return tipo == (cliente ? "offer" : "answer") && sdp?.StartsWith("v=0", StringComparison.Ordinal) == true
                ? new(new { descricao = new { type = tipo, sdp } }, true) : null;
        }
        if (candidato.ValueKind != JsonValueKind.Object) return null;
        string? valor = Texto(candidato, "candidate", 2048);
        string? medio = Texto(candidato, "sdpMid", 32);
        string? usuario = Texto(candidato, "usernameFragment", 256);
        int? indice = candidato.TryGetProperty("sdpMLineIndex", out var n) && n.ValueKind == JsonValueKind.Number &&
            n.TryGetInt32(out int i) && i is >= 0 and <= 16 ? i : null;
        return valor?.StartsWith("candidate:", StringComparison.Ordinal) == true && (medio is not null || indice is not null)
            ? new(new { candidato = new { candidate = valor, sdpMid = medio, sdpMLineIndex = indice, usernameFragment = usuario } }, false)
            : null;
    }

    private static string? Texto(JsonElement objeto, string campo, int limite) =>
        objeto.TryGetProperty(campo, out var valor) && valor.ValueKind == JsonValueKind.String &&
            valor.GetString() is { Length: > 0 } texto && texto.Length <= limite ? texto : null;
}
