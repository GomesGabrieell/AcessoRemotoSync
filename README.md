# SyncDesk

Suporte assistido no Windows. A sinalização roda em ASP.NET Core 10 sobre HTTPS/WSS. O aplicativo WPF executa um painel empacotado no WebView2. WebRTC transmite a tela, os comandos de mouse e teclado e as transferências de arquivos em canais de dados separados.

## Estado atual

O SyncDesk 0.5.0 está pronto para **teste local e implantação piloto**. O técnico usa `/painel` para gerar um link. O cliente abre o convite, clica em **Continuar no navegador**, confere o nome do técnico e escolhe a tela ou janela para compartilhar. Ambos podem usar o chat e encerrar o atendimento. O cliente precisa de um navegador de computador compatível com compartilhamento de tela. As funções de controle do Windows e de transferência de arquivos pelo Explorador dependem do cliente portátil.

O fluxo por convite foi validado no mesmo computador com o executável real e dois monitores físicos. A validação em duas redes independentes com TURN, controle real de mouse e teclado e transferência pelo Explorador ainda faz parte do aceite de implantação. As medições locais de vídeo e o roteiro de aceite estão nas seções abaixo.

### Começar a partir do repositório

No Windows x64, instale o SDK .NET 10 e tenha PowerShell 7 e Microsoft Edge WebView2 Runtime disponíveis. Na raiz do projeto, gere os artefatos locais:

```powershell
.\scripts\compilar.ps1
.\scripts\publicar.ps1 -Destino .\artifacts\teste-web-local
dotnet publish .\src\SyncDesk.Sinalizacao -c Release -o .\artifacts\teste-web-local\servidor
```

Abra **Testar SyncDesk Web local.cmd**. Use o usuário `TecnicoLocal` e a credencial temporária mostrada no terminal. No painel, clique em **Novo link**, copie o endereço e abra-o em outra aba. Clique em **Continuar no navegador** e escolha uma janela de teste para compartilhar. O teste local usa a porta `7446` em loopback.

O Git contém o código-fonte, os scripts e os testes. A pasta `artifacts/` é gerada localmente e o executável precisa ser publicado para o endereço HTTPS de cada implantação.

### O que falta para colocar o piloto na internet

- [ ] Provisionar uma instância com ASP.NET Core Runtime 10 ou publicar o servidor como self-contained para a arquitetura do host. Configurar um subdomínio, certificado HTTPS válido e execução automática do serviço no servidor.
- [ ] Configurar as credenciais dos técnicos e os caminhos persistentes dos códigos e do histórico. Definir permissões dos arquivos, backups e um procedimento de restauração. Nesta versão, reiniciar a instância encerra as sessões abertas.
- [ ] Configurar STUN/TURN, o segredo compartilhado do coturn, o firewall e a faixa de portas UDP do relay. Testar o acesso direto e repetir com `SyncDesk__ForcarTurn=true`.
- [ ] Gerar o `SyncDesk.exe` com `-Servidor` apontando para a origem HTTPS pública e configurar `SyncDesk__ExecutavelCliente` no servidor. Para distribuição ampla, assinar o executável com um certificado de assinatura de código.
- [ ] Executar o [teste de aceitação](#teste-de-aceitação-em-dois-computadores-windows) em dois computadores e redes independentes. Conferir vídeo, controle autorizado, arquivos nos dois sentidos, revogação e recuperação após perda de internet.

Os parâmetros detalhados estão em [Implantação Web](#implantação-web) e [STUN e TURN](#stun-e-turn).

Na Oracle Cloud Ampere A1, use Ubuntu ARM64 e o runtime ASP.NET Core 10 para ARM64. O servidor e o painel rodam na VPS. A publicação do portátil `SyncDesk.exe` é feita separadamente no Windows x64.

### Piloto gratuito com Cloudflare Quick Tunnel

No Windows com PowerShell 7, SDK .NET 10 e `cloudflared` no PATH, execute:

```powershell
pwsh -File .\scripts\piloto-cloudflare.ps1
```

O script abre um endereço temporário `trycloudflare.com`, gera o portátil apontando para esse endereço e inicia o servidor HTTPS em loopback. Os dados de login ficam em `%LOCALAPPDATA%\SyncDesk\piloto-cloudflare\ACESSO.txt`. Entre no painel público e use **Novo link** para convidar um cliente. O computador que iniciou o piloto precisa permanecer ligado e conectado à internet.

Para finalizar, execute `pwsh -File .\scripts\piloto-cloudflare.ps1 -Parar`. A próxima execução gera outro endereço e outro portátil.

O túnel encaminha HTTPS e WebSocket. A mídia usa conexão direta WebRTC com STUN neste piloto. Para atender redes que exigem relay, configure um servidor TURN separado. Para uma URL fixa e operação contínua, use um Cloudflare Tunnel com domínio próprio e um host permanente, ou siga a [implantação em VPS](#publicar-para-dois-computadores-em-redes-diferentes). O servidor ASP.NET Core roda no host; o GitHub armazena o código-fonte. [Quick Tunnels](https://developers.cloudflare.com/tunnel/get-started/quick-tunnels/) · [WebSocket no Cloudflare Tunnel](https://developers.cloudflare.com/cloudflare-one/faq/cloudflare-tunnels-faq/)

## Transmissão na versão 0.5

A captura reaproveita o monitor autorizado e usa Desktop Duplication (DXGI) com Direct3D 11 quando disponível. A cópia para memória compartilhada do WebView2 alimenta um `VideoFrame` com pixels BGRX. Quando o runtime aceita um gerador de trilhas, o quadro entra direto na trilha WebRTC. O WebRTC faz a codificação de vídeo e prefere H.264 quando anunciado pelo runtime, com os demais codecs disponíveis para negociação. A captura GDI continua disponível com pixels binários para saídas incompatíveis, giradas ou que precisem de redução antes da captura. JPEG é a alternativa para runtimes que não suportam o caminho binário.

O aplicativo mantém um buffer por monitor selecionado e um pedido em voo. O DXGI evita copiar o desktop em quadros sem mudança visual e mantém uma atualização periódica. O vídeo preserva a proporção e limita a apresentação ao tamanho recebido quando cabe na janela. O menu de qualidade aplica os cinco perfis durante a sessão. A adaptação usa bitrate estimado, RTT, perdas, desempenho do encoder e FPS efetivamente exibido, além de uma estimativa de movimento. Os modos manuais têm tetos diferentes e continuam sujeitos ao congestionamento.

Em **Ações → Estatísticas da conexão** estão as métricas disponíveis durante a sessão. RTT é o tempo de ida e volta de rede, e os custos de captura e codificação são medidos à parte. O percentual de CPU considera somente o processo do agente e sua fração da capacidade lógica total. O uso percentual da GPU fica indisponível no painel. Uma interrupção temporária tem até 2,5 segundos para a conexão se recuperar, enquanto a autorização continua válida. Se o transporte falhar ou a autorização expirar, o atendimento é encerrado.

Os componentes principais são `CapturaPixels.cs` e `AtendimentoWindow.Captura.cs` no Windows, e `captura.js`, `qualidade.js`, `webrtc.js` e `metricas.js` no painel. O teste de medição usa a cena de texto e movimento de `scripts/estimulo-video.ps1` e o teste `tests/painel-web.test.mjs` com `SYNC_DESK_WEB_NATIVO` e `SYNC_DESK_MEDIR_VIDEO=1`.

### Medição comparativa

Uma janela de 6,5 segundos da mesma cena sintética em movimento, com o agente Windows e o navegador no mesmo computador, em loopback e com qualidade automática. A resolução é a registrada no fim da janela.

| Métrica WebRTC | 0.4.0 | 0.5.0 |
| --- | ---: | ---: |
| FPS decodificados | 3,7 | 30,1 |
| Resolução | 1920 × 1080 | 1920 × 1080 |
| Bitrate recebido | 1.178 kb/s | 2.650 kb/s |
| Codec | VP8 | H.264 |
| RTT da conexão ICE | 1 ms | 1 ms |
| Descartes / congelamentos | 0 / 0 | 0 / 0 |

O teste verificou DXGI, troca real entre dois monitores, mudança de perfil com a sessão ativa e encerramento. Um segundo teste verificou que o vídeo continuou avançando durante 89 segundos após a troca de monitor. A suíte inclui os cenários de perda, banda baixa, alta latência e pressão de CPU no algoritmo de adaptação. A meta de 60 FPS depende do hardware, da fonte e da rede. Para o aceite, repita com vídeo 1080p, navegação e um sistema administrativo em outra rede. Use os modos Automática, Máxima e Economia durante o teste, registre as estatísticas e repita em um computador de menor capacidade e durante uma sessão longa.

Uma amostra instantânea da versão 0.5 durante a cena informou 22,8 ms de captura, 30,8 ms de codificação e 2,5% de CPU do processo do agente. As janelas dessa telemetria são independentes da média de 6,5 segundos da tabela. CPU total do WebView2, GPU e latência de ponta a ponta ficaram fora da medição comparativa.

Referências: [Desktop Duplication API](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api), [memória compartilhada no WebView2](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2environment.createsharedbuffer?view=webview2-dotnet-1.0.4191.47) e [trilhas inseríveis de mídia](https://developer.chrome.com/docs/capabilities/web-apis/mediastreamtrack-insertable-media-processing).

### Licenças do caminho de captura

Vortice.Windows 3.8.3 e Vortice.Mathematics 2.1.0 © Amer Koleci and Contributors. SharpGen.Runtime e SharpGen.Runtime.COM 2.4.2-beta © 2010–2017 Alexandre Mutel, 2017–2023 Jeremy Koritzinsky, 2023–2024 Amer Koleci.

```text
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Arquitetura e auditoria da versão 0.2.0

A auditoria encontrou três pontos de acoplamento. As transições de autorização ficavam junto da sinalização, o aplicativo WPF continha a implementação de entrada e transferência, e o script do painel concentrava apresentação, métricas e orquestração de sessão. A modernização preserva os executáveis, a publicação e os fluxos de consentimento e cópia existentes.

| Componente | Responsabilidade |
| --- | --- |
| `SyncDesk.Domain` | Regras de validade do código, solicitação, autorização, revogação, conexão e inatividade. Usa .NET e um relógio injetado, sem dependência de Windows ou WebRTC. |
| `SyncDesk.Infrastructure` | Entrada Win32, destino seguro, protocolo e motor de arquivos, diagnóstico local. |
| `SyncDesk.App` | Aplicativo portátil WPF, composição WebView2, seleção de captura, integração do clipboard com a janela do Explorador e preferências do técnico. |
| `SyncDesk.Sinalizacao` | Aplicação ASP.NET Core, credenciais e limites, códigos, sessões, WSS e validação de SDP/ICE por papel. |
| Painel WebView2 | `syncdesk.js` orquestra a sessão, `painel.js` cuida da apresentação, `webrtc.js` estabelece mídia e canais, e `controle.js`, `qualidade.js` e `metricas.js` cuidam de entrada, adaptação e leituras. |

As regras de autorização ficaram no domínio, os casos de uso de sinalização no serviço `Sessoes` e os contratos de arquivos junto ao motor que os utiliza. Projetos extras para aplicação e contratos compartilhados podem ser extraídos quando houver outro consumidor que justifique a separação.

## Usar o SyncDesk portátil

1. Salve `SyncDesk.exe` em uma pasta local e dê dois cliques para abrir o modo **Cliente**. O SyncDesk funciona com a conta de usuário comum. O código aparece quando o aplicativo se conecta ao servidor configurado.
2. O técnico abre `SyncDesk.exe --tecnico`, informa sua credencial e o código do cliente. O cliente aprova a sessão e escolhe a tela a compartilhar.
3. Feche a janela ou encerre o atendimento pelo painel para desconectar. Se a rede cair, clique em **Reconectar ao servidor** quando o botão estiver disponível. Uma nova conexão do cliente gera um novo código e exige novo aceite.

O executável publicado em `artifacts/publicacao` nesta entrega aponta para `https://localhost:7443`. Para distribuir entre computadores, gere o executável com a origem HTTPS do servidor de produção usando `-Servidor`.

O arquivo x64 inclui o .NET 10 e o carregador do WebView2. Usa o **WebView2 Runtime** já disponível no Windows ou uma cópia **Fixed Version x64** fornecida ao lado do executável em `WebView2Runtime\msedgewebview2.exe`, com os demais arquivos do runtime na mesma pasta. Esse pacote adicional é opcional. Em Windows 10, a versão fixa pode exigir a preparação das permissões da pasta do runtime conforme a documentação da Microsoft. [Distribuição do WebView2](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)

O SyncDesk não instala serviços, não se adiciona à inicialização do Windows e não grava credenciais. O painel usa um perfil WebView2 privado em uma pasta temporária, removida no encerramento normal. O carregador do .NET pode manter o cache das bibliotecas extraídas em `%TEMP%\.net\SyncDesk`. Os arquivos concluídos ficam na pasta que o destinatário escolheu.

No modo Técnico, **Configurações** reúne tema e histórico. O histórico fica desativado por padrão. Quando ativado, guarda localmente data e duração dos até 20 atendimentos mais recentes, sem nome de máquina, código ou credencial. Desative a opção para apagar o histórico registrado. Durante a sessão, use **Tamanho original** ou **Ajustar à janela**, **Tela cheia**, **Arquivos** e **Encerrar** na barra do vídeo.

### Métricas e diagnóstico

Abra **Métricas da sessão** para consultar RTT do par ICE, FPS recebido, bitrate de entrada e memória privada do processo WPF. A leitura de memória não soma os processos WebView2. As medidas são amostradas enquanto o painel está aberto e os valores sem amostra aparecem como um traço. O RTT mede ida e volta da conexão, sem incluir o tempo até a ação aparecer na tela.

O executável registra eventos estruturados em `%LOCALAPPDATA%\SyncDesk\diagnostico\eventos.ndjson`, com uma rotação de aproximadamente 256 KiB e uma cópia anterior. O servidor escreve JSON na saída padrão. Eventos de solicitação, aceite, recusa, início, encerramento e falhas têm identificador aleatório de correlação quando disponível. O diagnóstico registra tipos de evento, horários e correlação. Tela, teclas, conteúdo e nomes de arquivos, credenciais e código de atendimento ficam fora dos registros.

### SmartScreen e assinatura

O Microsoft Defender SmartScreen pode apresentar um alerta ao abrir um executável baixado sem assinatura ou sem reputação suficiente. O Controle Inteligente de Aplicativos e políticas corporativas também podem bloquear a execução. Confirme a origem do arquivo, consulte sua assinatura nas propriedades do Windows e compare o SHA-256 com o valor fornecido por quem publicou. [Reputação do Microsoft Defender SmartScreen](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation)

```powershell
Get-AuthenticodeSignature .\SyncDesk.exe
Get-FileHash .\SyncDesk.exe -Algorithm SHA256
```

## Testar com o servidor local

Para testar **sem certificado e no mesmo computador**, abra `Testar SyncDesk local.cmd` na pasta do projeto. O atalho inicia o servidor em `127.0.0.1:7445` e abre as janelas Cliente e Técnico. No Técnico, informe apenas o código do Cliente e clique em **Conectar**. Aceite a solicitação na janela Cliente. Feche as duas janelas para encerrar o teste.

O teste local usa `artifacts/teste-local-telas`. O argumento `--teste-local` mantém o servidor restrito a `127.0.0.1`. A distribuição entre computadores exige HTTPS. No mesmo computador, deixe **Permitir mouse e teclado** desmarcado para evitar controlar as próprias janelas de teste.

```powershell
.\scripts\publicar.ps1 -Destino .\artifacts\teste-local-telas
dotnet publish .\src\SyncDesk.Sinalizacao -c Release -o .\artifacts\teste-local-telas\servidor
```

Execute `.\scripts\teste-local.ps1 -Verificar` em uma sessão interativa do Windows para verificar a abertura dos dois modos e a captura local. O teste autoriza a própria sessão de visualização em loopback, sem habilitar mouse ou teclado, alterna entre duas telas quando disponíveis e fecha as janelas no fim.

### Teste local com HTTPS

Requisitos de desenvolvimento: SDK .NET 10, PowerShell 7 e Microsoft Edge WebView2 Runtime para o aplicativo Windows.

```powershell
.\scripts\compilar.ps1
dotnet dev-certs https --trust
$env:SyncDesk__Tecnicos__Suporte01 = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
dotnet run --project .\src\SyncDesk.Sinalizacao --no-launch-profile -- --urls https://localhost:7443
```

O valor gerado em `SyncDesk__Tecnicos__Suporte01` é a credencial do técnico. Guarde-o em um gerenciador de senhas e use-o no formulário Técnico. O servidor mostra `Suporte01` ao cliente.

No Windows, execute o SyncDesk publicado uma vez para receber suporte e outra com `--tecnico` para prestar suporte. Passe o código, clique em **Conectar** e use **Autorizar acesso** no cliente. Marque **Permitir mouse e teclado** antes de autorizar para habilitar o controle. No técnico, clique no vídeo para usar o teclado. **Ctrl+Alt+Esc** devolve o foco ao SyncDesk. O navegador continua usando o seletor de compartilhamento para visualização.

## Captura, controle e permissões do Windows

- O aplicativo Windows começa pelo monitor principal após o aceite. Com vários monitores autorizados, use **Tela 1**, **Tela 2** etc. O cliente pode autorizar telas conectadas depois do início com **Autorizar novas telas**. Apenas a tela selecionada é transmitida.
- O painel calcula as posições sobre a imagem exibida. O Windows usa as coordenadas físicas do monitor selecionado, inclusive em posições negativas, com DPI por monitor. A troca solta teclas e botões pressionados. Para digitar, dê foco a uma janela da tela selecionada.
- A captura nativa compartilha monitores inteiros e aceita até 3840 pixels no maior lado, limitada também a 8,3 milhões de pixels. A alternativa JPEG fica limitada a 1920 pixels no maior lado. A resolução e a taxa de quadros enviadas dependem do perfil, da capacidade de processamento e da conexão.
- A barra do técnico tem 32 pixels CSS. O menu **Ações** contém **Tamanho original** e métricas. O chat funciona durante a sessão e é apagado no encerramento.
- O Windows restringe `SendInput` via UIPI. Um SyncDesk de usuário comum pode enviar entrada a aplicativos no mesmo nível de integridade ou inferior. Prompt de UAC no desktop seguro, tela de bloqueio, tela de entrada, **Ctrl+Alt+Del** e áreas de trabalho protegidas ficam fora do atendimento. Conteúdo protegido pode aparecer em preto ou ausente. O aplicativo roda sem elevação e desativa o controle se deixa de detectar o desktop interativo.
- O painel empacotado é o único autorizado a acionar a ponte de entrada nativa. A janela do cliente sinaliza **Atendimento ativo** e oferece **Encerrar atendimento**. A captura GDI usa o indicador do SyncDesk, também presente no título da janela. Encerrar o atendimento ou fechar o aplicativo revoga o controle, solta teclas e botões e interrompe a captura. A perda da autorização também encerra a sessão.

Referências de permissões [SendInput e UIPI](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput) e [UAC e desktop seguro](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/user-account-control/how-it-works).

## Copiar e colar arquivos

1. Use o executável Windows nos dois computadores e aceite o atendimento. A autorização vale por toda a sessão.
2. No Explorador de origem, selecione até 256 arquivos e pressione **Ctrl+C**.
3. No Explorador de destino, abra uma pasta local, dê foco à lista de arquivos e pressione **Ctrl+V**. Se você estiver operando esse Explorador pelo vídeo remoto, clique no vídeo primeiro para ativar o teclado remoto. Da mesma forma, para trazer arquivos do cliente, copie no Explorador remoto e cole no Explorador do seu computador.
4. Acompanhe o progresso e a velocidade no painel do SyncDesk. Use **Cancelar envio** ou **Cancelar recebimento** se precisar parar.

O SyncDesk observa o formato de arquivos do Windows durante a sessão e mantém a oferta vinculada à seleção copiada no Explorador. Texto continua seguindo o comportamento normal do Windows. A cópia aceita arquivos de unidades locais. Pastas inteiras, destinos virtuais, compartilhamentos de rede e pastas com redirecionamento por junções ou atalhos não são aceitos. Use uma janela do Explorador com uma aba se o destino estiver ambíguo. A transferência não usa SMB.

Cada arquivo entra em um temporário na pasta escolhida e só é publicado depois da validação do tamanho e do SHA-256. Em caso de colisão, o SyncDesk mantém o arquivo existente e adiciona um sufixo como `(2)` ao recebido. Ao cancelar ou perder a conexão, o arquivo em andamento é descartado. Arquivos já concluídos permanecem na pasta. Para repetir uma transferência interrompida, use **Ctrl+C** e **Ctrl+V** novamente. Uma interrupção forçada do processo ou do sistema pode deixar um arquivo `.syncdesk-*.part` incompleto, que pode ser excluído depois que o SyncDesk for fechado.

## Publicar para dois computadores em redes diferentes

1. Publique `src/SyncDesk.Sinalizacao` em um host acessível aos dois computadores. Configure Kestrel para HTTPS com certificado público válido, incluindo `ASPNETCORE_URLS=https://0.0.0.0:443` e o certificado e sua senha em `ASPNETCORE_Kestrel__Certificates__Default__Path` e `ASPNETCORE_Kestrel__Certificates__Default__Password`. Use o diretório publicado como diretório de trabalho. O serviço rejeita HTTP e usa o IP da conexão direta para limitar novas conexões.
2. Defina uma chave aleatória e exclusiva por técnico em `SyncDesk__Tecnicos__<Identificador>`. Guarde as chaves e a senha do certificado no armazenamento de segredos do host.
3. Configure STUN e um servidor TURN público. Os clientes descobrem rotas com ICE e usam TURN quando a conexão direta não funciona. As portas públicas pertencem à infraestrutura do serviço. Os roteadores dos clientes não precisam de encaminhamento manual.
4. Publique o aplicativo apontando para o domínio HTTPS de produção. O cliente que recebe o executável abre no modo Cliente, sem precisar digitar o endereço.

```powershell
dotnet publish .\src\SyncDesk.Sinalizacao -c Release -o .\artifacts\sinalizacao
.\scripts\publicar.ps1 -Servidor 'https://suporte.exemplo.com'
```

Execute `publicar.ps1` sem `-Servidor` para gerar o pacote local de teste. O script cria `SyncDesk.exe` e uma cópia deste README em `artifacts/publicacao`. Ele imprime o SHA-256 e assina o executável quando encontra um certificado de assinatura de código válido com chave privada. Se houver vários, informe `-Thumbprint`. Com certificado disponível, o SignTool do Windows SDK é exigido e pode ser indicado em `-SignTool`. É possível trocar o servidor de carimbo RFC 3161 com `-CarimboDeTempo`. [Assinatura e verificação com SignTool](https://learn.microsoft.com/en-us/windows/win32/seccrypto/using-signtool-to-sign-a-file)

### STUN e TURN

Exemplo de variáveis do serviço de sinalização:

```text
SyncDesk__Stun__0=stun:turn.exemplo.com:3478
SyncDesk__Turn__0=turn:turn.exemplo.com:3478?transport=udp
SyncDesk__Turn__1=turns:turn.exemplo.com:5349?transport=tcp
SyncDesk__SegredoTurn=<mesmo segredo de static-auth-secret do coturn>
SyncDesk__ForcarTurn=false
```

No coturn, configure `realm`, `use-auth-secret`, `static-auth-secret`, `cert`, `pkey` e o intervalo UDP do relay em `min-port` e `max-port`. Restrinja o relay para impedir conexão com redes privadas da infraestrutura. Exponha as portas configuradas no firewall do servidor. Se o coturn estiver atrás de NAT, configure o mapeamento em `external-ip`. Para validar o relay sem uma conexão direta como alternativa, use `SyncDesk__ForcarTurn=true` e teste entre redes distintas. [Configuração oficial do coturn](https://github.com/coturn/coturn/blob/master/examples/etc/turnserver.conf)

O servidor emite credenciais TURN temporárias e individuais após o aceite. SDP, candidatos ICE e a identidade do técnico passam por WSS. WebRTC protege a mídia e o canal de dados. Os códigos ficam disponíveis por cinco minutos, são consumidos na primeira solicitação autenticada e não são reemitidos dentro da mesma instância. O cliente tem até 45 segundos para aceitar, respeitada a validade do código. O servidor limita consultas por técnico e globalmente, além de limitar a abertura de sockets por IP. A sessão termina ao fechar um painel, ao encerrar o atendimento ou ao vencer o limite de duas horas.

Atualize o servidor e os dois executáveis juntos. Durante a sessão, os dois lados renovam a autorização pela sinalização. A janela de autorização dura quatro segundos contados do envio da última renovação confirmada. Quando a rede ou o WebRTC informa a perda da conexão, o painel corta mídia e entrada de imediato. Se a falha for silenciosa, os comandos são bloqueados ao expirar a autorização. Uma tentativa nova exige código e aprovação novos.

O servidor encerra uma sessão após 15 minutos sem entrada remota ou blocos de arquivo. Configure `SyncDesk__InatividadeMaximaMinutos` entre 1 e 120 para ajustar esse prazo. Pulsos da conexão mantêm a autorização, e atividade de suporte renova o prazo de inatividade.

Esta versão usa uma instância de sinalização. Reiniciar o servidor encerra os atendimentos em memória. Configure `SyncDesk__ArquivoCodigos` em um volume persistente e exclusivo do processo para preservar o registro de códigos emitidos entre reinícios. O serviço para de emitir novos códigos ao atingir 900 mil emissões no registro. O compartilhamento de sessões entre réplicas exige outra implementação de armazenamento.

## Testes

Com Node.js 24, o SDK .NET 10 e o certificado de desenvolvimento disponível, compile a solução em Release e execute:

```powershell
.\scripts\testar-portatil.ps1
npm.cmd --prefix tests ci
$env:CHROMIUM_EXE = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
npm.cmd --prefix tests test
dotnet run --project .\tests\SyncDesk.Arquivos.Testes -c Release
dotnet run --project .\tests\SyncDesk.Domain.Testes -c Release
```

Também é possível instalar o Chromium com `npx --prefix tests playwright-core install chromium` e deixar `CHROMIUM_EXE` vazio. Para um SDK fora do PATH, aponte `DOTNET_EXE` para o `dotnet.exe`.

Os testes sobem Kestrel local com HTTPS e abrem dois painéis. Validam aceite, vídeo e eventos de entrada no canal WebRTC, desconexão, recusa, credencial inválida, expiração e limite de tentativas. Também verificam o mapeamento de coordenadas e os perfis de qualidade. O navegador de teste usa uma fonte de vídeo determinística e registra os eventos no lugar da ponte nativa para evitar injetar entrada no desktop de teste. As variáveis de TURN do ambiente são repassadas ao servidor de teste para executar a suíte com relay forçado quando houver um TURN disponível.

Os testes de arquivos exercitam o mesmo protocolo do aplicativo com gravações reais em pastas temporárias do Windows. Cobrem cópia nos dois sentidos, lote, arquivo vazio, colisão de nomes, cancelamento, desconexão, hash incorreto e tentativa de escapar do destino. A suíte WebRTC verifica dados nos dois sentidos pelo canal de arquivos separado.

O script `testar-portatil.ps1` abre os dois modos do executável em uma sessão interativa do Windows, verifica a arquitetura x64 e o token de cada processo, fecha as janelas e confirma a remoção dos perfis. A suíte de sinalização verifica que a reconexão abre outro WebSocket e fornece um código novo.

Os testes do domínio avançam um relógio controlado para verificar as transições sem aceite, a validade do código, a dependência dos dois pares e a expiração por inatividade.

## Teste de aceitação em dois computadores Windows

Publique o servidor em HTTPS e configure STUN/TURN. Coloque o cliente com **um monitor** em uma rede residencial e o técnico em outra rede, por exemplo um hotspot móvel. Abra o painel web como técnico e execute o portátil como usuário comum no cliente. Prepare arquivos de teste sem dados pessoais, com ao menos dois arquivos de nomes distintos. No painel web, respeite os limites de 32 MiB por arquivo e 64 MiB por lote. Registre as versões do Windows, navegador, WebView2 e SyncDesk, a resolução e o DPI.

| Cenário | Ação e critério de aprovação |
| --- | --- |
| 1. Convite | Entre em `/painel`, clique em **Novo link**, copie o endereço e abra-o no computador do cliente. Para este roteiro com controle e arquivos, acrescente `?modo=windows` ao link antes de baixar o portátil e confira se o convite é associado ao executá-lo. |
| 2. Técnico em outro PC | No painel, confira a espera pela autorização do cliente. Para testar o modo desktop legado, abra o cliente sem convite e informe o código no `SyncDesk.exe --tecnico`. |
| 3. Aceite | Confirme a identidade do técnico. Marque **Permitir mouse e teclado** para testar controle e clique em **Autorizar acesso**. Confira que a tela aparece após o aceite. Repita sem marcar para verificar somente visualização. |
| 4. Tela e entrada | Comece com um monitor e verifique a ausência de abas. Adicione um monitor com resolução ou DPI diferente, autorize-o e alterne pelas abas. Clique, arraste, role e digite em cada tela. Redimensione a janela, use tela cheia e desconecte o monitor selecionado para conferir a atualização. |
| 5. Técnico para cliente | Selecione arquivos em **Arquivos** no painel web. Cole com **Ctrl+V** na pasta do Explorador remoto e compare o SHA-256 de origem e destino. |
| 6. Cliente para técnico | Copie no Explorador remoto. No painel, clique em **Receber arquivos do cliente**, baixe os arquivos e compare o SHA-256. Teste cancelamento e colisão de nomes no destino. |
| 7. Encerramento pelo cliente | No cliente, encerre o atendimento. Confirme no técnico que o vídeo e o controle terminam. Tente digitar e clicar após o encerramento e confirme que o cliente não recebe entrada. |
| 8. Novo convite | Após encerrar pelo técnico ou cliente, confira se o link anterior é rejeitado e se o novo exige aprovação. |
| 9. Queda da internet | Com a sessão ativa e uma cópia em andamento, interrompa a internet de um lado. Confira o encerramento, o descarte do parcial e a ausência de comandos após a perda da autorização. Restaure a rede e inicie um novo atendimento com novo código e aprovação. |
| 10. Redes diferentes | Repita nas duas redes com o TURN configurado. Rode também com `SyncDesk__ForcarTurn=true` para validar o relay. Confira no coturn que a sessão usou o relay. |

Para medir espera, registre pelo Gerenciador de Tarefas o SyncDesk e seus processos WebView2 durante 30 segundos com o código exibido. Para avaliar adaptação e latência, repita os cenários 4 e 6 com rede limitada e anote a resposta antes e durante a transferência. Registre o tempo entre a queda de rede e a perda de acesso no cenário 9.

A suíte automatizada usa dois painéis no mesmo host, uma fonte de vídeo determinística e uma ponte Windows instrumentada. Exercita a sinalização real, a mídia WebRTC, os canais separados, a revogação, a renovação do código e a perda de renovação durante a sessão. O controle Win32, os atalhos do Explorador e o TURN entre redes diferentes precisam do teste acima para fechar o aceite.
## Painel Web do SyncDesk

O técnico abre `/painel` no navegador. O cliente continua usando o aplicativo portátil Windows. O painel usa dados do servidor e apresenta o visualizador em uma área dedicada quando o cliente autoriza e a sessão WebRTC fica disponível.

### Teste rápido no mesmo computador

Abra **Testar SyncDesk Web local.cmd**. No terminal, copie a credencial temporária e entre no painel com o usuário `TecnicoLocal`. Clique em **Novo link**, copie o link, abra-o no navegador e baixe o cliente. Abra o executável com o nome baixado. Na janela do SyncDesk, confira o técnico e clique em **Autorizar acesso**. Para este teste, deixe o controle do mouse e teclado desmarcado. Termine o atendimento e confira **Últimos atendimentos**. Pressione Enter no terminal para parar o servidor.

O teste web local usa HTTP/WS exclusivamente em `127.0.0.1:7446`. O navegador e o WebView2 devem estar instalados. Para recompilar e recriar o pacote:

```powershell
.\scripts\compilar.ps1
.\scripts\publicar.ps1 -Destino .\artifacts\teste-web-local
dotnet publish .\src\SyncDesk.Sinalizacao -c Release -o .\artifacts\teste-web-local\servidor
```

### Implantação Web

1. Publique `SyncDesk.Sinalizacao` como descrito na seção de produção deste README. Abra `https://seu-servidor/painel`. Configure `SyncDesk__Tecnicos__NomeDoTecnico` com uma credencial forte, exclusiva e com pelo menos 32 caracteres. O usuário do painel é o nome configurado.
2. Configure um certificado HTTPS válido e STUN/TURN conforme a rede. Para oferecer também o cliente portátil Windows, gere-o com `scripts/publicar.ps1 -Servidor https://seu-servidor` e aponte `SyncDesk__ExecutavelCliente` para o caminho absoluto desse executável, fora de `wwwroot`.
3. Defina `SyncDesk__ArquivoHistoricoWeb` como caminho persistente, de acesso exclusivo ao processo do servidor. O histórico conserva os últimos 1.000 registros no arquivo JSON. O console recebe eventos de criação, vínculo, consentimento e encerramento, com identificadores de auditoria e sem a chave do convite. Os indicadores do dia usam a data local do navegador.
4. Use uma instância do servidor por implantação neste MVP. Convites pendentes, sessões ativas e logins têm estado em memória e são encerrados no reinício. Atendimentos finalizados permanecem no histórico. Para operação externa, publique o aplicativo com assinatura de código quando houver certificado.

### Convites, consentimento e arquivos

- O link vale por 10 minutos por padrão e é consumido quando o cliente entra no atendimento. Configure `SyncDesk__ValidadeConviteMinutos` no servidor para outro valor entre 1 e 60 minutos. Depois de recusar, encerrar ou perder a conexão, peça ao técnico um novo link.
- Para oferecer controle do Windows e arquivos, o técnico pode acrescentar `?modo=windows` a um convite novo. O identificador vai no nome do executável baixado, preservando seu conteúdo e sua assinatura. Se o nome mudar, copie o **Código de convite** da página e use **Já tenho um código de convite** no aplicativo. A versão portátil deve apontar para o mesmo servidor que gerou o link.
- O navegador autentica o técnico com cookie HttpOnly, SameSite e Secure em produção. O servidor limita tentativas e valida o proprietário do atendimento. O download não autoriza acesso. A captura, os arquivos e os comandos dependem do aceite e da vigência da sessão. **Sair** revoga as conexões associadas ao login.
- **Arquivos** no técnico web permite escolher arquivos locais ou colá-los no painel quando o navegador os disponibilizar. No Windows remoto, cole na pasta de destino com **Ctrl+V**. Para a outra direção, copie no Explorador remoto, clique em **Receber arquivos do cliente** e baixe os arquivos conferidos. O navegador valida tamanho e SHA-256, mantém os arquivos recebidos em memória até o download ou fim da sessão e limita o lote a 64 arquivos, 32 MiB por arquivo e 64 MiB no total. Os atalhos nativos bidirecionais entre dois executáveis Windows continuam disponíveis no modo legado.

### Verificação

`npm.cmd --prefix tests test` cobre o painel, a sinalização, convites e transporte. Defina `SYNC_DESK_WEB_NATIVO` com o caminho absoluto do portátil publicado e rode `node --test tests/painel-web.test.mjs` para baixar, executar e autorizar o cliente Windows de verdade no teste em loopback. Execute a partir de uma sessão interativa do Windows, com a porta 7446 livre e Chrome disponível. O teste autoriza a própria sessão controlada, sem habilitar mouse nem teclado.

### Arquivos da migração

| Área | Arquivos principais |
| --- | --- |
| Dashboard e página do cliente | `wwwroot/painel-web.html`, `painel-web.js`, `painel-web.css`, `convite.html` e `convite.js` |
| Autenticação, convites e histórico | `PainelWeb.cs`, `SessoesLoginWeb.cs`, `ConvitesWeb.cs`, `HistoricoWeb.cs` e `Program.cs` |
| Sessão e arquivos no navegador | `Sockets.cs`, `Sessoes.cs`, `wwwroot/syncdesk.js`, `index.html`, `syncdesk.css` e `arquivos-web.js` |
| Aplicativo Windows | `SyncDesk.App/App.xaml.cs` e `Views/AtendimentoWindow.xaml.cs` |
| Teste local e validação | `Testar SyncDesk Web local.cmd`, `scripts/teste-web-local.ps1`, `scripts/testar-aceite-web.ps1`, `tests/painel-web.test.mjs` e `tests/arquivos-web.test.mjs` |
