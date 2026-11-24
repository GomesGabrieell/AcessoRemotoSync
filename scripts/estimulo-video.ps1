param([ValidateRange(5, 45)][int]$Segundos = 20)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$janela = [Windows.Window]::new()
$janela.Title = 'SyncDesk - cena de teste de video'
$janela.WindowStyle = 'None'
$janela.WindowState = 'Maximized'
$janela.ShowInTaskbar = $false
$janela.Topmost = $true
$janela.Background = [Windows.Media.Brushes]::White
$tela = [Windows.Controls.Canvas]::new()
$tela.ClipToBounds = $true
$janela.Content = $tela
$relogio = [Diagnostics.Stopwatch]::new()
$titulo = [Windows.Controls.TextBlock]::new()
$titulo.Text = 'SyncDesk - planilha e movimento sinteticos'
$titulo.FontSize = 24
[Windows.Controls.Canvas]::SetLeft($titulo, 24)
[Windows.Controls.Canvas]::SetTop($titulo, 18)
$null = $tela.Children.Add($titulo)
$faixas = @(foreach ($i in 0..15) {
    $painel = [Windows.Controls.Border]::new()
    $painel.Width = 430
    $painel.Height = 110
    $painel.BorderThickness = [Windows.Thickness]::new(1)
    $painel.BorderBrush = [Windows.Media.Brushes]::SteelBlue
    $painel.Background = if ($i % 2) { [Windows.Media.Brushes]::AliceBlue } else { [Windows.Media.Brushes]::WhiteSmoke }
    $texto = [Windows.Controls.TextBlock]::new()
    $texto.FontFamily = [Windows.Media.FontFamily]::new('Consolas')
    $texto.FontSize = 12
    $texto.Margin = [Windows.Thickness]::new(8)
    $texto.Text = ((1..6) | ForEach-Object { 'Consulta {0:00}/{1:00} | 0987654321 | 37,45 | 152,98' -f $i,$_ }) -join [Environment]::NewLine
    $painel.Child = $texto
    $null = $tela.Children.Add($painel)
    $painel
})
$animar = [EventHandler] {
    $tempo = $relogio.Elapsed.TotalSeconds
    if ($tempo -ge $Segundos) { $janela.Close(); return }
    for ($i = 0; $i -lt $faixas.Count; $i++) {
        $x = 24 + 460 * ($i % 4) + [Math]::Sin($tempo * 4.9 + $i) * 115
        $y = 72 + (126 * $i + 510 * $tempo) % [Math]::Max(240, $tela.ActualHeight - 50)
        [Windows.Controls.Canvas]::SetLeft($faixas[$i], $x)
        [Windows.Controls.Canvas]::SetTop($faixas[$i], $y)
    }
}
$janela.Add_ContentRendered({
    if ($relogio.IsRunning) { return }
    $relogio.Start()
    [Windows.Media.CompositionTarget]::add_Rendering($animar)
    [Console]::WriteLine('CENA_PRONTA')
})
try { $null = $janela.ShowDialog() }
finally { [Windows.Media.CompositionTarget]::remove_Rendering($animar) }
