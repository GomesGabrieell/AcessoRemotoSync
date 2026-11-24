import { QualidadeAutomatica } from "./qualidade.js";
import { amostrar } from "./metricas.js";

export class Transporte {
  #par;
  #canal;
  #candidatos = [];
  #recebeuPing = false;
  #prazo;
  #qualidade;
  #canalArquivos;
  #filaArquivos = [];
  #amostra;
  #metricas;
  #medindo;
  #instanteMetrica = -Infinity;
  #interrupcao;

  constructor(ice, enviar, pronto, falhou, dados, tela, captura, eventosArquivos, fonte, recuperacao) {
    const par = this.#par = new RTCPeerConnection(ice);
    this.#prazo = setTimeout(() => falhou("O WebRTC não conseguiu estabelecer a conexão."), 35_000);
    par.onicecandidate = ({ candidate }) => {
      if (candidate) enviar({ candidato: candidate.toJSON() });
    };
    par.onconnectionstatechange = () => {
      clearTimeout(this.#interrupcao);
      if (par.connectionState === "failed") {
        falhou("A conexão WebRTC foi interrompida. Inicie um novo atendimento.");
      } else if (par.connectionState === "disconnected") {
        recuperacao?.(true);
        this.#interrupcao = setTimeout(() => {
          if (par.connectionState === "disconnected") falhou("A conexão WebRTC foi interrompida. Inicie um novo atendimento.");
        }, 2500);
      } else if (par.connectionState === "connected") recuperacao?.(false);
    };
    par.ondatachannel = ({ channel }) => {
      if (channel.label === "syncdesk" && !this.#canal) this.#configurarCanal(channel, pronto, falhou, dados);
      else if (channel.label === "syncdesk-arquivos" && !this.#canalArquivos && eventosArquivos)
        this.#configurarArquivos(channel, eventosArquivos);
      else channel.close();
    };
    par.ontrack = ({ streams, track }) => {
      if (tela && track.kind === "video") tela.srcObject = streams[0] ?? new MediaStream([track]);
    };
    if (captura) {
      this.#configurarCanal(par.createDataChannel("syncdesk"), pronto, falhou, dados);
      if (eventosArquivos)
        this.#configurarArquivos(par.createDataChannel("syncdesk-arquivos"), eventosArquivos);
      for (const track of captura.getVideoTracks()) {
        const emissor = par.addTransceiver(track, { direction: "sendonly", streams: [captura] });
        try {
          const codecs = RTCRtpSender.getCapabilities("video")?.codecs ?? [];
          const h264 = codecs.filter((codec) => codec.mimeType.toLowerCase() === "video/h264");
          if (h264.length) emissor.setCodecPreferences([...h264, ...codecs.filter((codec) => !h264.includes(codec))]);
        } catch { /* A negociação padrão continua disponível. */ }
        this.#qualidade = new QualidadeAutomatica(par, emissor.sender, track, fonte,
          (metrica) => this.enviarDados(metrica));
      }
    }
    this.enviar = enviar;
    this.tela = tela;
  }

  async metricas() {
    if (this.#par.connectionState !== "connected") return null;
    if (performance.now() - this.#instanteMetrica < 750) return this.#metricas;
    this.#medindo ??= this.#lerMetricas().finally(() => { this.#medindo = undefined; });
    return this.#medindo;
  }

  async #lerMetricas() {
    try {
      const exibicao = this.tela?.getVideoPlaybackQuality?.();
      const renderizados = exibicao ? exibicao.totalVideoFrames - exibicao.droppedVideoFrames : undefined;
      const resultado = amostrar(await this.#par.getStats(), this.#amostra, renderizados);
      this.#amostra = resultado.amostra;
      this.#instanteMetrica = performance.now();
      this.#metricas = resultado;
      return resultado;
    } catch { return null; }
  }

  qualidade(modo) { this.#qualidade?.configurar(modo); }

  retornoVideo(dados) { this.#qualidade?.receber(dados); }

  #configurarArquivos(canal, eventos) {
    this.#canalArquivos = canal;
    canal.bufferedAmountLowThreshold = 16 * 1024;
    canal.onbufferedamountlow = () => this.#drenarArquivos();
    canal.onopen = eventos.abrir;
    canal.onclose = () => {
      this.#filaArquivos.length = 0;
      eventos.fechar();
    };
    canal.onerror = () => canal.close();
    canal.onmessage = ({ data }) => {
      if (typeof data !== "string" || data.length > 16 * 1024) { canal.close(); return; }
      try { eventos.dados(JSON.parse(data)); }
      catch { canal.close(); }
    };
    if (canal.readyState === "open") eventos.abrir();
  }

  enviarArquivo(dados) {
    const canal = this.#canalArquivos;
    if (canal?.readyState !== "open") return;
    const texto = JSON.stringify(dados);
    if (texto.length > 16 * 1024 || this.#filaArquivos.length >= 24) { canal.close(); return; }
    this.#filaArquivos.push(texto);
    this.#drenarArquivos();
  }

  #drenarArquivos() {
    const canal = this.#canalArquivos;
    if (canal?.readyState !== "open") return;
    try {
      while (this.#filaArquivos.length && canal.bufferedAmount + this.#filaArquivos[0].length <= 48 * 1024)
        canal.send(this.#filaArquivos.shift());
    }
    catch { canal.close(); }
  }

  #configurarCanal(canal, pronto, falhou, dados) {
    this.#canal = canal;
    const ping = () => canal.send("syncdesk:pronto");
    if (canal.readyState === "open") ping();
    else canal.onopen = ping;
    canal.onmessage = ({ data }) => {
      if (data === "syncdesk:pronto" && !this.#recebeuPing) {
        this.#recebeuPing = true;
        clearTimeout(this.#prazo);
        pronto();
      } else if (this.#recebeuPing && typeof data === "string" && data.length <= 1024) {
        try { dados(JSON.parse(data)); } catch { /* mensagem inválida */ }
      }
    };
    canal.onclose = () => falhou("O canal de dados foi fechado.");
  }

  enviarDados(dados) {
    if (this.#canal?.readyState !== "open") return false;
    if (dados.evento?.acao === "mover" && this.#canal.bufferedAmount > 1024) return false;
    if (this.#canal.bufferedAmount > 16 * 1024) {
      if (dados.evento?.acao !== "mover") this.#canal.close();
      return false;
    }
    const mensagem = JSON.stringify(dados);
    if (mensagem.length > 1024) return false;
    try {
      this.#canal.send(mensagem);
      return true;
    } catch { return false; }
  }

  async iniciar() {
    await this.#par.setLocalDescription(await this.#par.createOffer());
    this.enviar({ descricao: this.#par.localDescription.toJSON() });
  }

  async receber({ descricao, candidato }) {
    if (descricao) {
      await this.#par.setRemoteDescription(descricao);
      for (const fila of this.#candidatos) await this.#par.addIceCandidate(fila);
      this.#candidatos = [];
      if (descricao.type === "offer") {
        await this.#par.setLocalDescription(await this.#par.createAnswer());
        this.enviar({ descricao: this.#par.localDescription.toJSON() });
      }
    }
    if (candidato) {
      if (this.#par.remoteDescription) await this.#par.addIceCandidate(candidato);
      else this.#candidatos.push(candidato);
    }
  }

  fechar() {
    clearTimeout(this.#prazo);
    clearTimeout(this.#interrupcao);
    this.#filaArquivos.length = 0;
    this.#qualidade?.fechar();
    if (this.#canalArquivos) {
      this.#canalArquivos.onclose = null;
      this.#canalArquivos.close();
    }
    this.#par.onconnectionstatechange = null;
    if (this.#canal) {
      this.#canal.onclose = null;
      this.#canal.close();
    }
    this.#par.close();
  }
}
