#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$Parar,
    [ValidateRange(1024, 65535)][int]$Porta = 7450,
    [string]$DotNet = $(if ($env:DOTNET_EXE) { $env:DOTNET_EXE } else { 'dotnet' })
)

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot
$pasta = Join-Path $env:LOCALAPPDATA 'SyncDesk/piloto-cloudflare'
$estado = Join-Path $pasta 'processos.json'
$acesso = Join-Path $pasta 'ACESSO.txt'

function Processo-Registrado($registro) {
    $p = Get-Process -Id $registro.id -ErrorAction SilentlyContinue
    if ($p -and $p.StartTime.ToUniversalTime().Ticks -eq [long]$registro.inicio) { return $p }
}

function Parar-Piloto {
    if (Test-Path -LiteralPath $estado) {
        $processos = Get-Content -LiteralPath $estado -Raw | ConvertFrom-Json
        foreach ($registro in $processos) {
            $p = Processo-Registrado $registro
            if ($p) { $p.Kill(); $p.WaitForExit() }
        }
    }
    foreach ($arquivo in @($estado, $acesso)) {
        if (Test-Path -LiteralPath $arquivo) { Remove-Item -LiteralPath $arquivo }
    }
}

if ($Parar) {
    Parar-Piloto
    Write-Host 'Piloto Cloudflare encerrado.'
    return
}

if (!(Get-Command $DotNet -CommandType Application -ErrorAction SilentlyContinue)) {
    $DotNet = Join-Path $env:LOCALAPPDATA 'SyncDeskDev/dotnet/dotnet.exe'
    if (!(Test-Path -LiteralPath $DotNet)) { throw 'Instale o SDK .NET 10 ou informe -DotNet.' }
}
$cloudflared = (Get-Command cloudflared -CommandType Application -ErrorAction Stop).Source
if (Test-Path -LiteralPath $estado) {
    $registros = Get-Content -LiteralPath $estado -Raw | ConvertFrom-Json
    foreach ($registro in $registros) {
        if (Processo-Registrado $registro) { throw 'Já há um piloto ativo. Execute este script com -Parar para encerrá-lo.' }
    }
    Parar-Piloto
}

$null = New-Item -ItemType Directory -Force -Path $pasta
$pfx = Join-Path $pasta 'origem.pfx'
$pem = Join-Path $pasta 'origem-publica.pem'
$log = Join-Path $pasta 'tunnel-atual.log'
if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log }
$senhaPfx = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
$senhaTecnico = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$usuario = 'TecnicoPiloto'
$rsa = [Security.Cryptography.RSA]::Create(2048)
try {
    $pedido = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost', $rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $san.AddDnsName('localhost')
    $pedido.CertificateExtensions.Add($san.Build())
    $cert = $pedido.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddDays(7))
    try {
        [IO.File]::WriteAllText($pem, $cert.ExportCertificatePem())
        [IO.File]::WriteAllBytes($pfx, $cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $senhaPfx))
    } finally { $cert.Dispose() }
} finally { $rsa.Dispose() }

$tunnel = $null
$backend = $null
try {
    $urlOrigem = "https://127.0.0.1:$Porta"
    $tunnel = Start-Process -FilePath $cloudflared -ArgumentList @('tunnel', '--protocol', 'http2',
        '--url', $urlOrigem, '--origin-server-name', 'localhost', '--origin-ca-pool', ('"' + $pem + '"'),
        '--logfile', ('"' + $log + '"'), '--no-autoupdate') -WindowStyle Hidden -PassThru
    $publico = $null
    for ($i = 0; $i -lt 60; $i++) {
        if ($tunnel.HasExited) { throw "O túnel encerrou durante a inicialização. Consulte $log" }
        if (Test-Path -LiteralPath $log) {
            $match = [regex]::Match((Get-Content -LiteralPath $log -Raw), 'https://[a-z0-9-]+\.trycloudflare\.com')
            if ($match.Success) { $publico = $match.Value; break }
        }
        Start-Sleep -Milliseconds 500
    }
    if (!$publico) { throw "A Cloudflare não forneceu um endereço público. Consulte $log" }

    $publicacao = Join-Path $pasta 'cliente'
    $servidor = Join-Path $pasta 'servidor'
    & (Join-Path $PSScriptRoot 'publicar.ps1') -Servidor $publico -Destino $publicacao -DotNet $DotNet
    & $DotNet publish (Join-Path $raiz 'src/SyncDesk.Sinalizacao') -c Release -o $servidor
    if ($LASTEXITCODE -ne 0) { throw 'A publicação do servidor falhou.' }

    $config = @{
        ASPNETCORE_ENVIRONMENT = 'Production'
        DOTNET_ENVIRONMENT = 'Production'
        ASPNETCORE_Kestrel__Certificates__Default__Path = $pfx
        ASPNETCORE_Kestrel__Certificates__Default__Password = $senhaPfx
        SyncDesk__Tecnicos__TecnicoPiloto = $senhaTecnico
        SyncDesk__ExecutavelCliente = (Join-Path $publicacao 'SyncDesk.exe')
        SyncDesk__ArquivoCodigos = (Join-Path $pasta 'codigos.bin')
        SyncDesk__ArquivoHistoricoWeb = (Join-Path $pasta 'historico.json')
        SyncDesk__Stun__0 = 'stun:stun.cloudflare.com:3478'
    }
    $anteriores = @{}
    try {
        foreach ($nome in $config.Keys) {
            $anteriores[$nome] = [Environment]::GetEnvironmentVariable($nome)
            [Environment]::SetEnvironmentVariable($nome, $config[$nome])
        }
        $inicio = @{
            FilePath = $DotNet
            ArgumentList = @('SyncDesk.Sinalizacao.dll', '--urls', $urlOrigem)
            WorkingDirectory = $servidor
            WindowStyle = 'Hidden'
            RedirectStandardOutput = Join-Path $pasta 'servidor.log'
            RedirectStandardError = Join-Path $pasta 'servidor-erro.log'
            PassThru = $true
        }
        $backend = Start-Process @inicio
    } finally {
        foreach ($nome in $anteriores.Keys) { [Environment]::SetEnvironmentVariable($nome, $anteriores[$nome]) }
    }

    $pronto = $false
    for ($i = 0; $i -lt 45; $i++) {
        if ($backend.HasExited -or $tunnel.HasExited) { throw "Um processo encerrou. Consulte os logs em $pasta" }
        try {
            $resposta = Invoke-WebRequest "$publico/painel" -TimeoutSec 5 -SkipHttpErrorCheck
            if ($resposta.StatusCode -eq 200 -and $resposta.Content -match 'id="novo-link"') { $pronto = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 700
    }
    if (!$pronto) { throw "O painel público não respondeu. Consulte os logs em $pasta" }
    $registros = @($tunnel, $backend) | ForEach-Object {
        @{ id = $_.Id; inicio = [string]$_.StartTime.ToUniversalTime().Ticks }
    }
    $registros | ConvertTo-Json -AsArray | Set-Content -LiteralPath $estado
    @("Painel: $publico/painel", "Usuário: $usuario", "Credencial: $senhaTecnico") | Set-Content -LiteralPath $acesso
    Write-Host "Painel publicado em $publico/painel"
    Write-Host "Dados de acesso em $acesso"
} catch {
    foreach ($p in @($backend, $tunnel)) {
        if ($p -and !$p.HasExited) { $p.Kill(); $p.WaitForExit() }
    }
    Parar-Piloto
    throw
}
