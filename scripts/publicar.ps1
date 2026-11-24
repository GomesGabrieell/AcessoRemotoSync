[CmdletBinding()]
param(
    [string]$Servidor = 'https://localhost:7443',
    [string]$Destino = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/publicacao'),
    [string]$Thumbprint,
    [string]$SignTool,
    [uri]$CarimboDeTempo = 'http://timestamp.digicert.com',
    [string]$DotNet = $(if ($env:DOTNET_EXE) { $env:DOTNET_EXE } else { 'dotnet' })
)

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot

function Encontrar-Certificado {
    $agora = Get-Date
    $candidatos = @(
        foreach ($local in 'CurrentUser', 'LocalMachine') {
            Get-ChildItem "Cert:\$local\My" -CodeSigningCert | Where-Object {
                $_.HasPrivateKey -and $_.NotBefore -le $agora -and $_.NotAfter -gt $agora
            } | ForEach-Object {
                [pscustomobject]@{ Certificado = $_; Local = $local }
            }
        }
    )
    if ($Thumbprint) {
        $normalizado = $Thumbprint -replace '\s', ''
        $selecionado = $candidatos | Where-Object { $_.Certificado.Thumbprint -eq $normalizado } | Select-Object -First 1
        if (!$selecionado) { throw 'O certificado de assinatura solicitado não foi encontrado ou está indisponível.' }
        return $selecionado
    }
    $unicos = @($candidatos | Group-Object { $_.Certificado.Thumbprint } | ForEach-Object { $_.Group[0] })
    if ($unicos.Count -gt 1) { throw 'Há mais de um certificado de assinatura. Selecione o certificado com -Thumbprint.' }
    return $unicos | Select-Object -First 1
}

function Encontrar-SignTool {
    if ($SignTool) {
        $explicito = Get-Command $SignTool -CommandType Application -ErrorAction SilentlyContinue
        if (!$explicito) { throw 'O SignTool informado não foi encontrado.' }
        return $explicito.Source
    }
    $comando = Get-Command 'signtool.exe' -CommandType Application -ErrorAction SilentlyContinue
    if ($comando) { return $comando.Source }
    $kits = Join-Path ([Environment]::GetEnvironmentVariable('ProgramFiles(x86)')) 'Windows Kits/10/bin'
    if (Test-Path -LiteralPath $kits) {
        $ferramenta = Get-ChildItem -LiteralPath $kits -Directory |
            Where-Object Name -Match '^\d+\.\d+\.\d+\.\d+$' |
            Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64/signtool.exe' } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
        if ($ferramenta) { return $ferramenta }
    }
    throw 'Há um certificado de assinatura, mas o SignTool não foi encontrado. Informe -SignTool ou instale o Windows SDK na máquina de publicação.'
}

if (!$IsWindows) { throw 'Execute a publicação do SyncDesk no Windows com PowerShell 7.' }
if (!(Get-Command $DotNet -ErrorAction SilentlyContinue)) {
    throw 'SDK .NET 10 não encontrado. Informe o caminho do dotnet.exe em -DotNet ou DOTNET_EXE.'
}
$uri = $null
if (![uri]::TryCreate($Servidor, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https' -or
    $uri.AbsolutePath -ne '/' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) {
    throw 'Informe uma origem HTTPS, por exemplo https://suporte.exemplo.com.'
}
$origem = $uri.GetLeftPart([UriPartial]::Authority)
$certificado = Encontrar-Certificado
if ($certificado) {
    $assinador = Encontrar-SignTool
    if (!$CarimboDeTempo.IsAbsoluteUri -or $CarimboDeTempo.Scheme -notin @('http','https')) {
        throw 'Informe um endereço HTTP ou HTTPS para o serviço de carimbo de tempo.'
    }
}

$pasta = [IO.Path]::GetFullPath($Destino)
$null = [IO.Directory]::CreateDirectory($pasta)
$temporaria = [IO.Directory]::CreateTempSubdirectory('SyncDesk-publicar-').FullName
$novo = Join-Path $pasta ('.SyncDesk-' + [guid]::NewGuid().ToString('N') + '.tmp')
try {
    $projeto = Join-Path $raiz 'src/SyncDesk.App/SyncDesk.App.csproj'
    $argumentos = @('publish', $projeto, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=None',
        '-p:PublishReferencesDocumentationFiles=false', "-p:SyncDeskServerUrl=$origem", '-o', $temporaria)
    & $DotNet @argumentos
    if ($LASTEXITCODE -ne 0) { throw "A publicação falhou (código $LASTEXITCODE)." }
    $executavel = Join-Path $temporaria 'SyncDesk.exe'
    if (!(Test-Path -LiteralPath $executavel -PathType Leaf)) { throw 'A publicação não gerou SyncDesk.exe.' }
    $externos = @(Get-ChildItem -LiteralPath $temporaria -File -Recurse | Where-Object {
        $_.FullName -ne $executavel -and $_.Extension -notin @('.xml', '.pdb')
    })
    if ($externos.Count) { throw "A publicação gerou arquivos externos inesperados: $($externos.Name -join ', ')." }

    if ($certificado) {
        $argumentos = @('sign', '/fd', 'SHA256', '/tr', $CarimboDeTempo.AbsoluteUri, '/td', 'SHA256',
            '/s', 'My', '/sha1', $certificado.Certificado.Thumbprint)
        if ($certificado.Local -eq 'LocalMachine') { $argumentos += '/sm' }
        & $assinador @argumentos $executavel
        if ($LASTEXITCODE -ne 0) { throw "A assinatura falhou (código $LASTEXITCODE)." }
        & $assinador verify /pa /tw $executavel
        if ($LASTEXITCODE -ne 0) { throw "A validação da assinatura ou do carimbo de tempo falhou (código $LASTEXITCODE)." }
    }
    else { Write-Warning 'Nenhum certificado de assinatura de código com chave privada foi encontrado. O executável será publicado sem assinatura.' }

    $saida = Join-Path $pasta 'SyncDesk.exe'
    Copy-Item -LiteralPath $executavel -Destination $novo
    [IO.File]::Move($novo, $saida, $true)
    Copy-Item -LiteralPath (Join-Path $raiz 'README.md') -Destination (Join-Path $pasta 'README.md') -Force
    Write-Host "Publicado em $saida"
    Write-Host "Servidor: $origem"
    Write-Host "SHA-256: $((Get-FileHash -LiteralPath $saida -Algorithm SHA256).Hash)"
}
finally {
    if (Test-Path -LiteralPath $novo -PathType Leaf) { Remove-Item -LiteralPath $novo }
    $localTemporario = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    $pastaTemporaria = [IO.Path]::GetFullPath($temporaria)
    if ([IO.Path]::GetDirectoryName($pastaTemporaria) -eq $localTemporario -and
        [IO.Path]::GetFileName($pastaTemporaria).StartsWith('SyncDesk-publicar-')) {
        Remove-Item -LiteralPath $pastaTemporaria -Recurse -Force
    }
}
