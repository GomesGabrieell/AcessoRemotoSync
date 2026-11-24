export class CapturaNativa {
  #tela = document.createElement("canvas");
  #contexto;
  #pedir;
  #timer;
  #ativa = false;
  #geracao = -1;
  #pronto;
  #binario;
  #webview;
  #buffer;
  #pedido = 0;
  #inicioPedido = 0;
  #ultimoQuadro = 0;
  #fps = 30;
  #quadros = 0;
  #instanteAmostra = performance.now();
  #fonte;
  #ms;
  #cpu;
  #movimento = 0;
  #cinza;
  #ultimaComparacao = 0;
  #mini = document.createElement("canvas");
  #writer;

  constructor(pedir, pronto, webview) {
    this.#pedir = pedir;
    this.#pronto = pronto;
    this.#webview = webview;
    this.#binario = typeof VideoFrame === "function" && typeof webview?.releaseBuffer === "function";
    if (this.#binario) webview.addEventListener("sharedbufferreceived", this.#receberBuffer);
    this.#tela.width = 960;
    this.#tela.height = 540;
    this.#contexto = this.#tela.getContext("2d", { alpha: false });
    this.#contexto.fillRect(0, 0, 960, 540);
    this.#mini.width = 24;
    this.#mini.height = 14;
    if (this.#binario && typeof MediaStreamTrackGenerator === "function") {
      try {
        const trilha = new MediaStreamTrackGenerator({ kind: "video" });
        this.#writer = trilha.writable.getWriter();
        this.stream = new MediaStream([trilha]);
      } catch { /* Runtimes sem gerador mantêm a trilha Canvas. */ }
    }
    this.stream ??= this.#tela.captureStream(0);
    this.#quadroEscuro();
  }

  iniciar() {
    this.#ativa = true;
    this.#solicitar();
  }

  #solicitar() {
    this.#inicioPedido = performance.now();
    this.#timer = setTimeout(() => this.#solicitar(), 1200);
    this.#pedir({ pedido: ++this.#pedido, binario: this.#binario, repetir: this.#inicioPedido - this.#ultimoQuadro > 900 });
  }

  selecionar(geracao) {
    if (geracao === this.#geracao) return;
    this.#liberar();
    this.#geracao = geracao;
    this.#ultimoQuadro = this.#movimento = 0;
    this.#cinza = undefined;
    this.#contexto.fillRect(0, 0, this.#tela.width, this.#tela.height);
    this.#quadroEscuro();
  }

  #quadroEscuro() {
    if (!this.#writer) {
      this.stream.getVideoTracks()[0].requestFrame();
      return;
    }
    if (!(this.#writer.desiredSize > 0)) return;
    const frame = new VideoFrame(this.#tela, { timestamp: Math.round(performance.now() * 1000) });
    void this.#writer.write(frame).catch(() => {}).finally(() => frame.close());
  }

  #receberBuffer = (evento) => {
    const { geracao, largura, altura } = evento.additionalData ?? {};
    const buffer = evento.getBuffer();
    if (!this.#ativa || geracao !== this.#geracao || !Number.isInteger(largura) || !Number.isInteger(altura) ||
        largura <= 0 || altura <= 0 || buffer.byteLength !== largura * altura * 4) {
      this.#webview.releaseBuffer(buffer);
      return;
    }
    this.#liberar();
    this.#buffer = { buffer, largura, altura };
  };

  #liberar() {
    if (!this.#buffer) return;
    this.#webview.releaseBuffer(this.#buffer.buffer);
    this.#buffer = undefined;
  }

  async #desenhar(imagem, largura, altura, geracao) {
    let entrega;
    if (this.#writer) {
      if (!(this.#writer.desiredSize > 0)) return;
      this.#comparar(imagem);
      const frame = imagem instanceof VideoFrame ? imagem :
        new VideoFrame(imagem, { timestamp: Math.round(performance.now() * 1000) });
      entrega = this.#writer.write(frame);
      if (frame !== imagem) entrega = entrega.finally(() => frame.close());
    } else {
      if (this.#tela.width !== largura) this.#tela.width = largura;
      if (this.#tela.height !== altura) this.#tela.height = altura;
      this.#contexto.drawImage(imagem, 0, 0);
      this.stream.getVideoTracks()[0].requestFrame();
      this.#comparar(imagem);
    }
    if (entrega) await entrega;
    if (!this.#ativa || geracao !== this.#geracao) return;
    this.#ultimoQuadro = performance.now();
    this.#quadros++;
    this.#pronto(geracao);
  }

  #comparar(imagem) {
    const agora = performance.now();
    if (agora - this.#ultimaComparacao < 220) return;
    this.#ultimaComparacao = agora;
    const contexto = this.#mini.getContext("2d", { willReadFrequently: true });
    contexto.drawImage(imagem, 0, 0, 24, 14);
    const cinza = contexto.getImageData(0, 0, 24, 14).data;
    if (this.#cinza) {
      let alterados = 0;
      for (let i = 0; i < cinza.length; i += 4) {
        const diferenca = Math.abs(cinza[i] - this.#cinza[i]) + Math.abs(cinza[i + 1] - this.#cinza[i + 1]) +
          Math.abs(cinza[i + 2] - this.#cinza[i + 2]);
        if (diferenca > 45) alterados++;
      }
      this.#movimento = .45 * this.#movimento + .55 * (4 * alterados / cinza.length);
    }
    this.#cinza = cinza;
  }

  async quadro({ jpeg, pixels, fonte, capturaMs, compatibilidade, geracao, pedido, cpu }) {
    if (!this.#ativa || (pedido && pedido !== this.#pedido)) return;
    clearTimeout(this.#timer);
    try {
      if (compatibilidade) {
        this.#binario = false;
        this.#liberar();
      }
      if (this.#geracao !== geracao) return;
      if (Number.isFinite(cpu)) this.#cpu = cpu;
      if (Number.isFinite(capturaMs) && fonte) {
        this.#fonte = fonte;
        if (pixels) this.#ms = this.#ms === undefined ? capturaMs : .8 * this.#ms + .2 * capturaMs;
      }
      if (pixels && this.#buffer) {
        const { buffer, largura, altura } = this.#buffer;
        const frame = new VideoFrame(buffer, { format: "BGRX", codedWidth: largura, codedHeight: altura,
          timestamp: Math.round(performance.now() * 1000) });
        try { await this.#desenhar(frame, largura, altura, geracao); }
        finally { frame.close(); }
      } else if (jpeg) {
        const bytes = Uint8Array.from(atob(jpeg), (c) => c.charCodeAt(0));
        const imagem = await createImageBitmap(new Blob([bytes], { type: "image/jpeg" }));
        try {
          if (!this.#ativa || this.#geracao !== geracao) return;
          this.#fonte = "JPEG (compatibilidade)";
          await this.#desenhar(imagem, imagem.width, imagem.height, geracao);
        } finally { imagem.close(); }
      }
    } catch {
      if (pixels) {
        this.#binario = false;
        this.#liberar();
      }
    }
    finally {
      const intervalo = this.#binario ? 1000 / this.#fps : 82;
      if (this.#ativa) this.#timer = setTimeout(() => this.#solicitar(),
        Math.max(0, intervalo - (performance.now() - this.#inicioPedido)));
    }
  }

  configurar(fps) { this.#fps = fps; }

  amostrar() {
    const agora = performance.now();
    const fps = this.#quadros * 1000 / Math.max(1, agora - this.#instanteAmostra);
    this.#quadros = 0;
    this.#instanteAmostra = agora;
    return { fps, movimento: agora - this.#ultimoQuadro > 800 ? 0 : this.#movimento, fonte: this.#fonte, ms: this.#ms, cpu: this.#cpu };
  }

  parar() {
    this.#ativa = false;
    clearTimeout(this.#timer);
    this.#webview?.removeEventListener("sharedbufferreceived", this.#receberBuffer);
    this.#liberar();
    void this.#writer?.abort().catch(() => {});
    this.stream.getTracks().forEach((track) => track.stop());
  }
}
