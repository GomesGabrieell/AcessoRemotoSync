using System.Runtime.InteropServices;

namespace SyncDesk.Controle;

// Os callbacks e o controlador rodam na thread da janela. Nenhum evento é gravado ou transmitido.
internal sealed partial class MonitorEntradaLocal : IDisposable
{
    private const int HookTeclado = 13, HookMouse = 14;
    private const int TeclaInjetada = 0x10, MouseInjetado = 0x01, TeclaSolta = 0x80;
    private readonly Hook _teclado;
    private readonly Hook _mouse;
    private readonly Action _aoPausar;
    private nint _hookTeclado, _hookMouse;

    public PrioridadeEntradaLocal Prioridade { get; } = new();

    public MonitorEntradaLocal(Action aoPausar)
    {
        _aoPausar = aoPausar;
        _teclado = ObservarTeclado;
        _mouse = ObservarMouse;
    }

    public bool Iniciar()
    {
        nint modulo = GetModuleHandle(0);
        _hookTeclado = SetWindowsHookEx(HookTeclado, _teclado, modulo, 0);
        _hookMouse = SetWindowsHookEx(HookMouse, _mouse, modulo, 0);
        if (_hookTeclado != 0 && _hookMouse != 0) return true;
        Dispose();
        return false;
    }

    private unsafe nint ObservarTeclado(int codigo, nuint mensagem, nint dados)
    {
        if (codigo == 0)
        {
            var evento = (EventoTeclado*)dados;
            if ((evento->Flags & TeclaInjetada) == 0 || evento->Extra != EntradaWindows.MarcaRemota)
            {
                long agora = Environment.TickCount64;
                bool pausada = Prioridade.Pausada(agora);
                Prioridade.RegistrarTecla(agora, evento->Vk, (evento->Flags & TeclaSolta) == 0);
                if (!pausada) _aoPausar();
            }
        }
        return CallNextHookEx(0, codigo, mensagem, dados);
    }

    private unsafe nint ObservarMouse(int codigo, nuint mensagem, nint dados)
    {
        if (codigo == 0)
        {
            var evento = (EventoMouse*)dados;
            if ((evento->Flags & MouseInjetado) == 0 || evento->Extra != EntradaWindows.MarcaRemota)
            {
                long agora = Environment.TickCount64;
                bool pausada = Prioridade.Pausada(agora);
                switch (mensagem)
                {
                    case 0x0201: Prioridade.RegistrarBotao(agora, 0, true); break; // Esquerdo
                    case 0x0202: Prioridade.RegistrarBotao(agora, 0, false); break;
                    case 0x0204: Prioridade.RegistrarBotao(agora, 2, true); break; // Direito
                    case 0x0205: Prioridade.RegistrarBotao(agora, 2, false); break;
                    case 0x0207: Prioridade.RegistrarBotao(agora, 1, true); break; // Meio
                    case 0x0208: Prioridade.RegistrarBotao(agora, 1, false); break;
                    case 0x020B: Prioridade.RegistrarBotao(agora, 2 + (evento->MouseData >> 16), true); break;
                    case 0x020C: Prioridade.RegistrarBotao(agora, 2 + (evento->MouseData >> 16), false); break;
                    default: Prioridade.Registrar(agora); break;
                }
                if (!pausada) _aoPausar();
            }
        }
        return CallNextHookEx(0, codigo, mensagem, dados);
    }

    public void Dispose()
    {
        if (_hookTeclado != 0) UnhookWindowsHookEx(_hookTeclado);
        if (_hookMouse != 0) UnhookWindowsHookEx(_hookMouse);
        _hookTeclado = _hookMouse = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventoTeclado { public int Vk, Scan, Flags, Tempo; public nuint Extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventoMouse { public int X, Y, MouseData, Flags, Tempo; public nuint Extra; }

    private delegate nint Hook(int codigo, nuint mensagem, nint dados);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    private static partial nint GetModuleHandle(nint nome);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW")]
    private static extern nint SetWindowsHookEx(int tipo, Hook callback, nint modulo, uint thread);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hook, int codigo, nuint mensagem, nint dados);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hook);
}
