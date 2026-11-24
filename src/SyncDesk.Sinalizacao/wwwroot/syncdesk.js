import { Transporte } from "./webrtc.js";
import { ControleDoTecnico } from "./controle.js";
import { prepararPainel } from "./painel.js";
import { CapturaNativa } from "./captura.js";
import { ModosQualidade } from "./qualidade.js";
import { ArquivosWeb } from "./arquivos-web.js";

const el = (id) => document.getElementById(id);
const parametros = new URLSearchParams(location.search);
const conviteWeb = location.pathname === "/sessao" ? parametros.get("id") : null;
const tecnico = Boolean(conviteWeb) || parametros.get("modo") === "tecnico";
const modo = tecnico ? "tecnico" : "cliente";
const nativo = window.syncDeskServidor && window.chrome?.webview;
const conviteNoNavegador = !nativo && !tecnico && parametros.get("modo") === "receber";
const capturaWindows = Boolean(nativo && window.syncDeskCapturaNativa === true);
if (conviteWeb) document.body.dataset.web = "true";
const testeLocal = nativo
  ? /^ws:\/\/127\.0\.0\.1:744[56]$/.test(window.syncDeskServidor)
  : /^http:\/\/127\.0\.0\.1:744[56]$/.test(location.origin);
if (testeLocal) {
  el("rotulo-credencial").hidden = true;
  el("credencial").hidden = true;
  el("credencial").disabled = true;
  el("credencial").required = false;
}
const alternar = el("trocar-modo");
alternar.hidden = Boolean(nativo || conviteWeb || conviteNoNavegador);
alternar.href = tecnico ? "/" : "/?modo=tecnico";
alternar.textContent = tecnico ? "Modo Cliente" : "Modo Técnico";
el(modo).hidden = false;

let socket;
let transporte;
let captura;
let capturaNativa;
let chaveInicial = conviteNoNavegador ? location.hash.slice(1) : window.syncDeskConvite;
if (conviteNoNavegador) {
  history.replaceState(null, "", location.pathname + location.search);
  el("descricao-consentimento").textContent = "O técnico quer visualizar a tela ou janela que você escolher.";
  el("aceitar").textContent = "Escolher tela para compartilhar";
}
let videoConfirmado = false;
let quadroPublicado;
let seletor;
let aguardandoMonitor;
let monitorPronto;
let atualizandoVideo = 0;
let codigo;
let sessao;
let fase = "offline";
let controlePermitido = false;
let canalArquivos = false;
let arquivosRemotos = false;
let arquivosAtivos = false;
let arquivosWeb;
const downloads = [];
let prontoTransporte = false;
const dadosPendentes = new Map();
let vigencia;
let atendimento;
let atividade = false;
let modoQualidade = "automatica";
let telemetriaVideo;
let tempoVideo;
let esperandoRede = false;
let semQuadros = 0;
const DuracaoAutorizacao = 4000;
const contadorTempo = () => performance.now();
const autorizada = () => Boolean(vigencia?.confirmada && contadorTempo() < vigencia.vence);
const ponte = (tipo, dados = {}) => nativo?.postMessage({ tipo, sessao, ...dados });
const controle = tecnico ? new ControleDoTecnico(el("tela"), (evento) => {
  if (!autorizada() || esperandoRede) return;
  if (evento.acao !== "sinal" && evento.acao !== "soltar") atividade = true;
  transporte?.enviarDados({ tipo: "entrada", evento, geracao: monitorPronto });
}) : null;
const painel = prepararPainel(tecnico, nativo, async () => {
  if (!autorizada()) return null;
  ponte("amostrarMemoria");
  const medicao = await transporte?.metricas();
  if (!medicao) return null;
  return { ...medicao, ...telemetriaVideo, modo: ModosQualidade[modoQualidade] +
    (telemetriaVideo?.alvoFps ? " · até " + telemetriaVideo.alvoFps + " FPS" : "") };
});
const diagnostico = (evento) => nativo?.postMessage({ tipo: "diagnostico", evento, sessao: atendimento });
el("linha-controle").hidden = !capturaWindows;
el("usar-convite").hidden = !capturaWindows;
el("permissao-cliente").hidden = conviteNoNavegador;
el("permissao-cliente").textContent = capturaWindows
  ? "O técnico poderá alternar entre as telas atualmente conectadas e transferir arquivos durante o atendimento."
  : "Escolha uma janela ou tela para compartilhar após autorizar.";

function avisoVideo(texto) {
  const aviso = el("diagnostico-video");
  if (aviso.textContent !== texto) aviso.textContent = texto;
  aviso.hidden = !texto;
}

function recuperacaoVideo(interrompido) {
  esperandoRede = interrompido;
  if (!tecnico || fase !== "ativo") return;
  controle.permitir(!interrompido && controlePermitido && (!seletor || monitorPronto !== undefined));
  avisoVideo(interrompido ? "Recuperando conexão…" : "");
}

function escolherQualidade(modo) {
  modoQualidade = modo;
  const menu = el("indicador-qualidade");
  menu.querySelector(".rotulo-qualidade").textContent = ModosQualidade[modo];
  menu.querySelector("summary").title = ModosQualidade[modo];
  for (const botao of menu.querySelectorAll("[data-modo-qualidade]"))
    botao.setAttribute("aria-pressed", botao.dataset.modoQualidade === modo);
  menu.open = false;
  if (fase === "ativo" && autorizada()) transporte?.enviarDados({ tipo: "qualidadeVideo", modo });
}

for (const botao of document.querySelectorAll("[data-modo-qualidade]"))
  botao.onclick = () => escolherQualidade(botao.dataset.modoQualidade);

async function observarVideo() {
  const atual = transporte;
  if (!autorizada() || esperandoRede || !atual) return;
  const medicao = await atual.metricas();
  if (!medicao || atual !== transporte || !autorizada() || esperandoRede) return;
  const { perda, fps, render } = medicao;
  atual.enviarDados({ tipo: "retornoVideo", perda, fps: render ?? fps });
  if (medicao.resolucao && render === 0 && (!seletor || monitorPronto !== undefined)) semQuadros++;
  else semQuadros = 0;
  avisoVideo(semQuadros >= 2 ? "Aguardando atualização da imagem…" : "");
}

function publicarPermissao(permitido, computador) {
  controlePermitido = permitido;
  el("controle-cliente").textContent = permitido
    ? "O técnico pode ver a tela e usar o mouse e o teclado. Seus movimentos e teclas têm prioridade."
    : "O técnico pode ver a tela compartilhada.";
  transporte?.enviarDados({ tipo: "permissao", controle: permitido, computador, capturaNativa: capturaWindows });
}

if (nativo && !tecnico) nativo.addEventListener("message", ({ data }) => {
  if (fase !== "ativo" || !autorizada()) return;
  if (data?.tipo === "permissao")
    publicarPermissao(data.controle === true, data.computador);
  if (data?.tipo === "monitores") {
    capturaNativa?.selecionar(data.geracao);
    el("autorizar-monitores").hidden = !data.telas.some((tela) => !tela.autorizado);
    transporte?.enviarDados({ tipo: "monitores", telas: data.telas.filter((tela) => tela.autorizado)
      .map(({ id, numero }) => [id, numero]), selecionado: data.selecionado, geracao: data.geracao });
  }
  if (data?.tipo === "quadro") void capturaNativa?.quadro(data);
  if (data?.tipo === "capturaEncerrada") encerrar("O Windows interrompeu o compartilhamento de tela.");
});
el("autorizar-monitores").onclick = () => {
  if (fase === "ativo" && autorizada()) ponte("autorizarMonitores");
};

function aplicarMonitores(dados) {
  if (!Array.isArray(dados.telas) || !Number.isSafeInteger(dados.geracao)) return;
  if (aguardandoMonitor || dados.geracao !== seletor?.geracao) {
    atualizandoVideo++;
    monitorPronto = undefined;
    controle.permitir(false);
    el("troca-monitor").hidden = false;
    el("troca-monitor").textContent = dados.selecionado ? "Preparando tela…" : "Aguardando uma tela autorizada…";
  }
  aguardandoMonitor = undefined;
  seletor = dados;
  const abas = el("abas-monitores");
  abas.hidden = dados.telas.length < 2;
  abas.replaceChildren(...dados.telas.map(([id, numero]) => {
    const botao = document.createElement("button");
    botao.type = "button";
    botao.textContent = "Tela " + numero;
    botao.setAttribute("aria-pressed", id === dados.selecionado);
    botao.onclick = () => {
      if (id === seletor?.selecionado || aguardandoMonitor || !autorizada() ||
          !transporte?.enviarDados({ tipo: "selecionarMonitor", id })) return;
      aguardandoMonitor = id;
      atualizandoVideo++;
      monitorPronto = undefined;
      controle.permitir(false);
      el("troca-monitor").textContent = "Alternando tela…";
      el("troca-monitor").hidden = false;
    };
    return botao;
  }));
}

function confirmarQuadro(geracao) {
  if (aguardandoMonitor || !seletor?.selecionado || geracao !== seletor.geracao || monitorPronto === geracao) return;
  const revisao = ++atualizandoVideo;
  const video = el("tela");
  const confirmar = () => {
    if (revisao !== atualizandoVideo || !autorizada()) return;
    monitorPronto = geracao;
    el("troca-monitor").hidden = true;
    const tela = seletor.telas.find(([id]) => id === seletor.selecionado);
    video.setAttribute("aria-label", tela ? "Tela compartilhada pelo cliente · Tela " + tela[1] : "Tela compartilhada pelo cliente");
    controle.permitir(controlePermitido && !esperandoRede);
    videoConfirmado = true;
    if (conviteWeb) enviar("visualizando", { computador: el("computador-remoto").textContent });
  };
  video.requestVideoFrameCallback(() => video.requestVideoFrameCallback(confirmar));
}

const chatBotao = el(tecnico ? "chat-tecnico" : "chat-cliente");
function mostrarChat(aberto) {
  el("chat").hidden = !aberto;
  chatBotao.setAttribute("aria-expanded", aberto);
}
function adicionarMensagem(autor, mensagem) {
  const lista = el("mensagens-chat");
  const item = document.createElement("li");
  item.textContent = autor + " · " + mensagem;
  lista.append(item);
  if (lista.children.length > 60) lista.firstElementChild.remove();
  lista.scrollTop = lista.scrollHeight;
}
chatBotao.onclick = () => mostrarChat(el("chat").hidden);
el("form-chat").onsubmit = (evento) => {
  evento.preventDefault();
  const campo = el("texto-chat");
  const mensagem = campo.value.trim();
  if (fase !== "ativo" || !autorizada() || !mensagem || !transporte?.enviarDados({ tipo: "chat", mensagem })) return;
  adicionarMensagem("Você", mensagem);
  campo.value = "";
};

function atualizarArquivos() {
  if (fase !== "ativo" || !autorizada() || !canalArquivos || !arquivosRemotos || arquivosAtivos) return;
  arquivosAtivos = true;
  el("mostrar-arquivos").disabled = false;
  if (conviteWeb) {
    arquivosWeb = new ArquivosWeb(
      (mensagem) => {
        if (mensagem.tipo === "bloco") atividade = true;
        transporte?.enviarArquivo(mensagem);
      }, () => arquivosAtivos && autorizada(), mostrarTransferencia,
      (disponivel) => { el("solicitar-arquivos").disabled = !disponivel; }, listarDownloads);
  } else ponte("arquivosAtivos");
}

function pararArquivos() {
  if (arquivosAtivos) ponte("arquivosFim");
  arquivosWeb?.fechar();
  arquivosWeb = undefined;
  listarDownloads([]);
  for (const envio of [true, false]) {
    const chave = envio ? "envio" : "recebimento";
    if (!el(chave).hidden && !el(chave + "-cancelar").hidden)
      mostrarTransferencia({ envio, ativo: false, texto: "Transferência interrompida pela desconexão." });
  }
  arquivosAtivos = false;
  el("mostrar-arquivos").disabled = true;
  arquivosRemotos = false;
  canalArquivos = false;
}

function listarDownloads(arquivos) {
  for (const url of downloads.splice(0)) URL.revokeObjectURL(url);
  el("downloads-web").replaceChildren(...arquivos.map(({ nome, blob }) => {
    const item = document.createElement("li");
    const link = document.createElement("a");
    link.textContent = nome;
    link.download = nome;
    link.href = URL.createObjectURL(blob);
    downloads.push(link.href);
    item.append(link);
    return item;
  }));
}

if (conviteWeb) {
  el("arquivos-web").hidden = false;
  el("transferencias-vazio").textContent = "Selecione arquivos locais ou cole arquivos na área de transferência quando o navegador permitir.";
  el("selecionar-arquivos").onchange = ({ currentTarget }) => {
    arquivosWeb?.selecionar(currentTarget.files);
    currentTarget.value = "";
  };
  el("solicitar-arquivos").onclick = () => arquivosWeb?.solicitar();
  el("envio-cancelar").onclick = () => arquivosWeb?.cancelar(true);
  el("recebimento-cancelar").onclick = () => arquivosWeb?.cancelar(false);
  document.addEventListener("paste", (evento) => {
    if (!arquivosWeb || el("transferencias").hidden || !evento.clipboardData?.files.length) return;
    evento.preventDefault();
    arquivosWeb.selecionar(evento.clipboardData.files);
  });
}

function mostrarTransferencia(status) {
  const chave = status.envio ? "envio" : "recebimento";
  el("transferencias").hidden = false;
  el("transferencias-vazio").hidden = true;
  el("mostrar-arquivos").setAttribute("aria-expanded", "true");
  el(chave).hidden = false;
  el(chave + "-texto").textContent = status.texto;
  const progresso = el(chave + "-progresso");
  progresso.hidden = !status.ativo;
  if (status.total > 0) progresso.value = Math.min(100, 100 * status.bytes / status.total);
  else progresso.removeAttribute("value");
  el(chave + "-velocidade").textContent = status.ativo && status.velocidade > 0
    ? new Intl.NumberFormat("pt-BR", { maximumFractionDigits: 1 }).format(status.velocidade / 1048576) + " MiB/s" : "";
  el(chave + "-cancelar").hidden = !status.ativo;
  el("mostrar-arquivos").dataset.transferindo = ["envio", "recebimento"].some((k) => !el(k + "-cancelar").hidden && !el(k).hidden);
}

if (nativo) {
  nativo.addEventListener("message", ({ data }) => {
    if (data?.tipo === "arquivo" && arquivosAtivos && autorizada()) {
      if (data.mensagem?.tipo === "bloco") atividade = true;
      transporte?.enviarArquivo(data.mensagem);
    }
    if (data?.tipo === "progressoArquivo") {
      mostrarTransferencia(data.status);
      if (data.status.falha) diagnostico("transferencia_falhou");
    }
    if (data?.tipo === "vigenciaEncerrada" && data.sessao === sessao)
      encerrar("Não foi possível renovar a autorização do atendimento.");
  });
  el("envio-cancelar").onclick = () => ponte("cancelarArquivo", { envio: true });
  el("recebimento-cancelar").onclick = () => ponte("cancelarArquivo", { envio: false });
}

function mostrar(estado, mensagem) {
  fase = estado;
  if (conviteWeb && parent !== window)
    parent.postMessage({ tipo: "syncdesk-fase", id: conviteWeb, estado, mensagem }, location.origin);
  painel.estado(fase, socket?.readyState === WebSocket.OPEN);
  el("status").textContent = mensagem;
  el("codigo-cliente").hidden = tecnico || conviteNoNavegador || fase !== "pronto";
  el("consentimento").hidden = tecnico || fase !== "solicitado";
  el("conectar").hidden = !tecnico || Boolean(conviteWeb) || !["pronto", "offline"].includes(fase);
  el("conectar").querySelector("button").disabled = fase !== "pronto";
  el("encerrar").hidden = (tecnico && fase === "ativo") ||
    !["negociando", "ativo", "consultando", "aguardando"].includes(fase);
  el("area-remota").hidden = !tecnico || fase !== "ativo";
  el("atividade-cliente").hidden = tecnico || fase !== "ativo";
  el("reconectar").hidden = conviteNoNavegador || fase !== "offline";
}

function enviar(tipo, dados = {}) {
  if (socket?.readyState !== WebSocket.OPEN) return false;
  socket.send(JSON.stringify({ tipo, ...dados }));
  return true;
}

function pararTransporte() {
  clearInterval(vigencia?.timer);
  vigencia = undefined;
  clearInterval(tempoVideo);
  telemetriaVideo = undefined;
  semQuadros = 0;
  esperandoRede = false;
  avisoVideo("");
  atividade = false;
  videoConfirmado = false;
  painel.encerrar();
  prontoTransporte = false;
  dadosPendentes.clear();
  atualizandoVideo++;
  seletor = aguardandoMonitor = monitorPronto = quadroPublicado = undefined;
  el("abas-monitores").hidden = true;
  el("troca-monitor").hidden = true;
  el("autorizar-monitores").hidden = true;
  mostrarChat(false);
  el("mensagens-chat").replaceChildren();
  el("texto-chat").value = "";
  pararArquivos();
  controle?.permitir(false);
  controlePermitido = false;
  if (sessao || (!tecnico && fase === "negociando")) ponte("fim");
  transporte?.fechar();
  transporte = undefined;
  sessao = undefined;
  capturaNativa?.parar();
  capturaNativa = undefined;
  for (const track of captura?.getTracks() ?? []) track.stop();
  captura = undefined;
  el("tela").setAttribute("aria-label", "Tela compartilhada pelo cliente");
  el("tela").srcObject = null;
}

function iniciarVigencia(identificador) {
  const atual = vigencia = { instante: 0, vence: contadorTempo() + DuracaoAutorizacao, sequencia: 0,
    pedidos: new Map(), confirmada: false };
  const renovar = () => {
    if (vigencia !== atual) return;
    const agora = contadorTempo();
    if (agora >= atual.vence) {
      encerrar("Não foi possível renovar a autorização do atendimento.");
      return;
    }
    for (const [id, envio] of atual.pedidos) if (agora - envio > 2000) atual.pedidos.delete(id);
    const sequencia = ++atual.sequencia;
    if (enviar("renovar", { sessao: identificador, sequencia, atividade })) {
      atual.pedidos.set(sequencia, agora);
      atividade = false;
    }
  };
  atual.timer = setInterval(renovar, 1000);
  renovar();
}

function confirmarVigencia(evento) {
  const atual = vigencia;
  if (!atual || sessao !== evento.sessao) return;
  const envio = atual.pedidos.get(evento.sequencia);
  const agora = contadorTempo();
  if (envio === undefined || agora >= atual.vence || agora - envio > 2000 || envio < atual.instante) return;
  atual.pedidos.delete(evento.sequencia);
  atual.confirmada = true;
  atual.instante = envio;
  atual.vence = envio + DuracaoAutorizacao;
  ponte("vigencia", { restante: Math.floor(atual.vence - agora), horario: Date.now() });
  if (prontoTransporte && fase === "negociando") pronto();
}

function encerrar(mensagem) {
  const avisado = enviar("encerrar");
  pararTransporte();
  if (avisado) mostrar("finalizando", mensagem);
  else mostrar("offline", mensagem);
}

function falhaNoTransporte(mensagem) {
  diagnostico("transporte_falhou");
  const atual = socket;
  pararTransporte();
  mostrar("finalizando", mensagem);
  // O comando explícito do outro lado pode já estar a caminho na sinalização.
  setTimeout(() => {
    if (socket !== atual || fase !== "finalizando") return;
    if (!enviar("encerrar", { motivo: "transporte" })) desconectar(mensagem);
  }, 350);
}

function pronto() {
  mostrar("ativo", "Conexão WebRTC estabelecida");
  diagnostico("sessao_ativa");
  ponte("sessaoAtiva");
  el("transferencias").hidden = true;
  el("transferencias-vazio").hidden = false;
  el("mostrar-arquivos").dataset.transferindo = false;
  el("mostrar-arquivos").disabled = true;
  el("mostrar-arquivos").setAttribute("aria-expanded", "false");
  el("envio").hidden = true;
  el("recebimento").hidden = true;
  if (canalArquivos) transporte?.enviarArquivo({ tipo: "capacidade" });
  atualizarArquivos();
  if (tecnico) {
    escolherQualidade(modoQualidade);
    clearInterval(tempoVideo);
    tempoVideo = setInterval(() => void observarVideo(), 2000);
    el("computador-remoto").textContent = "Computador do cliente";
    el("controle-tecnico").textContent = "Visualizando a tela compartilhada.";
    for (const tipo of ["permissao", "monitores", "quadroMonitor"])
      if (dadosPendentes.has(tipo)) receberDados(dadosPendentes.get(tipo));
    dadosPendentes.clear();
    return;
  }
  el("controle-cliente").textContent = "O técnico pode ver a tela compartilhada.";
  if (!nativo) {
    publicarPermissao(false, conviteNoNavegador ? "Cliente no navegador" : "");
    return;
  }
  if (capturaNativa) {
    ponte("atendimentoNativo", { controle: el("autorizar-controle").checked });
    capturaNativa.iniciar();
    return;
  }
  const { displaySurface, width, height } = captura.getVideoTracks()[0].getSettings();
  ponte("atendimento", { superficie: displaySurface, largura: width, altura: height });
}

function receberDados(dados) {
  if (!dados || typeof dados !== "object") return;
  if (fase === "negociando" && tecnico && ["permissao", "monitores", "quadroMonitor"].includes(dados.tipo)) {
    dadosPendentes.set(dados.tipo, dados);
    return;
  }
  if (fase !== "ativo" || !autorizada()) return;
  if (tecnico && dados.tipo === "permissao") {
    controlePermitido = dados.controle === true;
    controle.permitir(controlePermitido && !esperandoRede && (!dados.capturaNativa || monitorPronto !== undefined));
    const computador = typeof dados.computador === "string" ? dados.computador.trim().slice(0, 80) : "";
    el("computador-remoto").textContent = computador || "Computador do cliente";
    if (conviteWeb && videoConfirmado) {
      enviar("visualizando", { computador });
    } else if (conviteWeb && !dados.capturaNativa) {
      const revisao = ++atualizandoVideo;
      el("tela").requestVideoFrameCallback(() => {
        if (revisao !== atualizandoVideo || fase !== "ativo" || !autorizada()) return;
        videoConfirmado = true;
        enviar("visualizando", { computador: el("computador-remoto").textContent });
      });
    }
    el("controle-tecnico").textContent = dados.controle === true
      ? "O cliente tem prioridade no mouse e no teclado. Clique no vídeo para digitar. Ctrl+Alt+Esc devolve o foco ao SyncDesk."
      : "Visualizando a tela. O cliente não está compartilhando o mouse e o teclado.";
  } else if (tecnico && dados.tipo === "monitores") {
    aplicarMonitores(dados);
  } else if (tecnico && dados.tipo === "quadroMonitor") {
    confirmarQuadro(dados.geracao);
  } else if (!tecnico && capturaNativa && dados.tipo === "selecionarMonitor" && typeof dados.id === "string") {
    quadroPublicado = undefined;
    ponte("selecionarMonitor", { id: dados.id });
  } else if (!tecnico && controlePermitido && !esperandoRede && dados.tipo === "entrada" && dados.evento) {
    if (dados.evento.acao !== "sinal" && dados.evento.acao !== "soltar") atividade = true;
    ponte("entrada", { evento: dados.evento, geracao: dados.geracao });
  } else if (dados.tipo === "chat" && typeof dados.mensagem === "string" && dados.mensagem.trim()) {
    adicionarMensagem(tecnico ? "Cliente" : "Técnico", dados.mensagem.slice(0, 240));
    mostrarChat(true);
  } else if (!tecnico && dados.tipo === "qualidadeVideo" && Object.hasOwn(ModosQualidade, dados.modo)) {
    transporte?.qualidade(dados.modo);
  } else if (!tecnico && dados.tipo === "retornoVideo") {
    transporte?.retornoVideo({
      perda: Number.isFinite(dados.perda) ? Math.max(0, Math.min(1, dados.perda)) : null,
      fps: Number.isFinite(dados.fps) ? Math.max(0, Math.min(120, dados.fps)) : null,
    });
  } else if (tecnico && dados.tipo === "telemetriaVideo") {
    telemetriaVideo = Object.fromEntries(["captura", "envio", "capturaMs", "cpu", "codificacao", "alvoFps"]
      .filter((campo) => Number.isFinite(dados[campo])).map((campo) => [campo, dados[campo]]));
    if (["DXGI", "GDI", "JPEG (compatibilidade)"].includes(dados.fonte)) telemetriaVideo.fonte = dados.fonte;
  }
}

async function receber(evento) {
  switch (evento.tipo) {
    case "registrado":
      codigo = evento.codigo;
      el("codigo").value = codigo.slice(0, 3) + " " + codigo.slice(3);
      mostrar("pronto", "Aguardando conexão");
      if (chaveInicial) {
        associarConvite(chaveInicial);
        chaveInicial = null;
      }
      break;
    case "conviteVinculado":
      mostrar("vinculado", "Convite vinculado. Aguardando o técnico.");
      break;
    case "conviteInvalido":
      if (conviteNoNavegador) finalizarConviteNoNavegador();
      else mostrar("pronto", "Convite inválido, expirado ou usado. Confira o código ou solicite um novo link.");
      break;
    case "conviteAguardando":
      mostrar("consultando", "Aguardando o cliente abrir o link.");
      break;
    case "solicitacao":
      atendimento = evento.atendimento;
      diagnostico("solicitacao");
      el("identidade").textContent = evento.tecnico ? "Técnico: " + evento.tecnico : "";
      el("autorizar-controle").checked = false;
      mostrar("solicitado", "Aguardando sua decisão");
      break;
    case "aguardando":
      atendimento = evento.atendimento;
      diagnostico("solicitacao");
      if (fase === "consultando") mostrar("aguardando", "Aguardando autorização do cliente");
      break;
    case "aceita": {
      if (fase !== "aguardando" && fase !== "negociando") return;
      const identificador = sessao = evento.sessao;
      atendimento = identificador;
      diagnostico("autorizado");
      const eventosArquivos = (nativo || conviteWeb) ? {
        abrir: () => {
          if (sessao !== identificador) return;
          canalArquivos = true;
          if (fase === "ativo") transporte?.enviarArquivo({ tipo: "capacidade" });
        },
        fechar: () => { if (sessao === identificador) pararArquivos(); },
        dados: (mensagem) => {
          if (sessao !== identificador || !mensagem || typeof mensagem !== "object") return;
          if (mensagem.tipo === "capacidade") {
            arquivosRemotos = true;
            atualizarArquivos();
          } else if (arquivosAtivos && autorizada()) {
            if (mensagem.tipo === "bloco") atividade = true;
            if (arquivosWeb) arquivosWeb.receber(mensagem);
            else ponte("arquivo", { mensagem });
          }
        },
      } : undefined;
      mostrar("negociando", "Estabelecendo conexão WebRTC…");
      transporte = new Transporte(
        evento.ice,
        (dados) => { if (sessao === identificador) enviar("sinal", { sessao, dados }); },
        () => {
          if (sessao !== identificador) return;
          prontoTransporte = true;
          if (autorizada()) pronto();
        },
        (mensagem) => { if (sessao === identificador) falhaNoTransporte(mensagem); },
        (dados) => { if (sessao === identificador) receberDados(dados); },
        el("tela"),
        captura,
        eventosArquivos, capturaNativa, recuperacaoVideo);
      iniciarVigencia(identificador);
      if (!tecnico) await transporte.iniciar();
      break;
    }
    case "autorizacao":
      confirmarVigencia(evento);
      break;
    case "sinal":
      if (sessao === evento.sessao) await transporte?.receber(evento.dados);
      break;
    case "encerrada":
      diagnostico(evento.motivo === "recusado" ? "recusado" : "sessao_encerrada");
      atendimento = undefined;
      pararTransporte();
      if (tecnico) mostrar("pronto", evento.mensagem);
      else if (conviteNoNavegador) finalizarConviteNoNavegador(evento.mensagem);
      else {
        mostrar("abrindo", evento.mensagem);
        socket.close();
        abrir();
      }
      break;
    case "erro":
      if (sessao) encerrar(evento.mensagem);
      else if (tecnico) mostrar("pronto", evento.mensagem);
      else desconectar(evento.mensagem);
      break;
  }
}

function finalizarConviteNoNavegador(mensagem = "") {
  const atual = socket;
  socket = undefined;
  atual?.close();
  mostrar("finalizado", [mensagem, "Solicite um novo link ao técnico para outro atendimento."].filter(Boolean).join(" "));
}

function desconectar(mensagem) {
  diagnostico("rede_perdida");
  pararTransporte();
  atendimento = undefined;
  const atual = socket;
  socket = undefined;
  atual?.close();
  codigo = undefined;
  el("codigo").value = "";
  el("credencial").value = "";
  if (conviteNoNavegador) finalizarConviteNoNavegador(mensagem);
  else mostrar("offline", mensagem);
}

function abrir() {
  const origemSinalizacao = nativo ? window.syncDeskServidor :
    (location.protocol === "http:" ? "ws://" : "wss://") + location.host;
  const atual = socket = new WebSocket(origemSinalizacao + "/ws/" + (conviteWeb ? "tecnico-web" : modo));
  codigo = undefined;
  el("codigo").value = "";
  let abriu = false;
  let fila = Promise.resolve();
  mostrar("abrindo", "Abrindo sinalização…");
  atual.onopen = () => {
    abriu = true;
    if (atual === socket) diagnostico("sinalizacao_conectada");
    if (atual === socket && conviteWeb) {
      mostrar("consultando", "Aguardando conexão do cliente…");
      enviar("acompanharConvite", { id: conviteWeb });
    } else if (atual === socket && tecnico) mostrar("pronto", "Aguardando código");
  };
  atual.onmessage = ({ data }) => {
    fila = fila.then(async () => {
      if (atual === socket) await receber(JSON.parse(data));
    }).catch(() => {
      if (atual === socket) encerrar("Não foi possível negociar a conexão.");
    });
  };
  atual.onclose = () => {
    if (atual !== socket) return;
    desconectar(abriu
      ? "Conexão com o servidor encerrada." + (conviteNoNavegador ? "" : " Reconecte para iniciar um novo atendimento.")
      : origemSinalizacao.startsWith("ws:")
        ? "O servidor de teste local não respondeu. Inicie o teste local e tente reconectar."
        : "Não foi possível conectar ao servidor. Confira a rede e o certificado HTTPS, depois tente reconectar.");
  };
}

function associarConvite(valor) {
  const chave = valor.trim().toLowerCase();
  if (!/^[0-9a-f]{48}$/.test(chave)) {
    mostrar("pronto", "Cole o código do convite exibido na página de atendimento.");
    return;
  }
  if (enviar("associarConvite", { chave })) mostrar("vinculando", "Verificando convite…");
}

el("associar-convite").onsubmit = (evento) => {
  evento.preventDefault();
  if (fase !== "pronto") return;
  const campo = el("codigo-convite");
  associarConvite(campo.value);
  campo.value = "";
};

el("conectar").addEventListener("submit", (evento) => {
  evento.preventDefault();
  if (fase !== "pronto") return;
  const codigo = el("codigo-tecnico").value.replace(/\s/g, "");
  if (!/^[0-9]{6}$/.test(codigo)) {
    mostrar("pronto", "Informe os seis dígitos do código do cliente.");
    return;
  }
  const pedido = testeLocal ? { codigo } : { codigo, credencial: el("credencial").value };
  if (enviar("solicitar", pedido)) {
    el("credencial").value = "";
    mostrar("consultando", "Enviando solicitação…");
  }
});

el("copiar").addEventListener("click", async () => {
  try {
    await navigator.clipboard.writeText(codigo);
    if (fase === "pronto") el("status").textContent = "Código copiado. Aguardando conexão";
  } catch {
    if (fase === "pronto") el("status").textContent = "Selecione e copie o código exibido.";
  }
});

el("recusar").addEventListener("click", () => {
  if (fase === "solicitado" && enviar("recusar")) mostrar("finalizando", "Recusando solicitação…");
});

el("aceitar").addEventListener("click", async () => {
  if (fase !== "solicitado") return;
  const atual = socket;
  mostrar("negociando", capturaWindows ? "Preparando compartilhamento…" : "Escolha uma tela ou janela para compartilhar.");
  try {
    if (capturaWindows) capturaNativa = new CapturaNativa((pedido) => ponte("capturar", pedido), (geracao) => {
      if (geracao !== quadroPublicado && transporte?.enviarDados({ tipo: "quadroMonitor", geracao }))
        quadroPublicado = geracao;
    }, nativo);
    const stream = capturaNativa?.stream ?? await navigator.mediaDevices.getDisplayMedia({
      audio: false, video: { displaySurface: "monitor", frameRate: { ideal: 18, max: 30 } }
    });
    if (atual !== socket || fase !== "negociando" || !enviar("aceitar")) {
      for (const track of stream.getTracks()) track.stop();
      return;
    }
    captura = stream;
    stream.getVideoTracks()[0].addEventListener("ended", () => {
      if (captura === stream) encerrar("Compartilhamento interrompido.");
    });
    mostrar("negociando", "Acertando a conexão com o técnico…");
  } catch {
    if (atual === socket && fase === "negociando") {
      enviar("recusar");
      mostrar("finalizando", "O compartilhamento de tela foi cancelado ou está indisponível.");
    }
  }
});

el("encerrar").addEventListener("click", () => encerrar("Encerrando atendimento…"));
el("encerrar-tecnico").addEventListener("click", () => encerrar("Encerrando atendimento…"));
el("reconectar").addEventListener("click", abrir);
window.addEventListener("offline", () => desconectar(conviteNoNavegador
  ? "Conexão de rede perdida." : "Conexão de rede perdida. Reconecte para iniciar um novo atendimento."));
window.addEventListener("pagehide", () => {
  el("credencial").value = "";
  pararTransporte();
  socket?.close();
});
if (conviteNoNavegador && !/^[0-9a-f]{48}$/.test(chaveInicial)) {
  mostrar("finalizado", "Abra o link de atendimento enviado pelo técnico.");
} else if (conviteNoNavegador && !navigator.mediaDevices?.getDisplayMedia) {
  mostrar("finalizado", "Abra o convite em um navegador de computador compatível com compartilhamento de tela.");
} else abrir();
