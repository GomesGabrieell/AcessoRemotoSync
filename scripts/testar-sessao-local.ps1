param([Parameter(Mandatory)][string]$Executavel)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms

$elemento = [Windows.Automation.AutomationElement]
$escopo = [Windows.Automation.TreeScope]
$raiz = $elemento::RootElement
$arquivo = (Resolve-Path -LiteralPath $Executavel).Path
$cliente = $null
$tecnico = $null

function Aguardar([string]$Descricao, [scriptblock]$Consultar) {
    $tempo = [Diagnostics.Stopwatch]::StartNew()
    do {
        $resultado = & $Consultar
        if ($resultado) { return $resultado }
        Start-Sleep -Milliseconds 250
    } while ($tempo.Elapsed.TotalSeconds -lt 20)
    throw "Tempo excedido: $Descricao."
}

function Localizar-Janela($Processo) {
    $porProcesso = [Windows.Automation.PropertyCondition]::new($elemento::ProcessIdProperty, $Processo.Id)
    Aguardar 'janela do SyncDesk' { $raiz.FindFirst($escopo::Children, $porProcesso) }
}

function Localizar-Nome($Janela, [string]$Nome, [Windows.Automation.ControlType]$Tipo = $null) {
    $condicao = [Windows.Automation.PropertyCondition]::new($elemento::NameProperty, $Nome)
    if ($Tipo) {
        $porTipo = [Windows.Automation.PropertyCondition]::new($elemento::ControlTypeProperty, $Tipo)
        $condicao = [Windows.Automation.AndCondition]::new($condicao, $porTipo)
    }
    Aguardar $Nome { $Janela.FindFirst($escopo::Descendants, $condicao) }
}

function Ler-Codigo($Janela, [string]$Anterior = '') {
    Aguardar 'codigo de atendimento' {
        $itens = $Janela.FindAll($escopo::Descendants, [Windows.Automation.Condition]::TrueCondition)
        foreach ($item in $itens) {
            $nome = $item.Current.Name
            if ($nome -match '^[0-9]{3} [0-9]{3}$' -and $nome -ne $Anterior) { return $nome }
        }
    }
}

function Acionar($Janela, [string]$Nome) {
    $botao = Localizar-Nome $Janela $Nome ([Windows.Automation.ControlType]::Button)
    $padrao = $null
    if ($botao.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$padrao)) {
        $padrao.Invoke()
        return
    }
    if ($botao.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$padrao)) {
        $padrao.Toggle()
        return
    }
    throw "O controle '$Nome' nao oferece Invoke nem Toggle."
}

try {
    $cliente = Start-Process -FilePath $arquivo -ArgumentList '--teste-local' -PassThru
    $tecnico = Start-Process -FilePath $arquivo -ArgumentList '--teste-local','--tecnico' -PassThru
    $janelaCliente = Localizar-Janela $cliente
    $janelaTecnico = Localizar-Janela $tecnico
    $codigo = Ler-Codigo $janelaCliente
    $campo = Localizar-Nome $janelaTecnico ('C' + [char]0xF3 + 'digo do cliente') ([Windows.Automation.ControlType]::Edit)
    $campo.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($codigo)
    Acionar $janelaTecnico 'Conectar'
    Acionar $janelaCliente 'Autorizar acesso'
    $prefixo = 'Tela compartilhada pelo cliente ' + [char]0xB7 + ' Tela '
    $null = Localizar-Nome $janelaTecnico ($prefixo + '1')
    Write-Host 'OK Sessao local real: aceite do cliente e video do monitor principal.'

    if ([Windows.Forms.Screen]::AllScreens.Count -gt 1) {
        Acionar $janelaTecnico 'Tela 2'
        $null = Localizar-Nome $janelaTecnico ($prefixo + '2')
        Acionar $janelaTecnico 'Tela 1'
        $null = Localizar-Nome $janelaTecnico ($prefixo + '1')
        Write-Host 'OK Sessao local real: video alternado entre dois monitores fisicos.'
    }

    Acionar $janelaCliente 'Encerrar atendimento'
    $null = Localizar-Nome $janelaTecnico 'O cliente encerrou o atendimento.'
    $null = Ler-Codigo $janelaCliente $codigo
    Write-Host 'OK Sessao local real: fim do video e novo codigo.'
}
finally {
    foreach ($processo in @($cliente, $tecnico)) {
        if (!$processo) { continue }
        if (!$processo.HasExited) {
            $null = $processo.CloseMainWindow()
            if (!$processo.WaitForExit(15000)) { $processo.Kill(); $processo.WaitForExit() }
        }
        $processo.Dispose()
    }
}
