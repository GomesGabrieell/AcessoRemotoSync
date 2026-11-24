using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SyncDesk.Arquivos;

internal sealed class ClipboardExplorer : IDisposable
{
    private readonly HwndSource _janela;
    private readonly TransferenciaSessao _sessao;
    private readonly Action<string> _aviso;
    private readonly HookTeclado _callback;
    private nint _hook;
    private bool _descartado, _suprimirV, _buscandoPasta, _aguardarCopia;
    private uint _sequencia;
    private long _copiou;
    private uint _processoCopia;

    public ClipboardExplorer(HwndSource janela, TransferenciaSessao sessao, Action<string> aviso)
    {
        _janela = janela;
        _sessao = sessao;
        _aviso = aviso;
        _callback = Teclado;
        _sequencia = GetClipboardSequenceNumber();
        janela.AddHook(Janela);
        if (!AddClipboardFormatListener(janela.Handle))
        {
            Dispose();
            throw new InvalidOperationException("O Windows não permitiu monitorar o copiar e colar.");
        }
        _hook = SetWindowsHookExW(13, _callback, GetModuleHandleW(null), 0);
        if (_hook == 0)
        {
            Dispose();
            throw new InvalidOperationException("O Windows não permitiu integrar os atalhos do Explorador.");
        }
    }

    private nint Teclado(int codigo, nint evento, nint parametro)
    {
        if (codigo < 0 || _descartado) return CallNextHookEx(0, codigo, evento, parametro);
        int tecla = Marshal.ReadInt32(parametro);
        if (tecla == 0x56 && _suprimirV)
        {
            if (evento == 0x0101 || evento == 0x0105) _suprimirV = false;
            return 1;
        }
        if ((evento != 0x0100 && evento != 0x0104) || tecla is not (0x43 or 0x56) ||
            !Pressionada(0x11) || Pressionada(0x10) || Pressionada(0x12))
            return CallNextHookEx(0, codigo, evento, parametro);

        nint origem = GetForegroundWindow();
        if (tecla == 0x43 && JanelaDoExplorador(origem))
        {
            _aguardarCopia = true;
            _copiou = Environment.TickCount64;
            ObterProcessoJanela(origem, out _processoCopia);
        }
        else if (tecla == 0x56 && !CopiaPendente() && _sessao.OfertaRemota is { Length: > 0 } oferta &&
            FocoNoExplorador(origem))
        {
            _suprimirV = true;
            _ = _janela.Dispatcher.InvokeAsync(() => ColarNoExplorador(origem, oferta), DispatcherPriority.Background);
            return 1;
        }
        return CallNextHookEx(0, codigo, evento, parametro);
    }

    private bool CopiaPendente() => _aguardarCopia && Environment.TickCount64 - _copiou <= 2000;

    private nint Janela(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x031D) _ = _janela.Dispatcher.InvokeAsync(AtualizarClipboard, DispatcherPriority.Background);
        return 0;
    }

    private async Task AtualizarClipboard()
    {
        if (_descartado) return;
        uint sequencia = GetClipboardSequenceNumber();
        if (_sequencia == sequencia) return;
        _sequencia = sequencia;
        nint dono = GetClipboardOwner();
        ObterProcessoJanela(dono, out uint processoDono);
        bool copiarArquivos = CopiaPendente() && dono != 0 && processoDono != 0 && processoDono == _processoCopia;
        _aguardarCopia = false;
        string[] arquivos = [];
        if (copiarArquivos)
        {
            for (int i = 0; i < 4; i++)
            {
                if (_descartado || sequencia != GetClipboardSequenceNumber()) return;
                try
                {
                    var objeto = Clipboard.GetDataObject();
                    if (objeto?.GetDataPresent(DataFormats.FileDrop, autoConvert: false) == true)
                        arquivos = objeto.GetData(DataFormats.FileDrop, autoConvert: false) as string[] ?? [];
                    break;
                }
                catch (ExternalException)
                {
                    if (i < 3) await Task.Delay(40);
                }
            }
        }
        if (!_descartado && sequencia == GetClipboardSequenceNumber()) _sessao.Copiar(arquivos);
    }

    private async Task ColarNoExplorador(nint origem, string oferta)
    {
        if (_descartado || _buscandoPasta) return;
        _buscandoPasta = true;
        Task<string?> resolucao = ResolverPasta(origem);
        try
        {
            string? pasta = await resolucao.WaitAsync(TimeSpan.FromSeconds(4));
            if (_descartado) return;
            if (pasta is null || origem != GetForegroundWindow() || !FocoNoExplorador(origem))
            {
                _aviso("Abra a pasta de destino no Explorador. Se houver várias abas na janela, use uma janela com uma única aba.");
                return;
            }
            _sessao.Colar(pasta, oferta);
        }
        catch (TimeoutException)
        {
            if (!_descartado) _aviso("O Explorador não informou a pasta de destino a tempo.");
            await resolucao;
        }
        finally { _buscandoPasta = false; }
    }

    private static Task<string?> ResolverPasta(nint hwnd)
    {
        var resultado = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tarefa = new Thread(() =>
        {
            object? shell = null, janelas = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!);
                janelas = ((dynamic)shell!).Windows();
                int total = ((dynamic)janelas!).Count;
                string? pasta = null;
                int encontrados = 0;
                for (int i = 0; i < total; i++)
                {
                    object? item = null;
                    try
                    {
                        item = ((dynamic)janelas!).Item(i);
                        if (item is null) continue;
                        dynamic navegador = item;
                        if (Convert.ToInt64((object)navegador.HWND) != hwnd.ToInt64()) continue;
                        encontrados++;
                        if (encontrados > 1) { pasta = null; break; }
                        string? url = navegador.LocationURL as string;
                        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile && !uri.IsUnc)
                            pasta = uri.LocalPath;
                    }
                    catch (COMException) { }
                    finally { if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item); }
                }
                resultado.TrySetResult(pasta);
            }
            catch (Exception) { resultado.TrySetResult(null); }
            finally
            {
                if (janelas is not null && Marshal.IsComObject(janelas)) Marshal.ReleaseComObject(janelas);
                if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            }
        }) { IsBackground = true, Name = "SyncDesk — pasta do Explorador" };
        tarefa.SetApartmentState(ApartmentState.STA);
        tarefa.Start();
        return resultado.Task;
    }

    private static bool FocoNoExplorador(nint hwnd)
    {
        if (!JanelaDoExplorador(hwnd)) return false;
        uint thread = GetWindowThreadProcessId(hwnd, 0);
        var info = new Foco { Tamanho = Marshal.SizeOf<Foco>() };
        if (!GetGUIThreadInfo(thread, ref info)) return false;
        for (nint foco = info.FocoAtual; foco != 0 && foco != hwnd; foco = GetParent(foco))
        {
            string classe = Classe(foco);
            if (classe.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
                classe.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)) return false;
            if (classe is "DirectUIHWND" or "SHELLDLL_DefView" or "SysListView32" or "UIItemsView") return true;
        }
        return false;
    }

    private static bool JanelaDoExplorador(nint hwnd) => Classe(hwnd) is "CabinetWClass" or "ExploreWClass";

    private static string Classe(nint hwnd)
    {
        var classe = new StringBuilder(80);
        return GetClassNameW(hwnd, classe, classe.Capacity) > 0 ? classe.ToString() : "";
    }

    private static bool Pressionada(int tecla) => (GetAsyncKeyState(tecla) & 0x8000) != 0;

    public void Dispose()
    {
        if (_descartado) return;
        _descartado = true;
        if (_hook != 0) UnhookWindowsHookEx(_hook);
        RemoveClipboardFormatListener(_janela.Handle);
        _janela.RemoveHook(Janela);
    }

    private delegate nint HookTeclado(int codigo, nint evento, nint dados);
    [StructLayout(LayoutKind.Sequential)]
    private struct Foco
    {
        public int Tamanho, Flags;
        public nint Ativo, FocoAtual, Captura, Menu, Movimento, Caret;
        public int X, Y, R, B;
    }

    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(nint hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern nint GetClipboardOwner();
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetParent(nint janela);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint janela, nint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint ObterProcessoJanela(nint janela, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref Foco info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassNameW(nint janela, StringBuilder nome, int maximo);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int tecla);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint GetModuleHandleW(string? nome);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SetWindowsHookExW(int tipo, HookTeclado callback, nint modulo, uint thread);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int codigo, nint evento, nint dados);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
}
