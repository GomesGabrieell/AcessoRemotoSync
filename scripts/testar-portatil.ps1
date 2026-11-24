[CmdletBinding()]
param(
    [string]$Executavel = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/publicacao/SyncDesk.exe'),
    [ValidateRange(0, 30)]
    [int]$MedirEsperaSegundos = 0,
    [switch]$TesteLocal
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') {
    $opcoes = @('-NoProfile','-STA','-File',$PSCommandPath,'-Executavel',$Executavel,
        '-MedirEsperaSegundos',$MedirEsperaSegundos)
    if ($TesteLocal) { $opcoes += '-TesteLocal' }
    & powershell.exe @opcoes
    if ($LASTEXITCODE -ne 0) { throw "A verificacao nativa falhou (codigo $LASTEXITCODE)." }
    return
}

$executavelCompleto = (Resolve-Path -LiteralPath $Executavel).Path
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class SyncDeskToken {
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int type, out int value, int size, out int returned);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    public static bool Elevado(IntPtr processo) {
        IntPtr token;
        if (!OpenProcessToken(processo, 8, out token)) throw new Win32Exception();
        try {
            int value, size;
            if (!GetTokenInformation(token, 20, out value, sizeof(int), out size)) throw new Win32Exception();
            return value != 0;
        } finally { CloseHandle(token); }
    }
}
'@

$temporaria = Join-Path ([IO.Path]::GetTempPath()) ('SyncDesk-smoke-' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($temporaria)
$tipo = [Windows.Automation.AutomationElement]
$escopo = [Windows.Automation.TreeScope]
$raiz = $tipo::RootElement

function Medir-Arvore([int]$Identificador) {
    $filhos = Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId |
        Group-Object ParentProcessId -AsHashTable -AsString
    $fila = New-Object 'System.Collections.Generic.Queue[int]'
    $visitados = New-Object 'System.Collections.Generic.HashSet[int]'
    $fila.Enqueue($Identificador)
    $medidas = @{}
    while ($fila.Count) {
        $atual = $fila.Dequeue()
        if (!$visitados.Add($atual)) { continue }
        foreach ($filho in @($filhos[[string]$atual])) {
            if ($filho) { $fila.Enqueue([int]$filho.ProcessId) }
        }
        $item = Get-Process -Id $atual -ErrorAction SilentlyContinue
        if ($item) {
            try {
                $medidas[$atual] = @{ Cpu = $item.TotalProcessorTime.TotalSeconds; Privada = $item.PrivateMemorySize64 }
            }
            catch [InvalidOperationException] { }
            finally { $item.Dispose() }
        }
    }
    return $medidas
}

function Testar-Janela([string]$Argumentos, [string]$TituloPainel) {
    $inicio = New-Object Diagnostics.ProcessStartInfo
    $inicio.FileName = $executavelCompleto
    $inicio.WorkingDirectory = Split-Path -Parent $executavelCompleto
    $inicio.Arguments = $Argumentos
    $inicio.UseShellExecute = $false
    $inicio.EnvironmentVariables['TEMP'] = $temporaria
    $inicio.EnvironmentVariables['TMP'] = $temporaria
    $inicio.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $temporaria 'dotnet'
    $processo = [Diagnostics.Process]::Start($inicio)
    try {
        if ([SyncDeskToken]::Elevado($processo.Handle)) {
            throw 'O processo estava elevado. Rode este teste a partir de uma sessao sem elevacao.'
        }
        $porProcesso = [Windows.Automation.PropertyCondition]::new($tipo::ProcessIdProperty, $processo.Id)
        $porTexto = [Windows.Automation.PropertyCondition]::new($tipo::NameProperty, $TituloPainel)
        $janela = $null
        $painel = $null
        for ($i = 0; $i -lt 125; $i++) {
            if ($processo.HasExited) { throw "SyncDesk fechou durante a inicializacao (codigo $($processo.ExitCode))." }
            $janela = $raiz.FindFirst($escopo::Children, $porProcesso)
            if ($janela) { $painel = $janela.FindFirst($escopo::Descendants, $porTexto) }
            if ($painel) { break }
            Start-Sleep -Milliseconds 200
        }
        if (!$painel) { throw "O painel '$TituloPainel' nao apareceu." }
        if ($TesteLocal) {
            $estado = if ($Argumentos -like '*--tecnico*') { 'Aguardando c' + [char]0xF3 + 'digo' }
                      else { 'Aguardando conex' + [char]0xE3 + 'o' }
            $esperado = [Windows.Automation.PropertyCondition]::new($tipo::NameProperty, $estado)
            $conectou = $null
            for ($i = 0; $i -lt 50; $i++) {
                $conectou = $janela.FindFirst($escopo::Descendants, $esperado)
                if ($conectou) { break }
                Start-Sleep -Milliseconds 200
            }
            if (!$conectou) { throw "O servidor de teste nao conectou ao painel '$TituloPainel'." }
            if ($Argumentos -like '*--tecnico*') {
                $rotulo = 'Credencial do t' + [char]0xE9 + 'cnico'
                $porCredencial = [Windows.Automation.PropertyCondition]::new($tipo::NameProperty, $rotulo)
                if ($janela.FindFirst($escopo::Descendants, $porCredencial)) {
                    throw 'O modo de teste ainda exibe a credencial.'
                }
            }
            if ($Argumentos -notlike '*--tecnico*') {
                $textos = $janela.FindAll($escopo::Descendants, [Windows.Automation.Condition]::TrueCondition)
                if (!($textos | Where-Object { $_.Current.Name -match '^\d{3} \d{3}$' })) {
                    throw 'O painel Cliente nao exibiu um codigo de seis digitos.'
                }
            }
            Write-Output "OK $($TituloPainel): sinalizacao local HTTP/WS."
        }
        $perfis = @(Get-ChildItem -LiteralPath $temporaria -Directory -Filter 'SyncDesk-WebView2-*')
        if ($perfis.Count -ne 1) { throw 'O aplicativo nao criou um perfil temporario isolado.' }
        if ($MedirEsperaSegundos -gt 0) {
            Start-Sleep -Seconds 2
            $primeira = Medir-Arvore $processo.Id
            $tempo = [Diagnostics.Stopwatch]::StartNew()
            Start-Sleep -Seconds $MedirEsperaSegundos
            $segunda = Medir-Arvore $processo.Id
            $cpu = 0; $memoria = 0
            foreach ($id in $segunda.Keys) {
                if ($primeira.ContainsKey($id)) { $cpu += [Math]::Max(0, $segunda[$id].Cpu - $primeira[$id].Cpu) }
                $memoria += $segunda[$id].Privada
            }
            Write-Output ("Espera {0}: {1:F2}% de um nucleo, {2:F1} MiB privados, {3} processos." -f $TituloPainel,
                (100 * $cpu / $tempo.Elapsed.TotalSeconds), ($memoria / 1MB), $segunda.Count)
        }
        $janela.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
        if (!$processo.WaitForExit(18000)) { throw 'A janela nao encerrou normalmente.' }
        if (Test-Path -LiteralPath $perfis[0].FullName) { throw 'O perfil temporario permaneceu apos o fechamento.' }
        Write-Output "OK $($TituloPainel): inicializacao sem elevacao, fechamento e limpeza do perfil."
    }
    finally {
        if (!$processo.HasExited) { $processo.Kill(); $processo.WaitForExit() }
        $processo.Dispose()
    }
}

try {
    $stream = [IO.File]::OpenRead($executavelCompleto)
    try {
        $leitor = [IO.BinaryReader]::new($stream)
        $stream.Position = 0x3C
        $stream.Position = $leitor.ReadInt32()
        if ($leitor.ReadUInt32() -ne 0x00004550 -or $leitor.ReadUInt16() -ne 0x8664) {
            throw 'O arquivo nao e um executavel Windows x64.'
        }
    }
    finally { $stream.Dispose() }
    $argumentoLocal = if ($TesteLocal) { '--teste-local' } else { '' }
    Testar-Janela $argumentoLocal 'Receber suporte'
    Testar-Janela "$argumentoLocal --tecnico" 'Prestar suporte'
    Write-Output "Assinatura Authenticode: $((Get-AuthenticodeSignature -LiteralPath $executavelCompleto).Status)"
}
finally {
    $localTemporario = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    $pastaTemporaria = [IO.Path]::GetFullPath($temporaria)
    if ([IO.Path]::GetDirectoryName($pastaTemporaria) -eq $localTemporario -and
        [IO.Path]::GetFileName($pastaTemporaria).StartsWith('SyncDesk-smoke-')) {
        for ($tentativa = 0; $tentativa -lt 20; $tentativa++) {
            try {
                if (Test-Path -LiteralPath $pastaTemporaria) {
                    Remove-Item -LiteralPath $pastaTemporaria -Force -Recurse
                }
                break
            }
            catch [IO.IOException] {
                if ($tentativa -eq 19) { Write-Warning 'O ambiente de teste ainda tem arquivos temporarios ocupados.' }
                else { Start-Sleep -Milliseconds 250 }
            }
        }
    }
}
