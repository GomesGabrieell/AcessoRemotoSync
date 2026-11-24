using System.Runtime.InteropServices;
using System.Text;

namespace SyncDesk.Controle;

internal static partial class EntradaWindows
{
    private const uint Mouse = 0, Teclado = 1, MoverAbsoluto = 0xC001;
    private const uint SoltarTecla = 2, TeclaEstendida = 1;
    private static readonly (uint Pressionar, uint Soltar)[] Botoes = [(2, 4), (32, 64), (8, 16)];

    public static (int Largura, int Altura)? TelaUnica() =>
        GetSystemMetrics(80) == 1 ? (GetSystemMetrics(0), GetSystemMetrics(1)) : null;

    public static bool DesktopInterativo()
    {
        nint desktop = OpenInputDesktop(0, false, 1);
        if (desktop == 0) return false;
        try
        {
            var nome = new StringBuilder(64);
            return GetUserObjectInformation(desktop, 2, nome, 128, out _) &&
                string.Equals(nome.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }

    public static bool Mover(double x, double y) =>
        Enviar(MontarMouse(MoverAbsoluto, x: (int)Math.Round(x * 65535), y: (int)Math.Round(y * 65535)));

    public static bool Mover(MonitorWindows monitor, double x, double y)
    {
        var r = monitor.Limites;
        int largura = GetSystemMetrics(78), altura = GetSystemMetrics(79);
        if (largura <= 0 || altura <= 0) return false;
        double px = r.Left - GetSystemMetrics(76) + x * (r.Width - 1);
        double py = r.Top - GetSystemMetrics(77) + y * (r.Height - 1);
        return Mover(Math.Clamp((px + .5) / largura, 0, 1), Math.Clamp((py + .5) / altura, 0, 1));
    }

    public static bool Botao(int botao, bool pressionar) =>
        Enviar(MontarMouse(pressionar ? Botoes[botao].Pressionar : Botoes[botao].Soltar));

    public static bool Rolar(int x, int y)
    {
        bool horizontal = x == 0 || Enviar(MontarMouse(0x1000, x));
        return (y == 0 || Enviar(MontarMouse(0x0800, -y))) && horizontal;
    }

    public static bool Tecla(ushort vk, bool estendida, bool pressionar) =>
        Enviar(new Input
        {
            Tipo = Teclado,
            Dados = new DadosInput
            {
                Teclado = new DadosTeclado
                {
                    Vk = vk,
                    Flags = (estendida ? TeclaEstendida : 0) | (pressionar ? 0 : SoltarTecla)
                }
            }
        });

    private static Input MontarMouse(uint flags, int dados = 0, int x = 0, int y = 0) => new()
    {
        Tipo = Mouse,
        Dados = new DadosInput { Mouse = new DadosMouse { X = x, Y = y, Dados = dados, Flags = flags } }
    };

    private static bool Enviar(Input pacote) => SendInput(1, ref pacote, Marshal.SizeOf<Input>()) == 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Tipo; public DadosInput Dados; }

    [StructLayout(LayoutKind.Explicit)]
    private struct DadosInput
    {
        [FieldOffset(0)] public DadosMouse Mouse;
        [FieldOffset(0)] public DadosTeclado Teclado;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DadosMouse { public int X, Y, Dados; public uint Flags, Tempo; public nuint Extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DadosTeclado { public ushort Vk, Scan; public uint Flags, Tempo; public nuint Extra; }

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint quantidade, ref Input entrada, int tamanho);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int indice);

    [LibraryImport("user32.dll")]
    private static partial nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool herdar, uint acesso);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(nint desktop);

    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(nint objeto, int indice, StringBuilder informacao, uint tamanho, out uint necessario);
}
