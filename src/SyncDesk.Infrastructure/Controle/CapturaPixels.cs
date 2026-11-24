using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SyncDesk.Controle;

internal sealed class CapturaPixels(MonitorWindows monitor) : IDisposable
{
    public (int Largura, int Altura) Tamanho { get; } = Dimensionar(monitor.Limites.Width, monitor.Limites.Height);
    private Duplicacao? _dxgi;
    private CapturaGdi? _gdi;
    private long _reavaliar;
    public string Fonte => _dxgi is null ? "GDI" : "DXGI";

    internal static (int Largura, int Altura) Dimensionar(int largura, int altura)
    {
        double escala = Math.Min(1, Math.Min(3840.0 / Math.Max(largura, altura),
            Math.Sqrt(8_294_400.0 / ((double)largura * altura))));
        return (Math.Max(1, (int)(largura * escala)), Math.Max(1, (int)(altura * escala)));
    }

    public bool Capturar(nint destino, bool repetir)
    {
        if (!EntradaWindows.DesktopInterativo()) return false;
        if (_dxgi is null && Tamanho == (monitor.Limites.Width, monitor.Limites.Height) &&
            Environment.TickCount64 >= _reavaliar)
        {
            _reavaliar = Environment.TickCount64 + 5_000;
            try { _dxgi = Duplicacao.Abrir(monitor); }
            catch (SharpGenException) { }
        }
        if (_dxgi is not null)
        {
            try { return _dxgi.Capturar(destino, repetir); }
            catch (SharpGenException)
            {
                _dxgi.Dispose();
                _dxgi = null;
                _reavaliar = Environment.TickCount64 + 5_000;
            }
        }

        if (!MonitoresWindows.Listar().Contains(monitor)) return false;
        _gdi ??= new CapturaGdi(Tamanho.Largura, Tamanho.Altura);
        return _gdi.Capturar(monitor, Tamanho.Largura, Tamanho.Altura, destino);
    }

    public void Dispose()
    {
        _dxgi?.Dispose();
        _gdi?.Dispose();
    }

    private sealed class Duplicacao(ID3D11Device dispositivo, ID3D11DeviceContext contexto,
        IDXGIOutputDuplication duplicacao, ID3D11Texture2D textura, int largura, int altura) : IDisposable
    {
        private bool _temImagem;
        private const int TempoEsgotado = unchecked((int)0x887A0027);

        public static Duplicacao? Abrir(MonitorWindows monitor)
        {
            using var fabrica = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; fabrica.EnumAdapters1(i, out var adaptador).Success; i++)
            using (adaptador)
            {
                for (uint j = 0; adaptador.EnumOutputs(j, out var saida).Success; j++)
                using (saida)
                {
                    var info = saida.Description;
                    if (info.Monitor != monitor.Handle) continue;
                    // A superfície DXGI de telas giradas usa a orientação do adaptador.
                    if (info.Rotation != ModeRotation.Identity) return null;
                    using var saida1 = saida.QueryInterface<IDXGIOutput1>();
                    D3D11.D3D11CreateDevice(adaptador, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                        [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
                        out ID3D11Device dispositivo, out ID3D11DeviceContext contexto).CheckError();
                    IDXGIOutputDuplication? duplicacao = null;
                    try
                    {
                        duplicacao = saida1.DuplicateOutput(dispositivo);
                        int largura = monitor.Limites.Width, altura = monitor.Limites.Height;
                        var textura = dispositivo.CreateTexture2D(new Texture2DDescription
                        {
                            Width = (uint)largura, Height = (uint)altura, Format = Format.B8G8R8A8_UNorm,
                            MipLevels = 1, ArraySize = 1, SampleDescription = new(1, 0),
                            Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
                        });
                        return new(dispositivo, contexto, duplicacao, textura, largura, altura);
                    }
                    catch
                    {
                        duplicacao?.Dispose();
                        contexto.Dispose();
                        dispositivo.Dispose();
                        throw;
                    }
                }
            }
            return null;
        }

        public unsafe bool Capturar(nint destino, bool repetir)
        {
            var resultado = duplicacao.AcquireNextFrame(_temImagem ? 0u : 45u, out var info, out var recurso);
            if (resultado.Code == TempoEsgotado) return _temImagem && repetir;
            resultado.CheckError();
            try
            {
                using (recurso)
                {
                    if (_temImagem && info.LastPresentTime == 0) return repetir;
                    using var atual = recurso.QueryInterface<ID3D11Texture2D>();
                    var dimensoes = atual.Description;
                    if (dimensoes.Width != largura || dimensoes.Height != altura) return false;
                    contexto.CopyResource(textura, atual);
                }
                var mapa = contexto.Map(textura, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    int linha = largura * 4;
                    for (int y = 0; y < altura; y++)
                        Buffer.MemoryCopy((byte*)mapa.DataPointer + y * mapa.RowPitch,
                            (byte*)destino + y * linha, linha, linha);
                }
                finally { contexto.Unmap(textura, 0); }
                return _temImagem = true;
            }
            finally { duplicacao.ReleaseFrame(); }
        }

        public void Dispose()
        {
            textura.Dispose();
            duplicacao.Dispose();
            contexto.Dispose();
            dispositivo.Dispose();
        }
    }

    private sealed class CapturaGdi : IDisposable
    {
        private readonly nint _dc, _imagem, _anterior, _pixels;

        public CapturaGdi(int largura, int altura)
        {
            var dados = new BitmapInfo { Tamanho = 40, Largura = largura, Altura = -altura, Planos = 1, Bits = 32 };
            nint origem = GetDC(0);
            if (origem == 0) return;
            try { _dc = CreateCompatibleDC(origem); }
            finally { ReleaseDC(0, origem); }
            if (_dc == 0) return;
            _imagem = CreateDIBSection(_dc, ref dados, 0, out _pixels, 0, 0);
            if (_imagem != 0) _anterior = SelectObject(_dc, _imagem);
            SetStretchBltMode(_dc, 4);
        }

        public unsafe bool Capturar(MonitorWindows monitor, int largura, int altura, nint destino)
        {
            if (_anterior == 0 || _anterior == -1) return false;
            nint origem = GetDC(0);
            if (origem == 0) return false;
            var r = monitor.Limites;
            try
            {
                if (!StretchBlt(_dc, 0, 0, largura, altura, origem, r.Left, r.Top, r.Width, r.Height, 0x00CC0020))
                    return false;
                GdiFlush();
                int tamanho = largura * altura * 4;
                Buffer.MemoryCopy((void*)_pixels, (void*)destino, tamanho, tamanho);
                return true;
            }
            finally { ReleaseDC(0, origem); }
        }

        public void Dispose()
        {
            if (_anterior != 0 && _anterior != -1) SelectObject(_dc, _anterior);
            if (_imagem != 0) DeleteObject(_imagem);
            if (_dc != 0) DeleteDC(_dc);
        }

        [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
        {
            public uint Tamanho;
            public int Largura, Altura;
            public ushort Planos, Bits;
            public uint Compressao, Bytes;
            public int DpiX, DpiY;
            public uint Cores, CoresImportantes;
        }
        [DllImport("user32.dll")] private static extern nint GetDC(nint janela);
        [DllImport("user32.dll")] private static extern int ReleaseDC(nint janela, nint dc);
        [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
        [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint uso, out nint pixels, nint secao, uint deslocamento);
        [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint objeto);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint objeto);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
        [DllImport("gdi32.dll")] private static extern bool GdiFlush();
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint dc, int modo);
        [DllImport("gdi32.dll")] private static extern bool StretchBlt(nint destino, int x, int y, int largura, int altura,
            nint origem, int ox, int oy, int ow, int oh, uint raster);
    }
}
