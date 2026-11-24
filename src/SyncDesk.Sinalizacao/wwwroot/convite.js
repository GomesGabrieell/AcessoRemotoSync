const el = (id) => document.getElementById(id);
const chave = location.pathname.split("/").at(-1);

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
    el("orientacao").textContent = download ? "Você recebeu um convite da equipe de suporte da Sync Softwares." :
      "O download está indisponível. Se já tiver o SyncDesk desta equipe, use o código abaixo.";
    el("baixar").hidden = !download;
    el("baixar").href = location.pathname + "/baixar";
    el("chave").value = chave;
    el("instrucoes").hidden = false;
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
