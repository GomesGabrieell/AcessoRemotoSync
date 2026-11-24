param([string]$DotNet = $env:DOTNET_EXE)

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot
$pasta = Join-Path $raiz 'artifacts\teste-web-local'
$servidor = Join-Path $pasta 'servidor\SyncDesk.Sinalizacao.dll'
$executavel = Join-Path $pasta 'SyncDesk.exe'
if (!(Test-Path -LiteralPath $servidor) -or !(Test-Path -LiteralPath $executavel)) {
    throw 'Publique o servidor e o executavel para o teste Web conforme o README.'
}
if (!$DotNet) {
    $comando = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue
    $DotNet = if ($comando) { $comando.Source } else { Join-Path $env:LOCALAPPDATA 'SyncDeskDev\dotnet\dotnet.exe' }
}
if (!(Get-Command $DotNet -CommandType Application -ErrorAction SilentlyContinue)) {
    throw 'O .NET 10 e necessario para iniciar o servidor local.'
}
$bytes = New-Object byte[] 32
$gerador = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $gerador.GetBytes($bytes) } finally { $gerador.Dispose() }
$senha = [Convert]::ToBase64String($bytes)
$variaveis = @{
    SyncDesk__Tecnicos__TecnicoLocal = $senha
    SyncDesk__ExecutavelCliente = $executavel
    SyncDesk__ArquivoCodigos = (Join-Path $pasta 'codigos.bin')
    SyncDesk__ArquivoHistoricoWeb = (Join-Path $pasta 'historico.json')
}
$anteriores = @{}
$processo = $null
try {
    try {
        foreach ($nome in $variaveis.Keys) {
            $anteriores[$nome] = [Environment]::GetEnvironmentVariable($nome)
            [Environment]::SetEnvironmentVariable($nome, $variaveis[$nome])
        }
        $inicio = @{
            FilePath = $DotNet
            ArgumentList = @('SyncDesk.Sinalizacao.dll','--teste-web-local')
            WorkingDirectory = Split-Path -Parent $servidor
            WindowStyle = 'Hidden'
            PassThru = $true
        }
        $processo = Start-Process @inicio
    }
    finally {
        foreach ($nome in $anteriores.Keys) { [Environment]::SetEnvironmentVariable($nome, $anteriores[$nome]) }
    }
    $pronto = $false
    for ($i = 0; $i -lt 40; $i++) {
        if ($processo.HasExited) { throw 'O servidor nao iniciou. Verifique se a porta 7446 ja esta em uso.' }
        try {
            $resposta = Invoke-WebRequest 'http://127.0.0.1:7446/teste-local/pronto' -UseBasicParsing -TimeoutSec 1
            if ($resposta.Content -eq 'SyncDesk teste local' -and !$processo.HasExited) { $pronto = $true; break }
        }
        catch { }
        Start-Sleep -Milliseconds 250
    }
    if (!$pronto) { throw 'O servidor nao respondeu.' }
    Write-Host 'Painel: http://127.0.0.1:7446/painel'
    Write-Host 'Usuario: TecnicoLocal'
    Write-Host "Credencial temporaria: $senha"
    Start-Process 'http://127.0.0.1:7446/painel'
    $null = Read-Host 'Pressione Enter para encerrar o servidor local'
}
finally {
    if ($processo -and !$processo.HasExited) {
        Stop-Process -Id $processo.Id
        $processo.WaitForExit()
    }
}
