using System.Reflection;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using SyncDesk.Views;

namespace SyncDesk;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string arquivo = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "";
        var convite = Regex.Match(arquivo, @"^SyncDesk-((?:web-)?local-)?([0-9a-f]{48})(?: \([0-9]+\))?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        bool testeWebLocal = e.Args.Contains("--teste-web-local", StringComparer.OrdinalIgnoreCase) ||
            convite.Groups[1].Value.Equals("web-local-", StringComparison.OrdinalIgnoreCase);
        bool testeLocal = testeWebLocal || e.Args.Contains("--teste-local", StringComparer.OrdinalIgnoreCase) || convite.Groups[1].Success;
        var servidor = testeLocal ? $"http://127.0.0.1:{(testeWebLocal ? 7446 : 7445)}" :
            typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(item => item.Key == "SyncDeskServerUrl").Value;
        if (!Uri.TryCreate(servidor, UriKind.Absolute, out var origem) ||
            (!testeLocal && origem.Scheme != Uri.UriSchemeHttps) || origem.AbsolutePath != "/" ||
            origem.UserInfo.Length != 0 || origem.Query.Length != 0 || origem.Fragment.Length != 0)
        {
            MessageBox.Show("O endereço HTTPS do servidor SyncDesk é inválido.", "SyncDesk");
            Shutdown(1);
            return;
        }

        bool tecnico = e.Args.Contains("--tecnico", StringComparer.OrdinalIgnoreCase);
        MainWindow = new AtendimentoWindow(origem, tecnico,
            !tecnico && convite.Success ? convite.Groups[2].Value.ToLowerInvariant() : null);
        MainWindow.Show();
    }
}
