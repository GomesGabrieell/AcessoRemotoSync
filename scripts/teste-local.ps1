param([string]$DotNet = $env:DOTNET_EXE, [switch]$Verificar)

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot
$servidor = Join-Path $raiz 'artifacts\teste-local-telas\servidor\SyncDesk.Sinalizacao.dll'
$executavel = Join-Path $raiz 'artifacts\teste-local-telas\SyncDesk.exe'
if (!(Test-Path -LiteralPath $servidor) -or !(Test-Path -LiteralPath $executavel)) {
    throw 'Publique o servidor e o executavel de teste antes de iniciar.'
}
if (!$DotNet) {
    $comando = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue
    $DotNet = if ($comando) { $comando.Source } else { Join-Path $env:LOCALAPPDATA 'SyncDeskDev\dotnet\dotnet.exe' }
}
if (!(Get-Command $DotNet -CommandType Application -ErrorAction SilentlyContinue)) {
    throw 'O .NET 10 e necessario para iniciar o servidor de teste. Informe -DotNet com o caminho do dotnet.exe.'
}

$codigos = Join-Path $env:TEMP ('syncdesk-local-' + [guid]::NewGuid().ToString('N') + '.bin')
$arquivoAnterior = $env:SyncDesk__ArquivoCodigos
$processo = $null

function Testar-SolicitacaoLocal {
    $clienteWs = New-Object System.Net.WebSockets.ClientWebSocket
    $tecnicoWs = New-Object System.Net.WebSockets.ClientWebSocket
    $prazo = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(10))
    function Ler-Mensagem($socket) {
        $buffer = New-Object byte[] 16384
        $trecho = [ArraySegment[byte]]::new($buffer)
        $lido = $socket.ReceiveAsync($trecho, $prazo.Token).GetAwaiter().GetResult()
        if (!$lido.EndOfMessage -or $lido.MessageType -ne 'Text') { throw 'Resposta de teste invalida.' }
        [Text.Encoding]::UTF8.GetString($buffer, 0, $lido.Count) | ConvertFrom-Json
    }
    try {
        foreach ($socket in @($clienteWs, $tecnicoWs)) {
            $socket.Options.SetRequestHeader('Origin', 'http://127.0.0.1:7445')
        }
        $clienteWs.ConnectAsync([uri]'ws://127.0.0.1:7445/ws/cliente', $prazo.Token).GetAwaiter().GetResult()
        $tecnicoWs.ConnectAsync([uri]'ws://127.0.0.1:7445/ws/tecnico', $prazo.Token).GetAwaiter().GetResult()
        $registro = Ler-Mensagem $clienteWs
        if ($registro.tipo -ne 'registrado') { throw 'O Cliente nao recebeu o codigo.' }
        $dados = @{ tipo = 'solicitar'; codigo = $registro.codigo } | ConvertTo-Json -Compress
        $bytes = [Text.Encoding]::UTF8.GetBytes($dados)
        $tecnicoWs.SendAsync([ArraySegment[byte]]::new($bytes), 'Text', $true, $prazo.Token).GetAwaiter().GetResult()
        if ((Ler-Mensagem $tecnicoWs).tipo -ne 'aguardando' -or (Ler-Mensagem $clienteWs).tipo -ne 'solicitacao') {
            throw 'A solicitacao apenas com codigo nao chegou ao Cliente.'
        }
        Write-Host 'OK O codigo sozinho envia a solicitacao e aguarda o aceite.'
    }
    finally {
        $prazo.Dispose()
        $clienteWs.Dispose()
        $tecnicoWs.Dispose()
    }
}

try {
    try {
        $env:SyncDesk__ArquivoCodigos = $codigos
        $inicio = @{
            FilePath = $DotNet; ArgumentList = @('SyncDesk.Sinalizacao.dll','--teste-local')
            WorkingDirectory = Split-Path -Parent $servidor
            WindowStyle = 'Hidden'; PassThru = $true
        }
        $processo = Start-Process @inicio
    }
    finally {
        $env:SyncDesk__ArquivoCodigos = $arquivoAnterior
    }
    $pronto = $false
    for ($i = 0; $i -lt 30; $i++) {
        if ($processo.HasExited) { throw 'O servidor local nao iniciou. Confira se a porta 7445 ja esta em uso.' }
        try {
            $resposta = Invoke-WebRequest 'http://127.0.0.1:7445/teste-local/pronto' -UseBasicParsing -TimeoutSec 1
            if ($resposta.Content -eq 'SyncDesk teste local' -and !$processo.HasExited) { $pronto = $true; break }
        }
        catch { }
        Start-Sleep -Milliseconds 250
    }
    if (!$pronto) { throw 'O servidor de teste local nao respondeu.' }
    if ($Verificar) {
        $escutas = @(Get-NetTCPConnection -OwningProcess $processo.Id -State Listen)
        if ($escutas.Count -ne 1 -or $escutas[0].LocalAddress -ne '127.0.0.1' -or $escutas[0].LocalPort -ne 7445) {
            throw 'O servidor de teste abriu uma escuta fora do loopback.'
        }
        Testar-SolicitacaoLocal
        & (Join-Path $PSScriptRoot 'testar-portatil.ps1') -Executavel $executavel -TesteLocal
        & (Join-Path $PSScriptRoot 'testar-sessao-local.ps1') -Executavel $executavel
        return
    }

    Write-Host 'SyncDesk em teste local neste computador.'
    Write-Host 'Copie o codigo do Cliente para o painel do Tecnico.'
    Write-Host 'Feche as duas janelas do SyncDesk para encerrar o servidor de teste.'
    $cliente = Start-Process -FilePath $executavel -ArgumentList '--teste-local' -PassThru
    $tecnico = Start-Process -FilePath $executavel -ArgumentList '--teste-local','--tecnico' -PassThru
    while (!$cliente.HasExited -or !$tecnico.HasExited) {
        if ($processo.HasExited) { throw 'O servidor de teste local foi interrompido.' }
        Start-Sleep -Milliseconds 500
    }
}
finally {
    if ($processo -and !$processo.HasExited) {
        Stop-Process -Id $processo.Id
        $processo.WaitForExit()
    }
    if (Test-Path -LiteralPath $codigos) { Remove-Item -LiteralPath $codigos }
}
