const el = (id) => document.getElementById(id);
const moldura = el("visualizador");
const estados = {
  aguardando: "Aguardando conexão", autorizacao: "Aguardando autorização", conectando: "Conectando",
  ativo: "Ativo", encerrado: "Encerrado", cancelado: "Cancelado", recusado: "Recusado",
  expirado: "Expirado", codigo: "Expirado", duracao: "Tempo máximo", inatividade: "Inatividade",
  conexao: "Conexão perdida", transporte: "Conexão interrompida",
};
const horario = new Intl.DateTimeFormat("pt-BR", { dateStyle: "short", timeStyle: "short" });
const tabelas = new WeakMap();
let dados;
let selecionado;
let estadoRemoto;
let mensagemRemota;
let atualizando = false;
let criando = false;
let revisao = 0;

async function api(rota, metodo = "GET", corpo) {
  const resposta = await fetch("/api/web" + rota, {
    method: metodo, headers: corpo === undefined ? {} : { "Content-Type": "application/json" },
    body: corpo === undefined ? undefined : JSON.stringify(corpo),
  });
  if (!resposta.ok) throw resposta.status;
  return resposta.status === 204 ? null : resposta.json();
}

function mensagem(texto = "") {
  el("retorno").textContent = texto;
}

function voltarAoPainel() {
  moldura.hidden = true;
  el("dashboard").hidden = !dados;
  if (location.hash) history.replaceState(null, "", location.pathname);
}

function desanexar() {
  selecionado = estadoRemoto = mensagemRemota = undefined;
  moldura.removeAttribute("src");
  voltarAoPainel();
}

function telaDeLogin() {
  dados = undefined;
  desanexar();
  el("entrada").hidden = false;
  el("dashboard").hidden = true;
}

async function atualizar() {
  if (atualizando) return;
  atualizando = true;
  const solicitado = revisao;
  try {
    const resultado = await api("/painel");
    if (solicitado !== revisao) return;
    dados = resultado;
    el("entrada").hidden = true;
    el("dashboard").hidden = estadoRemoto === "ativo";
    renderizar();
  } catch (erro) {
    if (solicitado !== revisao) return;
    if (erro === 401) telaDeLogin();
    else if (dados) mensagem("Não foi possível atualizar as sessões. Tentando novamente…");
    else {
      telaDeLogin();
      el("erro-login").textContent = "Não foi possível abrir o painel. Confira a conexão com o servidor.";
    }
  } finally { atualizando = false; }
}

el("login").onsubmit = async (evento) => {
  evento.preventDefault();
  const botao = evento.currentTarget.querySelector("button");
  botao.disabled = true;
  el("erro-login").textContent = "";
  try {
    await api("/entrar", "POST", { nome: el("usuario").value.trim(), credencial: el("senha").value });
    await atualizar();
  } catch (erro) {
    el("erro-login").textContent = erro === 429 ? "Muitas tentativas. Aguarde um minuto." :
      erro === 401 ? "Usuário ou credencial inválida." : "Não foi possível entrar no painel.";
  } finally {
    el("senha").value = "";
    botao.disabled = false;
  }
};

el("sair").onclick = async () => {
  try {
    await api("/sair", "POST");
    revisao++;
    telaDeLogin();
  } catch { mensagem("Não foi possível sair. Tente novamente."); }
};

el("recolher").onclick = () => {
  const fechou = el("dashboard").classList.toggle("recolhido");
  const rotulo = fechou ? "Expandir menu" : "Recolher menu";
  el("recolher").title = rotulo;
  el("recolher").setAttribute("aria-label", rotulo);
  el("recolher").setAttribute("aria-expanded", !fechou);
};

function selecionar(id) {
  if (id === selecionado && !["offline", "pronto"].includes(estadoRemoto)) return;
  if (["aguardando", "negociando", "ativo"].includes(estadoRemoto)) {
    mensagem("Encerre o atendimento atual antes de acompanhar outro convite.");
    return;
  }
  selecionado = id;
  estadoRemoto = "abrindo";
  mensagemRemota = "";
  moldura.src = "/sessao?id=" + encodeURIComponent(id);
  renderizar();
}

window.addEventListener("message", ({ origin, source, data }) => {
  if (origin !== location.origin || source !== moldura.contentWindow ||
      data?.tipo !== "syncdesk-fase" || data.id !== selecionado) return;
  const anterior = estadoRemoto;
  estadoRemoto = data.estado;
  mensagemRemota = data.mensagem;
  if (data.estado === "ativo") {
    el("dashboard").hidden = true;
    moldura.hidden = false;
    history.replaceState(null, "", "#sessao-" + data.id);
  } else if (anterior === "ativo" || ["pronto", "offline", "finalizando"].includes(data.estado)) {
    voltarAoPainel();
    if (data.estado !== "finalizando") mensagem(data.mensagem);
  }
  renderizar();
  if (["pronto", "offline"].includes(data.estado)) void atualizar();
});

async function criar() {
  if (criando || !dados) return;
  criando = true;
  el("novo-link").disabled = true;
  try {
    if (["aguardando", "negociando", "ativo"].includes(estadoRemoto)) {
      mensagem("Encerre o atendimento atual antes de gerar outro convite.");
      return;
    }
    const solicitado = revisao;
    const convite = await api("/convites", "POST", {});
    if (!dados || solicitado !== revisao) return;
    revisao++;
    dados.atuais.unshift(convite);
    selecionar(convite.registro.id);
    mensagem();
  } catch (erro) {
    if (erro === 401) telaDeLogin();
    else mensagem(erro === 429 ? "Limite de convites abertos atingido." : "Não foi possível gerar o link.");
  } finally {
    criando = false;
    el("novo-link").disabled = false;
  }
}

el("novo-link").onclick = criar;
el("copiar-link").onclick = async () => {
  const campo = el("link");
  if (!campo.value) return;
  try {
    await navigator.clipboard.writeText(campo.value);
    mensagem("Link copiado.");
  } catch {
    campo.select();
    mensagem("A área de transferência está indisponível. Copie o link selecionado.");
  }
};

function duracao(segundos) {
  if (!Number.isFinite(segundos)) return "—";
  const minutos = Math.floor(segundos / 60);
  return minutos < 60 ? minutos + " min " + Math.floor(segundos % 60) + " s" :
    Math.floor(minutos / 60) + " h " + minutos % 60 + " min";
}

function renderizar() {
  if (!dados) return;
  const { atuais, historico, tecnico } = dados;
  el("nome-tecnico").textContent = tecnico;

  const convite = atuais.find(({ registro }) => registro.id === selecionado);
  const url = convite?.url ? new URL(convite.url, location.origin).href : "";
  if (el("link").value !== url) el("link").value = url;
  el("copiar-link").disabled = !url;
  const status = el("status-convite");
  const texto = convite ? mensagemRemota || estados[convite.registro.status] : "";
  if (status.textContent !== texto) status.textContent = texto;
  status.hidden = !texto;

  atualizarTabela(el("ativos"), atuais.map(({ registro }) => registro), true, "Nenhum atendimento em andamento.");
  atualizarTabela(el("ultimos"), historico.slice(0, 12), false, "Nenhum atendimento registrado.");
}

function atualizarTabela(corpo, lista, permiteAcesso, mensagemVazio) {
  const assinatura = JSON.stringify(lista);
  if (assinatura === tabelas.get(corpo)) {
    for (const celula of corpo.querySelectorAll("[data-inicio]"))
      celula.textContent = duracao((Date.now() - Date.parse(celula.dataset.inicio)) / 1000);
    return;
  }
  tabelas.set(corpo, assinatura);
  corpo.replaceChildren(...lista.map((registro) => linha(registro, permiteAcesso)));
  if (!lista.length) {
    const vazio = corpo.insertRow().insertCell();
    vazio.colSpan = permiteAcesso ? 7 : 6;
    vazio.className = "vazio";
    vazio.textContent = mensagemVazio;
  }
}

function linha(registro, permiteAcesso) {
  const tr = document.createElement("tr");
  const { id, cliente, computador, status, inicio, fim } = registro;
  for (const valor of [cliente || "—", computador || "—", id.slice(0, 8).toUpperCase(), estados[status] ?? "Encerrado",
    inicio ? horario.format(new Date(inicio)) : "—", inicio ?
      duracao(((fim ? Date.parse(fim) : Date.now()) - Date.parse(inicio)) / 1000) : "—"]) {
    tr.insertCell().textContent = valor;
  }
  tr.cells[3].className = status === "ativo" ? "estado-ativo" : "";
  if (inicio && !fim) tr.cells[5].dataset.inicio = inicio;
  if (permiteAcesso) {
    const botao = document.createElement("button");
    botao.type = "button";
    botao.textContent = "Acessar";
    botao.onclick = () => selecionar(id);
    tr.insertCell().append(botao);
  }
  return tr;
}

if (location.hash.startsWith("#sessao-")) history.replaceState(null, "", location.pathname);
void atualizar();
setInterval(() => { if (dados) void atualizar(); }, 2000);
