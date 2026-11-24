param([Parameter(Mandatory)][int]$Id, [switch]$Controle)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$elemento = [Windows.Automation.AutomationElement]
$escopo = [Windows.Automation.TreeScope]
$raiz = $elemento::RootElement
$porProcesso = [Windows.Automation.PropertyCondition]::new($elemento::ProcessIdProperty, $Id)
$porNome = [Windows.Automation.PropertyCondition]::new($elemento::NameProperty, 'Autorizar acesso')
$porTipo = [Windows.Automation.PropertyCondition]::new($elemento::ControlTypeProperty, [Windows.Automation.ControlType]::Button)
$porBotao = [Windows.Automation.AndCondition]::new($porNome, $porTipo)
$nomeControle = [Windows.Automation.PropertyCondition]::new($elemento::NameProperty, 'Permitir mouse e teclado')
$tipoControle = [Windows.Automation.PropertyCondition]::new($elemento::ControlTypeProperty, [Windows.Automation.ControlType]::CheckBox)
$porControle = [Windows.Automation.AndCondition]::new($nomeControle, $tipoControle)
for ($i = 0; $i -lt 100; $i++) {
    $janela = $raiz.FindFirst($escopo::Children, $porProcesso)
    if ($janela) {
        $botao = $janela.FindFirst($escopo::Descendants, $porBotao)
        if ($botao) {
            if ($Controle) {
                $checkbox = $janela.FindFirst($escopo::Descendants, $porControle)
                if (!$checkbox) { throw 'O agente nao apresentou a permissao de mouse e teclado.' }
                $alternar = $checkbox.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
                if ($alternar.Current.ToggleState -ne [Windows.Automation.ToggleState]::On) { $alternar.Toggle() }
            }
            $botao.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
            exit 0
        }
    }
    Start-Sleep -Milliseconds 200
}
throw 'O agente baixado nao apresentou o pedido de autorizacao.'
