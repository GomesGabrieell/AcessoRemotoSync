[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuracao = 'Release',
    [string]$DotNet = $(if ($env:DOTNET_EXE) { $env:DOTNET_EXE } else { 'dotnet' })
)

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot

if (!(Get-Command $DotNet -ErrorAction SilentlyContinue)) {
    throw 'SDK .NET 10 não encontrado. Informe o caminho do dotnet.exe em -DotNet ou DOTNET_EXE.'
}

& $DotNet build (Join-Path $raiz 'SyncDesk.slnx') -c $Configuracao
if ($LASTEXITCODE -ne 0) { throw "A compilação falhou (código $LASTEXITCODE)." }
