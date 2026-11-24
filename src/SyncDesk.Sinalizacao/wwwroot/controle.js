export function pontoNoVideo(rect, largura, altura, clientX, clientY, arrastando = false) {
  if (largura <= 0 || altura <= 0 || rect.width <= 0 || rect.height <= 0) return null;
  const escala = Math.min(rect.width / largura, rect.height / altura);
  const w = largura * escala;
  const h = altura * escala;
  const x = (clientX - rect.left - (rect.width - w) / 2) / w;
  const y = (clientY - rect.top - (rect.height - h) / 2) / h;
  if (!arrastando && (x < 0 || x > 1 || y < 0 || y > 1)) return null;
  return { x: Math.max(0, Math.min(1, x)), y: Math.max(0, Math.min(1, y)) };
}

export class ControleDoTecnico {
  #video;
  #enviar;
  #ativo = false;
  #pulso;
  #frame;
  #movimento;
  #botoes = new Set();

  constructor(video, enviar) {
    this.#video = video;
    this.#enviar = enviar;
    video.addEventListener("contextmenu", (evento) => {
      if (this.#ativo) evento.preventDefault();
    });
    video.addEventListener("pointermove", (evento) => this.#mover(evento));
    video.addEventListener("pointerdown", (evento) => this.#botao(evento, true));
    video.addEventListener("pointerup", (evento) => this.#botao(evento, false));
    video.addEventListener("pointercancel", () => this.#soltar());
    video.addEventListener("lostpointercapture", () => this.#soltar());
    video.addEventListener("wheel", (evento) => this.#rolar(evento), { passive: false });
    video.addEventListener("keydown", (evento) => this.#tecla(evento, true));
    video.addEventListener("keyup", (evento) => this.#tecla(evento, false));
    video.addEventListener("blur", () => this.#soltar());
    window.addEventListener("blur", () => this.#soltar());
  }

  permitir(ativo) {
    if (ativo === this.#ativo) return;
    if (!ativo) this.#soltar();
    this.#ativo = ativo;
    clearInterval(this.#pulso);
    this.#video.tabIndex = ativo ? 0 : -1;
    this.#video.classList.toggle("controlavel", ativo);
    if (ativo) {
      this.#enviar({ acao: "sinal" });
      this.#pulso = setInterval(() => this.#enviar({ acao: "sinal" }), 1000);
    }
  }

  #ponto(evento, arrastando = false) {
    return pontoNoVideo(this.#video.getBoundingClientRect(), this.#video.videoWidth, this.#video.videoHeight,
      evento.clientX, evento.clientY, arrastando);
  }

  #mover(evento) {
    if (!this.#ativo || evento.pointerType !== "mouse") return;
    this.#movimento = this.#ponto(evento, this.#botoes.size > 0);
    if (this.#frame) return;
    this.#frame = setTimeout(() => {
      this.#frame = undefined;
      if (this.#movimento) this.#enviar({ acao: "mover", ...this.#movimento });
    }, 16);
  }

  #botao(evento, pressionar) {
    if (!this.#ativo || evento.pointerType !== "mouse" || evento.button < 0 || evento.button > 2) return;
    if (!pressionar && !this.#botoes.has(evento.button)) return;
    const ponto = this.#ponto(evento, !pressionar || this.#botoes.size > 0);
    if (!ponto) return;
    evento.preventDefault();
    this.#movimento = undefined;
    this.#enviar({ acao: "botao", ...ponto, botao: evento.button, pressionar });
    if (pressionar) {
      this.#video.focus({ preventScroll: true });
      this.#video.setPointerCapture(evento.pointerId);
      this.#botoes.add(evento.button);
    } else {
      this.#botoes.delete(evento.button);
    }
  }

  #rolar(evento) {
    if (!this.#ativo) return;
    const ponto = this.#ponto(evento);
    if (!ponto) return;
    evento.preventDefault();
    const fator = evento.deltaMode === 1 ? 40 : evento.deltaMode === 2 ? 120 : 3;
    this.#enviar({ acao: "rolar", ...ponto, dx: Math.round(evento.deltaX * fator),
      dy: Math.round(evento.deltaY * fator) });
  }

  #tecla(evento, pressionar) {
    if (!this.#ativo || !evento.code || evento.isComposing) return;
    if (evento.code === "Escape" && document.fullscreenElement) return;
    evento.preventDefault();
    if (evento.code === "Escape" && evento.ctrlKey && evento.altKey) {
      this.#video.blur();
      return;
    }
    this.#enviar({ acao: "tecla", codigo: evento.code, pressionar });
  }

  #soltar() {
    this.#botoes.clear();
    this.#movimento = undefined;
    if (this.#ativo) this.#enviar({ acao: "soltar" });
  }
}
