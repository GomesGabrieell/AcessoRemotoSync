using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace SyncDesk.Sinalizacao;

internal sealed class CodigosEmitidos : IDisposable
{
    private const int Espaco = 1_000_000;
    private const int Capacidade = 900_000;
    private readonly byte[] _bitmap = new byte[Espaco / 8];
    private readonly FileStream _arquivo;
    private int _total;
    private bool _falhou;

    public CodigosEmitidos(Configuracao config)
    {
        string caminho = Path.GetFullPath(config.ArquivoCodigos);
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        _arquivo = new FileStream(caminho, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (_arquivo.Length == 0)
            {
                _arquivo.SetLength(_bitmap.Length);
                _arquivo.Flush(flushToDisk: true);
            }
            if (_arquivo.Length != _bitmap.Length)
                throw new IOException("O registro de códigos tem tamanho inválido.");
            _arquivo.ReadExactly(_bitmap);
            _total = _bitmap.Sum(b => BitOperations.PopCount(b));
        }
        catch
        {
            _arquivo.Dispose();
            throw;
        }
    }

    // A sinalização serializa emissões junto ao registro de sessões.
    public string? Emitir()
    {
        if (_falhou || _total >= Capacidade) return null;
        while (true)
        {
            int codigo = RandomNumberGenerator.GetInt32(Espaco);
            int indice = codigo / 8;
            int bit = 1 << (codigo % 8);
            if ((_bitmap[indice] & bit) != 0) continue;
            byte valor = (byte)(_bitmap[indice] | bit);
            try
            {
                _arquivo.Position = indice;
                _arquivo.WriteByte(valor);
                _arquivo.Flush(flushToDisk: true);
            }
            catch (IOException)
            {
                _falhou = true;
                return null;
            }
            _bitmap[indice] = valor;
            _total++;
            return codigo.ToString("D6", CultureInfo.InvariantCulture);
        }
    }

    public void Dispose() => _arquivo.Dispose();
}
