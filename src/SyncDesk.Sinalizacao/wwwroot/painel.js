const el = (id) => document.getElementById(id);
const formataData = new Intl.DateTimeFormat("pt-BR", { dateStyle: "short", timeStyle: "short" });

export function prepararPainel(tecnico, nativo, consultar) {
  document.body.dataset.modo = tecnico ? "tecnico" : "cliente";
  el("versao-painel").textContent = window.syncDeskVersao ?? "";
  let preferencias = window.syncDeskPreferencias ?? { tema: "sistema", historico: false };

  function aplicar(dados) {
    preferencias = dados;
    document.documentElement.dataset.tema = preferencias.tema;
    el("tema-painel").value = preferencias.tema;
    el("guardar-historico").checked = preferencias.historico;
    el("historico").hidden = !preferencias.historico;
    const itens = (preferencias.recentes ?? []).map((item) => {
      const li = document.createElement("li");
      li.textContent = formataData.format(new Date(item.data)) + " · " + Math.floor(item.segundos / 60) +
        " min " + (item.segundos % 60) + " s";
      return li;
    });
    el("historico-lista").replaceChildren(...itens);
    el("historico-vazio").hidden = itens.length > 0;
  }

  function configurar(tema, historico) {
    aplicar({ ...preferencias, tema, historico });
    if (tecnico && nativo) nativo.postMessage({ tipo: "preferencias", tema, historico });
  }

  aplicar(preferencias);
  el("preferencias").hidden = !tecnico;
  el("linha-historico").hidden = !(tecnico && nativo);
  el("tema-rapido").onclick = () => {
    const escuro = preferencias.tema === "escuro" ||
      (preferencias.tema === "sistema" && matchMedia("(prefers-color-scheme: dark)").matches);
    configurar(escuro ? "claro" : "escuro", preferencias.historico);
  };
  el("tema-painel").onchange = () => configurar(el("tema-painel").value, preferencias.historico);
  el("guardar-historico").onchange = () => configurar(preferencias.tema, el("guardar-historico").checked);
  nativo?.addEventListener("message", ({ data }) => {
    if (data?.tipo === "preferencias") aplicar(data.dados);
    if (data?.tipo === "memoria") el("metrica-memoria").textContent = Math.round(data.mib) + " MiB";
  });

  const video = el("tela");
  const viewport = el("viewport-video");
  video.addEventListener("resize", () => {
    viewport.style.setProperty("--video-largura", video.videoWidth + "px");
    viewport.style.setProperty("--video-altura", video.videoHeight + "px");
  });
  el("ajustar-video").onclick = () => {
    const original = viewport.dataset.original !== "true";
    viewport.dataset.original = original;
    el("ajustar-video").setAttribute("aria-pressed", original);
    el("ajustar-video").textContent = original ? "Ajustar à janela" : "Tamanho original";
    if (!original) viewport.scrollTo(0, 0);
  };
  el("mostrar-arquivos").onclick = () => {
    const area = el("transferencias");
    area.hidden = !area.hidden;
    el("mostrar-arquivos").setAttribute("aria-expanded", !area.hidden);
  };
  el("mostrar-metricas").onclick = () => {
    const metricas = el("metricas");
    metricas.open = metricas.hidden;
    metricas.hidden = !metricas.open;
    el("mostrar-metricas").setAttribute("aria-expanded", metricas.open);
    el("mostrar-metricas").closest("details").open = false;
  };
  el("tela-cheia").onclick = async () => {
    try {
      if (document.fullscreenElement) await document.exitFullscreen();
      else await document.querySelector("main").requestFullscreen();
    } catch {
      el("status").textContent = "Tela cheia indisponível nesta janela.";
    }
  };
  document.addEventListener("fullscreenchange", () => {
    const rotulo = document.fullscreenElement ? "Sair da tela cheia" : "Tela cheia";
    el("tela-cheia").title = rotulo;
    el("tela-cheia").setAttribute("aria-label", rotulo);
  });

  let timer;
  let relogio;
  let coletando = false;
  let revisao = 0;
  const metrica = (nome, valor, unidade = "") => {
    el("metrica-" + nome).textContent = typeof valor === "string" && valor ? valor :
      Number.isFinite(valor) ? new Intl.NumberFormat("pt-BR", { maximumFractionDigits: 1 }).format(valor) + unidade : "—";
  };
  const parar = () => { clearInterval(timer); revisao++; };
  el("metricas").addEventListener("toggle", () => {
    parar();
    if (!el("metricas").open) {
      el("metricas").hidden = true;
      el("mostrar-metricas").setAttribute("aria-expanded", false);
      return;
    }
    const atual = revisao;
    const coletar = async () => {
      if (coletando) return;
      coletando = true;
      try {
        const m = await consultar();
        if (!m || atual !== revisao) return;
        for (const [nome, unidade] of Object.entries({ rtt: " ms", fps: " FPS", render: " FPS", captura: " FPS",
          envio: " FPS", capturaMs: " ms", codificacao: " ms", cpu: "%", gpu: "%", descartados: "", congelamentos: "",
          resolucao: "", fonte: "", rota: "", codec: "", modo: "" })) metrica(nome, m[nome], unidade);
        metrica("bitrate", m.kbps, " kb/s");
        metrica("perda", Number.isFinite(m.perda) ? m.perda * 100 : null, "%");
      } finally { coletando = false; }
    };
    void coletar();
    timer = setInterval(coletar, 2000);
  });

  return {
    estado(fase, online) {
      const indicador = el("indicador-servidor");
      indicador.dataset.conexao = online ? "online" : fase === "offline" ? "offline" : "abrindo";
      indicador.textContent = online ? "Disponível" : fase === "offline" ? "Desconectado" : "Conectando";
      el("status").dataset.espera = ["abrindo", "consultando", "aguardando", "negociando", "finalizando"].includes(fase);
      document.body.dataset.remoto = tecnico && fase === "ativo";
      if (tecnico && fase === "ativo" && !relogio) {
        const inicio = performance.now();
        relogio = setInterval(() => {
          const segundos = Math.floor((performance.now() - inicio) / 1000);
          el("tempo-sessao").textContent = [Math.floor(segundos / 3600),
            Math.floor(segundos / 60) % 60, segundos % 60].map((parte) => String(parte).padStart(2, "0")).join(":");
        }, 1000);
      }
    },
    encerrar() {
      parar();
      clearInterval(relogio);
      relogio = undefined;
      el("tempo-sessao").textContent = "00:00:00";
      el("metricas").open = false;
      el("metricas").hidden = true;
      el("mostrar-metricas").setAttribute("aria-expanded", "false");
      for (const valor of el("metricas").querySelectorAll("dd")) valor.textContent = "—";
      viewport.dataset.original = false;
      el("ajustar-video").textContent = "Tamanho original";
      el("ajustar-video").setAttribute("aria-pressed", false);
      if (document.fullscreenElement) void document.exitFullscreen().catch(() => {});
    }
  };
}
