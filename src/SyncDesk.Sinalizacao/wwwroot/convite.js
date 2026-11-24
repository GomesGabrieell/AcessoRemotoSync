const el = (id) => document.getElementById(id);
const chave = location.pathname.split("/").at(-1);
const modoWindows = new URLSearchParams(location.search).get("modo") === "windows";

async function preparar() {
  if (!/^[0-9a-f]{48}$/.test(chave)) {
    el("orientacao").textContent = "Este endereço de atendimento é inválido.";
    return;
  }
  try {
    const resposta = await fetch("/api/convite/" + chave);
    if (!resposta.ok) {
      el("orientacao").textContent = resposta.status === 404 ?
        "Este convite expirou, foi cancelado ou já foi usado. Solicite um novo link ao técnico." :
        "O convite está indisponível no momento. Atualize a página em instantes.";
      return;
    }
    const { download } = await resposta.json();
    const podeCompartilhar = !modoWindows && Boolean(navigator.mediaDevices?.getDisplayMedia);
    el("orientacao").textContent = podeCompartilhar && download
      ? "Escolha como deseja receber o suporte."
      : podeCompartilhar || download || modoWindows
        ? "Você recebeu um convite da equipe de suporte da Sync Softwares."
        : "Abra o convite em um navegador de computador compatível com compartilhamento de tela.";
    if (podeCompartilhar) {
      el("continuar").href = "/?modo=receber#" + chave;
      el("navegador").hidden = false;
    }
    if (download || modoWindows) {
      if (download) {
        el("baixar").href = location.pathname + "/baixar";
        el("baixar").classList.toggle("primario", !podeCompartilhar);
      } else {
        el("baixar").hidden = true;
        el("orientacao-windows").textContent = "O download está indisponível. Se já tiver o SyncDesk desta equipe, use o código abaixo.";
      }
      el("chave").value = chave;
      el("instrucoes").hidden = false;
    }
  } catch {
    el("orientacao").textContent = "Não foi possível consultar o convite. Confira a conexão e atualize a página.";
  }
}

el("copiar-chave").onclick = async () => {
  try {
    await navigator.clipboard.writeText(chave);
    el("copiar-chave").textContent = "Código copiado";
  } catch {
    el("chave").select();
  }
};
void preparar();
