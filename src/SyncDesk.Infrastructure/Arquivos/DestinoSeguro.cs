using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SyncDesk.Arquivos;

internal sealed class DestinoSeguro : IDisposable
{
    private const uint Reparse = 0x400;
    private readonly List<SafeFileHandle> _travas = [];
    public string Pasta { get; }

    public DestinoSeguro(string pasta)
    {
        Pasta = CaminhoLocal(pasta);
        try
        {
            var cadeia = new Stack<string>();
            for (var item = new DirectoryInfo(Pasta); item is not null; item = item.Parent)
                cadeia.Push(item.FullName);
            foreach (string item in cadeia)
            {
                var handle = CreateFileW(item, 0x80, 3, 0, 3, 0x02200000, 0);
                _travas.Add(handle);
                if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out var info, 8) ||
                    (info.Attributes & (Reparse | 0x10)) != 0x10)
                    throw new IOException("A pasta de destino não é uma pasta local comum.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static string CaminhoLocal(string caminho)
    {
        if (!Path.IsPathFullyQualified(caminho) || caminho.Length < 3 || !char.IsAsciiLetter(caminho[0]) ||
            caminho[1] != ':' || caminho[2] != '\\' || caminho.AsSpan(2).Contains(':'))
            throw new IOException("Escolha uma pasta ou arquivo em uma unidade local.");
        string completo = Path.GetFullPath(caminho);
        if (new DriveInfo(completo[..3]).DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new IOException("Unidades de rede não são usadas para transferir arquivos.");
        return completo;
    }

    public static bool NomeValido(string nome)
    {
        if (nome.Length is < 1 or > 220 || nome is "." or ".." || nome.EndsWith('.') || nome.EndsWith(' ') ||
            nome.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;
        string baseNome = nome.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
        return baseNome is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$") &&
            !(baseNome.Length == 4 && (baseNome.StartsWith("COM", StringComparison.Ordinal) ||
                baseNome.StartsWith("LPT", StringComparison.Ordinal)) && "0123456789¹²³".Contains(baseNome[3]));
    }

    public (string Caminho, FileStream Stream) AbrirTemporario()
    {
        string caminho = Path.Combine(Pasta, $".syncdesk-{Guid.NewGuid():N}.part");
        return (caminho, new FileStream(caminho, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public string Publicar(string temporario, string nome)
    {
        if (!NomeValido(nome)) throw new IOException("Nome de arquivo inválido.");
        for (int i = 1; i <= 1000; i++)
        {
            string disponivel = i == 1 ? nome : $"{Path.GetFileNameWithoutExtension(nome)} ({i}){Path.GetExtension(nome)}";
            string final = Path.Combine(Pasta, disponivel);
            try
            {
                File.Move(temporario, final, overwrite: false);
                return disponivel;
            }
            catch (IOException) when (Path.Exists(final)) { }
        }
        throw new IOException("Não foi possível reservar um nome para o arquivo.");
    }

    public void Dispose()
    {
        foreach (var handle in _travas) handle.Dispose();
        _travas.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Atributos { public uint Attributes, Tag; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(
        string nome, uint acesso, uint compartilhamento, nint seguranca, uint criacao, uint flags, nint modelo);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int classe, out Atributos dados, uint tamanho);
}
