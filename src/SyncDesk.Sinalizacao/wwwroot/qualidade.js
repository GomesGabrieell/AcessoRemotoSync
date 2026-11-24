const perfis = [
  { largura: 960, fps: 12, bitrate: 550_000 },
  { largura: 1280, fps: 22, bitrate: 1_250_000 },
  { largura: 1920, fps: 30, bitrate: 2_700_000 },
  { largura: 2560, fps: 45, bitrate: 5_500_000 },
  { largura: 3840, fps: 60, bitrate: 10_000_000 },
];

export const ModosQualidade = Object.freeze({
  automatica: "Qualidade automática", maxima: "Máxima qualidade", equilibrada: "Equilibrada",
  desempenho: "Alto desempenho", economia: "Economia de dados",
});

export function nivelParaBanda(banda) {
  if (!Number.isFinite(banda) || banda <= 0) return undefined;
  return banda < 850_000 ? 0 : banda < 1_900_000 ? 1 : banda < 3_700_000 ? 2 : banda < 8_000_000 ? 3 : 4;
}

export function fatorDeResolucao(largura, altura, limite) {
  return Math.max(1, Math.max(largura, altura) / limite);
}

export function ajustarNivel(nivel, boa, { banda, perda, rtt, cpu, limitada, fpsRecebido, fpsCaptura, movimento }) {
  const alvo = nivelParaBanda(banda);
  const movimentoAlto = movimento > .16;
  const lenta = movimentoAlto && (rtt >= 60 || perda >= .01) &&
    Number.isFinite(fpsRecebido) && fpsCaptura >= 15 && fpsRecebido < fpsCaptura * .55;
  if (perda >= .04 || (perda >= .015 && rtt >= 275))
    return { nivel: Math.max(0, Math.min(nivel - 1, alvo ?? nivel)), boa: 0 };
  if (cpu || lenta || (limitada && alvo !== undefined && alvo < nivel))
    return { nivel: Math.max(0, nivel - 1), boa: 0 };
  if (movimentoAlto && alvo !== undefined && alvo < nivel)
    return { nivel: alvo, boa: 0 };
  if ((alvo === undefined || alvo >= nivel) && (perda ?? 0) < .02 && (rtt ?? 0) < 300) {
    if (boa >= 2) return { nivel: Math.min(4, nivel + 1), boa: 0 };
    return { nivel, boa: boa + 1 };
  }
  return { nivel, boa: 0 };
}

export function perfilPara(modo, nivel, movimento, nitidez = false) {
  const teto = modo === "economia" ? 1 : modo === "equilibrada" ? 2 : 4;
  const base = perfis[Math.min(teto, nivel)];
  const preservar = nitidez && modo !== "economia" && modo !== "desempenho";
  const resolucao = preservar ? Math.max(1920, base.largura) : base.largura;
  const bitrate = preservar ? Math.max(perfis[2].bitrate, base.bitrate) : base.bitrate;
  const fluidez = modo === "desempenho" || (modo === "automatica" && movimento > .16);
  if (fluidez && nivel >= 2) return {
    ...base, bitrate, largura: preservar || modo === "automatica" || nivel > 2 ? 1920 : 1280,
    fps: nivel === 2 ? 40 : 60, fluidez,
  };
  return { ...base, largura: resolucao, bitrate, fps: modo === "maxima" ? Math.min(base.fps, 30) : base.fps, fluidez };
}

export class QualidadeAutomatica {
  #par;
  #sender;
  #track;
  #origem;
  #publicar;
  #timer;
  #ocupado = false;
  #fechado = false;
  #nivel = 2;
  #boa = 0;
  #aplicado = "";
  #modo = "automatica";
  #feedback;
  #anterior;
  #movimento = 0;
  #nitidez = false;
  #redeEsgotada = 0;

  constructor(par, sender, track, origem, publicar) {
    this.#par = par;
    this.#sender = sender;
    this.#track = track;
    this.#origem = origem;
    this.#publicar = publicar;
    track.contentHint = "detail";
    this.#timer = setInterval(() => void this.#atualizar(), 2000);
  }

  configurar(modo) {
    if (!Object.hasOwn(ModosQualidade, modo)) return;
    this.#modo = modo;
    void this.#aplicar();
  }

  receber(dados) {
    this.#feedback = { ...dados, instante: performance.now() };
  }

  async #aplicar() {
    if (this.#fechado || this.#par.connectionState !== "connected") return;
    const perfil = perfilPara(this.#modo, this.#nivel, this.#movimento, this.#nitidez);
    const { width: largura = 1920, height: altura = 1080 } = this.#track.getSettings();
    const escala = fatorDeResolucao(largura, altura, perfil.largura);
    const preferencia = this.#modo === "maxima" || (this.#nitidez && this.#modo === "automatica")
      ? "maintain-resolution" : perfil.fluidez ? "maintain-framerate" : "balanced";
    const assinatura = [this.#modo, perfil.fps, perfil.bitrate, preferencia, escala].join(":");
    this.#origem?.configurar(perfil.fps);
    if (assinatura === this.#aplicado) return;
    const parametros = this.#sender.getParameters();
    const encoding = parametros.encodings?.[0];
    if (!encoding) return;
    try {
      this.#track.contentHint = perfil.fluidez ? "motion" : "detail";
      parametros.degradationPreference = preferencia;
      encoding.maxFramerate = perfil.fps;
      encoding.maxBitrate = perfil.bitrate;
      encoding.scaleResolutionDownBy = escala;
      await this.#sender.setParameters(parametros);
      this.#aplicado = assinatura;
    } catch { /* O WebRTC mantém seu controle de congestionamento. */ }
  }

  async #atualizar() {
    if (this.#fechado || this.#ocupado || this.#par.connectionState !== "connected") return;
    this.#ocupado = true;
    try {
      const stats = await this.#par.getStats();
      if (this.#fechado) return;
      let transporte, video, remoto;
      for (const s of stats.values()) {
        if (s.type === "transport" && s.selectedCandidatePairId) transporte = s;
        if (s.type === "outbound-rtp" && s.kind === "video" && s.active !== false) video = s;
        if (s.type === "remote-inbound-rtp" && s.kind === "video") remoto = s;
      }
      const gelo = transporte && stats.get(transporte.selectedCandidatePairId);
      const fonte = this.#origem?.amostrar();
      this.#movimento = fonte?.movimento ?? 1;
      const feedback = this.#feedback && performance.now() - this.#feedback.instante < 6500 ? this.#feedback : {};
      const perda = feedback.perda ?? remoto?.fractionLost;
      const rtt = (gelo?.currentRoundTripTime ?? remoto?.roundTripTime) * 1000;
      const limitada = video?.qualityLimitationReason === "bandwidth";
      this.#redeEsgotada = limitada && gelo?.availableOutgoingBitrate < 1_100_000 ? this.#redeEsgotada + 1 : 0;
      this.#nitidez = rtt < 80 && (perda ?? 0) < .015 && this.#redeEsgotada < 2;
      const estado = ajustarNivel(this.#nivel, this.#boa, {
        banda: gelo?.availableOutgoingBitrate, perda, rtt,
        cpu: video?.qualityLimitationReason === "cpu" || (fonte?.ms > 27 && this.#movimento > .16),
        limitada,
        fpsRecebido: feedback.fps, fpsCaptura: fonte?.fps ?? video?.framesPerSecond, movimento: this.#movimento,
      });
      this.#nivel = estado.nivel;
      this.#boa = estado.boa;
      await this.#aplicar();
      if (this.#fechado) return;
      const anterior = this.#anterior?.id === video?.id ? this.#anterior : null;
      const delta = video && anterior ? video.framesEncoded - anterior.framesEncoded : 0;
      const codificacao = delta > 0 ? 1000 * (video.totalEncodeTime - anterior.totalEncodeTime) / delta : undefined;
      this.#anterior = video;
      const perfil = perfilPara(this.#modo, this.#nivel, this.#movimento, this.#nitidez);
      this.#publicar({ tipo: "telemetriaVideo", captura: fonte?.fps, envio: video?.framesPerSecond,
        fonte: fonte?.fonte, capturaMs: fonte?.ms, cpu: fonte?.cpu, codificacao, alvoFps: perfil.fps, modo: this.#modo });
    } catch { /* A próxima amostra pode recuperar a leitura. */ }
    finally { this.#ocupado = false; }
  }

  fechar() {
    this.#fechado = true;
    clearInterval(this.#timer);
  }
}
