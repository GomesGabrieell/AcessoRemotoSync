using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SyncDesk.Controle;

internal sealed record MonitorWindows(nint Handle, Rectangle Limites, bool Principal)
{
    public string Id => Handle.ToString("x");
}

internal static class MonitoresWindows
{
    public static MonitorWindows[] Listar()
    {
        var telas = new List<MonitorWindows>();
        EnumDisplayMonitors(0, 0, (handle, _, _, _) =>
        {
            var dados = new Info { Tamanho = Marshal.SizeOf<Info>() };
            if (GetMonitorInfoW(handle, ref dados) && dados.Monitor.R > dados.Monitor.L && dados.Monitor.B > dados.Monitor.T)
                telas.Add(new(handle, Rectangle.FromLTRB(dados.Monitor.L, dados.Monitor.T, dados.Monitor.R, dados.Monitor.B),
                    (dados.Flags & 1) != 0));
            return true;
        }, 0);
        return [.. telas.OrderByDescending(t => t.Principal).ThenBy(t => t.Limites.Left).ThenBy(t => t.Limites.Top)];
    }

    public static bool FocoEm(MonitorWindows monitor) =>
        MonitorFromWindow(GetForegroundWindow(), 0) == monitor.Handle;

    public static byte[]? Capturar(MonitorWindows monitor)
    {
        if (!EntradaWindows.DesktopInterativo() || !Listar().Contains(monitor)) return null;
        var rect = monitor.Limites;
        double escala = Math.Min(1, 1920.0 / Math.Max(rect.Width, rect.Height));
        int largura = Math.Max(1, (int)Math.Round(rect.Width * escala));
        int altura = Math.Max(1, (int)Math.Round(rect.Height * escala));
        using var imagem = new Bitmap(largura, altura, PixelFormat.Format24bppRgb);
        using var destino = Graphics.FromImage(imagem);
        nint origem = GetDC(0);
        if (origem == 0) return null;
        try
        {
            nint dc = destino.GetHdc();
            try
            {
                SetStretchBltMode(dc, 4);
                if (!StretchBlt(dc, 0, 0, largura, altura, origem, rect.X, rect.Y, rect.Width, rect.Height, 0x00CC0020))
                    return null;
            }
            finally { destino.ReleaseHdc(dc); }
        }
        finally { ReleaseDC(0, origem); }
        if (!EntradaWindows.DesktopInterativo() || !Listar().Contains(monitor)) return null;
        using var bytes = new MemoryStream();
        imagem.Save(bytes, ImageFormat.Jpeg);
        return bytes.Length <= 750_000 ? bytes.ToArray() : null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct Info { public int Tamanho; public Rect Monitor, Trabalho; public uint Flags; }
    private delegate bool MonitorCallback(nint monitor, nint dc, nint retangulo, nint dados);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint dados);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool GetMonitorInfoW(nint monitor, ref Info info);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint janela, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint janela);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint janela, nint dc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint dc, int modo);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(nint destino, int x, int y, int largura, int altura,
        nint origem, int ox, int oy, int ow, int oh, uint raster);
}
